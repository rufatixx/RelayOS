using System.Security.Cryptography;
using System.Text;
using RelayOS.Core;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;
using RelayOS.Core.Storage;
using RelayOS.Core.Transport;

namespace RelayOS.Demo;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnRunDemoClicked(object? sender, EventArgs e)
    {
        RunDemoButton.IsEnabled = false;
        BusyIndicator.IsVisible = true;
        BusyIndicator.IsRunning = true;
        EventLogLabel.Text = "Starting…";

        try
        {
            EventLogLabel.Text = await RunDemoAsync(MessageEntry.Text ?? string.Empty);
        }
        catch (Exception exception)
        {
            EventLogLabel.Text = $"Demo failed: {exception.Message}";
        }
        finally
        {
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            RunDemoButton.IsEnabled = true;
        }
    }

    private static async Task<string> RunDemoAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Enter a message first.");
        }

        var sessionDirectory = Path.Combine(
            FileSystem.CacheDirectory,
            "relayos-demo",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);

        using var aliceIdentity = RelayIdentity.Create("alice");
        using var courierIdentity = RelayIdentity.Create("courier");
        using var bobIdentity = RelayIdentity.Create("bob");
        using var aliceStore = new FileRelayPacketStore(Path.Combine(sessionDirectory, "alice.json"));
        using var bobStore = new FileRelayPacketStore(Path.Combine(sessionDirectory, "bob.json"));

        var hub = new LocalRelayHub();
        var aliceTransport = hub.CreateTransport("alice");
        var courierTransport = hub.CreateTransport("courier");
        var bobTransport = hub.CreateTransport("bob");
        var cryptography = new RelayCryptography();
        var alice = new RelayNode(aliceIdentity, aliceStore, aliceTransport, cryptography);
        var bob = new RelayNode(bobIdentity, bobStore, bobTransport, cryptography);

        hub.Connect("alice", "courier");
        var packet = await alice.SendAsync(
            bobIdentity.PublicKey,
            Encoding.UTF8.GetBytes(message),
            new RelaySendOptions
            {
                TimeToLive = TimeSpan.FromHours(1),
                Priority = RelayPriority.High,
                ContentType = "text/plain; charset=utf-8"
            });
        var firstHop = await alice.ForwardPendingAsync();

        var courierQueuePath = Path.Combine(sessionDirectory, "courier.json");
        RelayReceiveReport courierReceive;
        using (var courierStore = new FileRelayPacketStore(courierQueuePath))
        {
            var courier = new RelayNode(courierIdentity, courierStore, courierTransport, cryptography);
            courierReceive = await courier.ReceiveAvailableAsync();

            var carriedPacket = (await courierStore.GetPendingAsync()).Single();
            try
            {
                _ = cryptography.Decrypt(courierIdentity, carriedPacket);
                throw new InvalidOperationException("Courier unexpectedly decrypted the packet.");
            }
            catch (CryptographicException)
            {
                // Expected: only Bob owns the destination private key.
            }
        }

        // Reopening the store simulates the courier app stopping while moving offline.
        hub.Disconnect("alice", "courier");
        hub.Connect("courier", "bob");
        using var reopenedCourierStore = new FileRelayPacketStore(courierQueuePath);
        var reopenedCourier = new RelayNode(
            courierIdentity,
            reopenedCourierStore,
            courierTransport,
            cryptography);
        var secondHop = await reopenedCourier.ForwardPendingAsync();
        var bobReceive = await bob.ReceiveAvailableAsync();
        var delivered = bobReceive.Delivered.Single();
        var deliveredText = Encoding.UTF8.GetString(delivered.Payload);

        return string.Join(
            Environment.NewLine,
            $"1. Encrypted {packet.Ciphertext.Length} bytes for Bob.",
            $"2. Alice → Courier: {firstHop.PeerDeliveries} peer delivery.",
            $"3. Courier stored {courierReceive.AcceptedForRelay} packet; decrypt denied ✓",
            "4. Courier queue closed and reopened from disk.",
            $"5. Courier → Bob: {secondHop.PeerDeliveries} peer delivery.",
            $"6. Bob decrypted: “{deliveredText}”",
            $"Packet: {packet.PacketId}");
    }
}
