using System.Security.Cryptography;
using System.Text;
using RelayOS.Core;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;
using RelayOS.Core.Storage;
using RelayOS.Core.Transport;

namespace RelayOS.DeliveryLab;

/// <summary>A bounded, single-note scenario. All nodes and contacts live in this process.</summary>
public sealed class DeliveryLabSession : IAsyncDisposable
{
    public const int MaximumMessageBytes = 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTimeOffset _startTime;
    private readonly LabClock _clock;
    private readonly RelayCryptography _cryptography = new();
    private readonly List<string> _events = [];
    private RelayIdentity _stationIdentity = null!;
    private RelayIdentity _courierIdentity = null!;
    private RelayIdentity _deskIdentity = null!;
    private FileRelayPacketStore _stationStore = null!;
    private FileRelayPacketStore _courierStore = null!;
    private FileRelayPacketStore _deskStore = null!;
    private LocalRelayHub _hub = null!;
    private LocalRelayTransport _courierTransport = null!;
    private RelayNode _station = null!;
    private RelayNode _courier = null!;
    private RelayNode _desk = null!;
    private Guid _sessionId;
    private RelayPacket? _packet;
    private string? _deliveredMessage;
    private int _deliveryCount;
    private int _duplicateCount;
    private int _courierReopenCount;
    private bool _courierRejectedAsRecipient;
    private bool _advanced;
    private bool _disposed;

    public DeliveryLabSession(string? baseDirectory = null, DateTimeOffset? startTime = null)
    {
        _startTime = (startTime ?? DateTimeOffset.UtcNow).ToUniversalTime();
        _clock = new LabClock(_startTime);
        DataDirectory = Path.Combine(
            baseDirectory ?? Path.Combine(Path.GetTempPath(), "relayos-delivery-lab"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        Initialize();
    }

    public string DataDirectory { get; }

    public async Task<DeliveryLabSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await SnapshotAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeliveryLabSnapshot> ExecuteAsync(
        Guid sessionId,
        string action,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sessionId != _sessionId)
            {
                throw new InvalidOperationException("This run was reset. Refresh the lab before taking another step.");
            }

            // Once an action is accepted, finish it even if its HTTP client disconnects.
            // A subsequent GET observes the result; reloads do not leave half a contact.
            switch (action)
            {
                case "queue":
                    await QueueAsync(message);
                    break;
                case "offline":
                    RequirePacket();
                    _hub.DisconnectAll();
                    var offline = await _station.ForwardPendingAsync();
                    AddEvent($"No contact: {offline.PeerDeliveries} peer transfers. The note stays queued until a later encounter or expiry.");
                    break;
                case "pickup":
                    RequirePacket();
                    await PickUpAsync();
                    break;
                case "reopen":
                    RequirePacket();
                    await ReopenCourierAsync();
                    break;
                case "deliver":
                    RequirePacket();
                    await DeliverAsync();
                    break;
                case "advance":
                    RequirePacket();
                    if (_advanced)
                    {
                        throw new InvalidOperationException("The clock was already advanced. Reset to try a new run.");
                    }
                    _clock.UtcNow += TimeSpan.FromMinutes(61);
                    _advanced = true;
                    var pruned = await _stationStore.PruneExpiredAsync()
                        + await _courierStore.PruneExpiredAsync()
                        + await _deskStore.PruneExpiredAsync();
                    AddEvent($"Simulated clock advanced 61 minutes; {pruned} expired queue records pruned. This does not erase a note already read.");
                    break;
                case "reset":
                    DisposeNodes();
                    foreach (var name in new[] { "station.json", "courier.json", "desk.json" })
                    {
                        File.Delete(Path.Combine(DataDirectory, name));
                    }
                    Initialize();
                    break;
                default:
                    throw new ArgumentException("Unknown lab action.", nameof(action));
            }

            return await SnapshotAsync();
        }
        finally
        {
            _hub.DisconnectAll();
            _gate.Release();
        }
    }

    private async Task QueueAsync(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || Encoding.UTF8.GetByteCount(message) > MaximumMessageBytes)
        {
            throw new ArgumentException($"Enter a note between 1 and {MaximumMessageBytes} UTF-8 bytes.", nameof(message));
        }
        if (_packet is not null)
        {
            throw new InvalidOperationException("This run already has a note. Reset before queuing another.");
        }

