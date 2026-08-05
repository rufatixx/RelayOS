using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;

namespace RelayOS.Core.Storage;

public sealed class FileRelayPacketStore : IRelayPacketStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SharedGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate;
    private bool _disposed;

    public FileRelayPacketStore(string filePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gate = SharedGates.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
    }

    public async ValueTask<RelayEnqueueResult> EnqueueAsync(
        RelayPacket packet,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RelayPacketValidator.Validate(packet);
        var candidate = packet.Copy();
        var digest = RelayPacketCodec.CalculateDigest(candidate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var records = document.Records
                .Where(record => !record.Packet.IsExpired(now))
                .Select(record => record.Copy())
                .ToList();

            var existing = records.SingleOrDefault(record => record.Packet.PacketId == candidate.PacketId);
            if (existing is not null)
            {
                if (!string.Equals(existing.Digest, digest, StringComparison.Ordinal))
                {
                    throw new RelayPacketConflictException(candidate.PacketId);
                }

                if (records.Count != document.Records.Count)
                {
                    await CommitAsync(new StoreDocument(CurrentSchemaVersion, records), cancellationToken)
                        .ConfigureAwait(false);
                }

                return RelayEnqueueResult.Duplicate;
            }

            if (candidate.IsExpired(now))
            {
                if (records.Count != document.Records.Count)
                {
                    await CommitAsync(new StoreDocument(CurrentSchemaVersion, records), cancellationToken)
                        .ConfigureAwait(false);
                }

                return RelayEnqueueResult.Expired;
            }

            records.Add(new StoredPacketRecord(
                candidate,
                digest,
                RelayStoredState.Pending,
                now));
            await CommitAsync(new StoreDocument(CurrentSchemaVersion, records), cancellationToken)
                .ConfigureAwait(false);
            return RelayEnqueueResult.Added;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RelayPacket>> GetPendingAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
            document = await PruneExpiredCoreAsync(document, cancellationToken).ConfigureAwait(false);

            return document.Records
                .Where(record => record.State == RelayStoredState.Pending)
                .OrderByDescending(record => record.Packet.Priority)
                .ThenBy(record => record.Packet.CreatedAtUtc)
                .ThenBy(record => record.Packet.PacketId)
                .Take(maxCount)
                .Select(record => record.Packet.Copy())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> MarkDeliveredAsync(
        Guid packetId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var document = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
            var index = document.Records.FindIndex(record => record.Packet.PacketId == packetId);
            if (index < 0 || document.Records[index].State == RelayStoredState.Delivered)
            {
                return false;
            }

            var records = document.Records.Select(record => record.Copy()).ToList();
            records[index] = records[index] with { State = RelayStoredState.Delivered };
            await CommitAsync(new StoreDocument(CurrentSchemaVersion, records), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var document = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
            var before = document.Records.Count;
            var updated = await PruneExpiredCoreAsync(document, cancellationToken).ConfigureAwait(false);
            return before - updated.Records.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RelayStoreStatistics> GetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var document = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
            document = await PruneExpiredCoreAsync(document, cancellationToken).ConfigureAwait(false);
            var pending = document.Records.Count(record => record.State == RelayStoredState.Pending);
            var delivered = document.Records.Count - pending;
            return new RelayStoreStatistics(pending, delivered, document.Records.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private async ValueTask<StoreDocument> GetDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new StoreDocument(CurrentSchemaVersion, []);
        }

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var loaded = await JsonSerializer.DeserializeAsync<StoreDocument>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (loaded is null || loaded.SchemaVersion != CurrentSchemaVersion || loaded.Records is null)
            {
                throw new InvalidDataException("The RelayOS queue has an unsupported or incomplete schema.");
            }

            var ids = new HashSet<Guid>();
            foreach (var record in loaded.Records)
            {
                if (record is null || record.Packet is null || !Enum.IsDefined(record.State))
                {
                    throw new InvalidDataException("The RelayOS queue contains an invalid record.");
                }

                RelayPacketValidator.Validate(record.Packet);
                if (!ids.Add(record.Packet.PacketId))
                {
                    throw new InvalidDataException("The RelayOS queue contains duplicate packet IDs.");
                }

                var expectedDigest = RelayPacketCodec.CalculateDigest(record.Packet);
                if (!string.Equals(expectedDigest, record.Digest, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The persisted packet {record.Packet.PacketId} failed its integrity check.");
                }
            }

            return loaded.Copy();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The RelayOS queue at '{_filePath}' is corrupt or incomplete.",
                exception);
        }
    }

    private async ValueTask<StoreDocument> PruneExpiredCoreAsync(
        StoreDocument document,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var records = document.Records
            .Where(record => !record.Packet.IsExpired(now))
            .Select(record => record.Copy())
            .ToList();

        if (records.Count == document.Records.Count)
        {
            return document;
        }

        var updated = new StoreDocument(CurrentSchemaVersion, records);
        await CommitAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async ValueTask CommitAsync(StoreDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The queue path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record StoreDocument(int SchemaVersion, List<StoredPacketRecord> Records)
    {
        public StoreDocument Copy() => new(
            SchemaVersion,
            Records.Select(record => record.Copy()).ToList());
    }

    private sealed record StoredPacketRecord(
        RelayPacket Packet,
        string Digest,
        RelayStoredState State,
        DateTimeOffset FirstSeenAtUtc)
    {
        public StoredPacketRecord Copy() => this with { Packet = Packet.Copy() };
    }

    private enum RelayStoredState
    {
        Pending,
        Delivered
    }
}
