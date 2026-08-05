using System.Text;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;
using RelayOS.Core.Storage;
using RelayOS.Core.Transport;

namespace RelayOS.Core.Tests;

public sealed class RelayNodeStoreCarryForwardTests
{
    [Fact]
    public async Task ThreeNodes_CourierPersistsPacketAcrossRestartAndDeliversOnSecondHop()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
        var hub = new LocalRelayHub();
        var senderTransport = hub.CreateTransport("sender");
        var courierTransport = hub.CreateTransport("courier");
        var recipientTransport = hub.CreateTransport("recipient");
        using var senderIdentity = RelayIdentity.Create("sender");
        using var courierIdentity = RelayIdentity.Create("courier");
        using var recipientIdentity = RelayIdentity.Create("recipient");
        using var senderStore = new FileRelayPacketStore(directory.File("sender.json"), clock);
        using var recipientStore = new FileRelayPacketStore(directory.File("recipient.json"), clock);
        var courierPath = directory.File("courier.json");
        var sender = new RelayNode(senderIdentity, senderStore, senderTransport, timeProvider: clock);
        var recipient = new RelayNode(
            recipientIdentity,
            recipientStore,
            recipientTransport,
            timeProvider: clock);
        var payload = Encoding.UTF8.GetBytes("store, carry, then forward");

        var outbound = await sender.SendAsync(
            recipientIdentity.PublicKey,
            payload,
            new RelaySendOptions
            {
                TimeToLive = TimeSpan.FromHours(2),
                Priority = RelayPriority.High,
                ContentType = "text/plain"
            });

        hub.Connect("sender", "courier");
        var firstHop = await sender.ForwardPendingAsync();
        Assert.Equal(new RelayForwardReport(1, 1, 0), firstHop);

        using (var courierStore = new FileRelayPacketStore(courierPath, clock))
        {
            var courier = new RelayNode(
                courierIdentity,
                courierStore,
                courierTransport,
                timeProvider: clock);
            var receivedByCourier = await courier.ReceiveAvailableAsync();

            Assert.Equal(1, receivedByCourier.InboundCount);
            Assert.Equal(1, receivedByCourier.AcceptedForRelay);
            Assert.Empty(receivedByCourier.Delivered);
            Assert.Equal(outbound.PacketId, Assert.Single(await courierStore.GetPendingAsync()).PacketId);
        }

        hub.DisconnectAll();
        using var reopenedCourierStore = new FileRelayPacketStore(courierPath, clock);
        Assert.Equal(outbound.PacketId, Assert.Single(await reopenedCourierStore.GetPendingAsync()).PacketId);
        var restartedCourier = new RelayNode(
            courierIdentity,
            reopenedCourierStore,
            courierTransport,
            timeProvider: clock);

        hub.Connect("courier", "recipient");
        var secondHop = await restartedCourier.ForwardPendingAsync();
        var receivedByRecipient = await recipient.ReceiveAvailableAsync();

        Assert.Equal(new RelayForwardReport(1, 1, 0), secondHop);
        Assert.Equal(1, receivedByRecipient.InboundCount);
        Assert.Equal(0, receivedByRecipient.AcceptedForRelay);
        Assert.Equal(0, receivedByRecipient.Duplicates);
        Assert.Equal(0, receivedByRecipient.Expired);
        Assert.Equal(0, receivedByRecipient.Invalid);
        var delivered = Assert.Single(receivedByRecipient.Delivered);
        Assert.Equal(outbound.PacketId, delivered.Packet.PacketId);
        Assert.Equal("courier", delivered.FromPeerId);
        Assert.Equal(payload, delivered.Payload);
        Assert.Equal(new RelayStoreStatistics(0, 1, 1), await recipientStore.GetStatisticsAsync());
    }

    [Fact]
    public async Task Recipient_RecoversPreviouslyPersistedLocalPacketWithoutNewInboundData()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
        using var recipientIdentity = RelayIdentity.Create("recipient");
        var packet = TestPackets.Create(
            recipientIdentity.PublicKey,
            clock,
            senderId: "sender",
            payload: "recover after restart");
        using var recipientStore = new FileRelayPacketStore(directory.File("recipient.json"), clock);
        Assert.Equal(RelayEnqueueResult.Added, await recipientStore.EnqueueAsync(packet));

        var hub = new LocalRelayHub();
        var recipient = new RelayNode(
            recipientIdentity,
            recipientStore,
            hub.CreateTransport("recipient"),
            timeProvider: clock);

        var report = await recipient.ReceiveAvailableAsync();

        Assert.Equal(0, report.InboundCount);
        Assert.Equal(0, report.Invalid);
        var delivered = Assert.Single(report.Delivered);
        Assert.Equal("local-queue", delivered.FromPeerId);
        Assert.Equal("recover after restart", Encoding.UTF8.GetString(delivered.Payload));
        Assert.Equal(new RelayStoreStatistics(0, 1, 1), await recipientStore.GetStatisticsAsync());
    }

    [Fact]
    public async Task InvalidDestinationPacket_DoesNotBlockFollowingValidPacket()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
        using var recipientIdentity = RelayIdentity.Create("recipient");
        using var recipientStore = new FileRelayPacketStore(directory.File("recipient.json"), clock);
        var hub = new LocalRelayHub();
        var senderTransport = hub.CreateTransport("sender");
        var recipientTransport = hub.CreateTransport("recipient");
        var recipient = new RelayNode(
            recipientIdentity,
            recipientStore,
            recipientTransport,
            timeProvider: clock);
        var valid = TestPackets.Create(
            recipientIdentity.PublicKey,
            clock,
            senderId: "sender",
            payload: "valid payload");
        var invalid = valid.Copy();
        invalid.AuthenticationTag[0] ^= 0x01;

        hub.Connect("sender", "recipient");
        await senderTransport.BroadcastAsync(invalid);
        await senderTransport.BroadcastAsync(valid);

        var report = await recipient.ReceiveAvailableAsync();

        Assert.Equal(2, report.InboundCount);
        Assert.Equal(1, report.Invalid);
        Assert.Equal(0, report.Duplicates);
        var delivered = Assert.Single(report.Delivered);
        Assert.Equal("valid payload", Encoding.UTF8.GetString(delivered.Payload));
        Assert.Equal(new RelayStoreStatistics(0, 1, 1), await recipientStore.GetStatisticsAsync());
    }
}
