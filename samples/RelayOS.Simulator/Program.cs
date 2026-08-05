using System.Security.Cryptography;
using System.Text;
using RelayOS.Core;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;
using RelayOS.Core.Storage;
using RelayOS.Core.Transport;

var message = args.Length == 0
    ? "Hello through a disconnected relay"
    : string.Join(' ', args);
var sessionDirectory = Path.Combine(
    Path.GetTempPath(),
    "relayos-simulator",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sessionDirectory);

try
{
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

    var packet = await alice.SendAsync(
        bobIdentity.PublicKey,
        Encoding.UTF8.GetBytes(message),
        new RelaySendOptions
        {
            TimeToLive = TimeSpan.FromHours(1),
            Priority = RelayPriority.High,
            ContentType = "text/plain; charset=utf-8"
        });
    Console.WriteLine("ALICE  encrypted and queued a packet for Bob");

    hub.Connect("alice", "courier");
    await alice.ForwardPendingAsync();

    var courierQueuePath = Path.Combine(sessionDirectory, "courier.json");
    using (var courierStore = new FileRelayPacketStore(courierQueuePath))
    {
        var courier = new RelayNode(courierIdentity, courierStore, courierTransport, cryptography);
        await courier.ReceiveAvailableAsync();
        Console.WriteLine("HOP 1  Alice → Courier: ciphertext persisted");

        var carriedPacket = (await courierStore.GetPendingAsync()).Single();
        try
        {
            _ = cryptography.Decrypt(courierIdentity, carriedPacket);
            throw new InvalidOperationException("Courier unexpectedly decrypted the payload.");
        }
        catch (CryptographicException)
        {
            Console.WriteLine("CHECK  Courier cannot decrypt the payload ✓");
        }
    }

    hub.Disconnect("alice", "courier");
    hub.Connect("courier", "bob");
    using var reopenedCourierStore = new FileRelayPacketStore(courierQueuePath);
    var reopenedCourier = new RelayNode(
        courierIdentity,
        reopenedCourierStore,
        courierTransport,
        cryptography);
    await reopenedCourier.ForwardPendingAsync();
    Console.WriteLine("HOP 2  Courier → Bob: packet forwarded");

    var bobReceive = await bob.ReceiveAvailableAsync();
    var delivered = bobReceive.Delivered.Single();
    var deliveredText = Encoding.UTF8.GetString(delivered.Payload);
    Console.WriteLine($"BOB    decrypted: “{deliveredText}”");
    Console.WriteLine($"PACKET {packet.PacketId}");
}
finally
{
    if (Directory.Exists(sessionDirectory))
    {
        Directory.Delete(sessionDirectory, recursive: true);
    }
}