        _packet = await _station.SendAsync(
            _deskIdentity.PublicKey,
            Encoding.UTF8.GetBytes(message),
            new RelaySendOptions
            {
                TimeToLive = TimeSpan.FromHours(1),
                ContentType = "text/plain; charset=utf-8"
            });
        AddEvent("Field Station encrypted a note for Research Desk and wrote it to its file queue. No contact is connected.");
    }

    private async Task PickUpAsync()
    {
        _hub.DisconnectAll();
        _hub.Connect("field-station", "courier");
        await _station.ForwardPendingAsync();
        var received = await _courier.ReceiveAvailableAsync();
        var pending = await _courierStore.GetPendingAsync();
        if (pending.Count > 0)
        {
            CheckCourierIsRejected(pending[0]);
        }
        AddEvent($"Station → Courier contact: {received.AcceptedForRelay} stored, {received.Duplicates} duplicates ignored. Research Desk was not connected.");
    }

    private async Task ReopenCourierAsync()
    {
        var before = await _courierStore.GetPendingAsync();
        if (before.Count == 0)
        {
            throw new InvalidOperationException("Courier needs an unexpired queued note before its queue can be reopened.");
        }

        _courierStore.Dispose();
        _courierStore = CreateStore("courier.json");
        _courier = CreateNode(_courierIdentity, _courierStore, _courierTransport);
        var after = await _courierStore.GetPendingAsync();
        if (after.Count != 1 || after[0].PacketId != before[0].PacketId)
        {
            throw new InvalidOperationException("Courier queue did not preserve the expected packet.");
        }
        CheckCourierIsRejected(after[0]);
        _courierReopenCount++;
        AddEvent("Courier file queue closed and reopened: the same encrypted packet is still pending. Keys and process were not restarted.");
    }

    private void CheckCourierIsRejected(RelayPacket packet)
    {
        try
        {
            _ = _cryptography.Decrypt(_courierIdentity, packet);
        }
        catch (CryptographicException)
        {
            _courierRejectedAsRecipient = true;
            return;
        }
        throw new InvalidOperationException("Courier unexpectedly decrypted the Research Desk payload.");
    }

    private async Task DeliverAsync()
    {
        _hub.DisconnectAll();
        _hub.Connect("courier", "research-desk");
        await _courier.ForwardPendingAsync();
        var received = await _desk.ReceiveAvailableAsync();
        foreach (var delivered in received.Delivered)
        {
            _deliveredMessage = Encoding.UTF8.GetString(delivered.Payload);
            _deliveryCount++;
        }
        _duplicateCount += received.Duplicates;
        AddEvent($"Courier → Research Desk contact: {received.Delivered.Count} delivered, {received.Duplicates} duplicates ignored. Field Station was not connected.");
    }

    private async Task<DeliveryLabSnapshot> SnapshotAsync()
    {
        var station = await _stationStore.GetStatisticsAsync();
        var courier = await _courierStore.GetStatisticsAsync();
        var desk = await _deskStore.GetStatisticsAsync();
        var status = _deliveryCount > 0 ? "delivered"
            : _packet is null ? "empty"
            : _packet.IsExpired(_clock.GetUtcNow()) ? "expired"
            : courier.Pending > 0 ? "carried" : "queued";
        return new DeliveryLabSnapshot(
            _sessionId, _clock.GetUtcNow(), status, _packet?.PacketId, _packet?.ExpiresAtUtc,
            station, courier, desk, _deliveredMessage, _deliveryCount, _duplicateCount,
            _courierReopenCount, _courierRejectedAsRecipient, _advanced, _events.ToArray());
    }

    private void Initialize()
    {
        _sessionId = Guid.NewGuid();
        _packet = null;
        _deliveredMessage = null;
        _deliveryCount = _duplicateCount = _courierReopenCount = 0;
        _courierRejectedAsRecipient = _advanced = false;
        _clock.UtcNow = _startTime;
        _events.Clear();
        _hub = new LocalRelayHub();
        _stationIdentity = RelayIdentity.Create("field-station");
        _courierIdentity = RelayIdentity.Create("courier");
        _deskIdentity = RelayIdentity.Create("research-desk");
        _stationStore = CreateStore("station.json");
        _courierStore = CreateStore("courier.json");
        _deskStore = CreateStore("desk.json");
        _courierTransport = _hub.CreateTransport("courier");
        _station = CreateNode(_stationIdentity, _stationStore, _hub.CreateTransport("field-station"));
        _courier = CreateNode(_courierIdentity, _courierStore, _courierTransport);
        _desk = CreateNode(_deskIdentity, _deskStore, _hub.CreateTransport("research-desk"));
        AddEvent("Ready. Queue a sample field reading, then arrange two separate simulated encounters.");
    }

    private FileRelayPacketStore CreateStore(string name) => new(Path.Combine(DataDirectory, name), _clock);

    private RelayNode CreateNode(RelayIdentity identity, FileRelayPacketStore store, LocalRelayTransport transport) =>
        new(identity, store, transport, _cryptography, _clock);

    private void RequirePacket()
    {
        if (_packet is null)
        {
            throw new InvalidOperationException("Queue a note first.");
        }
    }

    private void AddEvent(string text)
    {
        if (_events.Count == 12)
        {
            _events.RemoveAt(0);
        }
        _events.Add(text);
    }

    private void DisposeNodes()
    {
        _stationStore.Dispose();
        _courierStore.Dispose();
        _deskStore.Dispose();
        _stationIdentity.Dispose();
        _courierIdentity.Dispose();
        _deskIdentity.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DisposeNodes();
            Directory.Delete(DataDirectory, recursive: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class LabClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

public sealed record DeliveryLabSnapshot(
    Guid SessionId,
    DateTimeOffset SimulatedUtcNow,
    string Status,
    Guid? PacketId,
    DateTimeOffset? ExpiresAtUtc,
    RelayStoreStatistics Station,
    RelayStoreStatistics Courier,
    RelayStoreStatistics Desk,
    string? DeliveredMessage,
    int DeliveryCount,
    int DuplicateCount,
    int CourierReopenCount,
    bool CourierRejectedAsRecipient,
    bool ClockAdvanced,
    IReadOnlyList<string> Events);
