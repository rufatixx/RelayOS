using System.Text.Json;
using System.Text.Json.Nodes;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;
using RelayOS.Core.Storage;

namespace RelayOS.Core.Tests;

public sealed class FileRelayPacketStoreTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Store_ReopenPreservesPendingPacketAndDeliveredState()
    {
        using var directory = new TemporaryDirectory();
        using var recipient = RelayIdentity.Create("recipient");
        var path = directory.File("queue.json");
        var clock = new ManualTimeProvider(TestNow);
        var packet = TestPackets.Create(
            recipient.PublicKey,
            clock,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RelayPriority.High,
            payload: "persist me");

        using (var first = new FileRelayPacketStore(path, clock))
        {
            Assert.Equal(RelayEnqueueResult.Added, await first.EnqueueAsync(packet));
        }

        using (var reopened = new FileRelayPacketStore(path, clock))
        {
            var pending = Assert.Single(await reopened.GetPendingAsync());
            RelayPacketAssertions.Equivalent(packet, pending);
            Assert.Equal(new RelayStoreStatistics(1, 0, 1), await reopened.GetStatisticsAsync());
            Assert.True(await reopened.MarkDeliveredAsync(packet.PacketId));
        }

        using var reopenedAgain = new FileRelayPacketStore(path, clock);
        Assert.Empty(await reopenedAgain.GetPendingAsync());
        Assert.Equal(new RelayStoreStatistics(0, 1, 1), await reopenedAgain.GetStatisticsAsync());
        Assert.False(await reopenedAgain.MarkDeliveredAsync(packet.PacketId));
    }

    [Fact]
    public async Task GetPending_OrdersByPriorityThenOldestCreationTime()
    {
        using var directory = new TemporaryDirectory();
        using var recipient = RelayIdentity.Create("recipient");
        var storeClock = new ManualTimeProvider(TestNow.AddMinutes(10));
        using var store = new FileRelayPacketStore(directory.File("queue.json"), storeClock);
        var packetClock = new ManualTimeProvider(TestNow);

        var highOlder = TestPackets.Create(
            recipient.PublicKey,
            packetClock,
            Guid.Parse("10000000-0000-0000-0000-000000000000"),
            RelayPriority.High,
            payload: "high older");
        packetClock.Advance(TimeSpan.FromMinutes(1));
        var low = TestPackets.Create(
            recipient.PublicKey,
            packetClock,
            Guid.Parse("20000000-0000-0000-0000-000000000000"),
            RelayPriority.Low,
            payload: "low");
        packetClock.Advance(TimeSpan.FromMinutes(1));
        var normal = TestPackets.Create(
            recipient.PublicKey,
            packetClock,
            Guid.Parse("30000000-0000-0000-0000-000000000000"),
            RelayPriority.Normal,
            payload: "normal");
        packetClock.Advance(TimeSpan.FromMinutes(1));
        var critical = TestPackets.Create(
            recipient.PublicKey,
            packetClock,
            Guid.Parse("40000000-0000-0000-0000-000000000000"),
            RelayPriority.Critical,
            payload: "critical");
        packetClock.Advance(TimeSpan.FromMinutes(1));
        var highNewer = TestPackets.Create(
            recipient.PublicKey,
            packetClock,
            Guid.Parse("50000000-0000-0000-0000-000000000000"),
            RelayPriority.High,
            payload: "high newer");

        foreach (var packet in new[] { low, highNewer, normal, critical, highOlder })
        {
            Assert.Equal(RelayEnqueueResult.Added, await store.EnqueueAsync(packet));
        }

        var pending = await store.GetPendingAsync();

        Assert.Equal(
            new[]
            {
                critical.PacketId,
                highOlder.PacketId,
                highNewer.PacketId,
                normal.PacketId,
                low.PacketId
            },
            pending.Select(packet => packet.PacketId));
        Assert.Equal(
            new[]
            {
                RelayPriority.Critical,
                RelayPriority.High,
                RelayPriority.High,
                RelayPriority.Normal,
                RelayPriority.Low
            },
            pending.Select(packet => packet.Priority));
    }

    [Fact]
    public async Task Store_UsesFakeClockForExactTtlBoundaryAndPruning()
    {
        using var directory = new TemporaryDirectory();
        using var recipient = RelayIdentity.Create("recipient");
        var clock = new ManualTimeProvider(TestNow);
        using var store = new FileRelayPacketStore(directory.File("queue.json"), clock);
        var expiring = TestPackets.Create(
            recipient.PublicKey,
            clock,
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000"),
            timeToLive: TimeSpan.FromMinutes(5),
            payload: "short lived");
        var surviving = TestPackets.Create(
            recipient.PublicKey,
            clock,
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000"),
            timeToLive: TimeSpan.FromMinutes(6),
            payload: "longer lived");

        Assert.Equal(RelayEnqueueResult.Added, await store.EnqueueAsync(expiring));
        Assert.Equal(RelayEnqueueResult.Added, await store.EnqueueAsync(surviving));
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.Equal(2, (await store.GetPendingAsync()).Count);

        clock.Advance(TimeSpan.FromTicks(1));

        Assert.Equal(1, await store.PruneExpiredAsync());
        Assert.Equal(surviving.PacketId, Assert.Single(await store.GetPendingAsync()).PacketId);
        Assert.Equal(RelayEnqueueResult.Expired, await store.EnqueueAsync(expiring));
        Assert.Equal(new RelayStoreStatistics(1, 0, 1), await store.GetStatisticsAsync());
    }

    [Fact]
    public async Task Enqueue_ExactPacketIsDuplicateButSameIdWithDifferentContentConflicts()
    {
        using var directory = new TemporaryDirectory();
        using var recipient = RelayIdentity.Create("recipient");
        var clock = new ManualTimeProvider(TestNow);
        using var store = new FileRelayPacketStore(directory.File("queue.json"), clock);
        var packetId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var packet = TestPackets.Create(recipient.PublicKey, clock, packetId);

        Assert.Equal(RelayEnqueueResult.Added, await store.EnqueueAsync(packet));
        Assert.Equal(RelayEnqueueResult.Duplicate, await store.EnqueueAsync(packet.Copy()));

        var conflicting = packet with { ContentType = "application/conflicting" };
        var exception = await Assert.ThrowsAsync<RelayPacketConflictException>(
            async () => await store.EnqueueAsync(conflicting));

        Assert.Equal(packetId, exception.PacketId);
        Assert.Equal(new RelayStoreStatistics(1, 0, 1), await store.GetStatisticsAsync());
    }

    [Fact]
    public async Task ConcurrentEnqueue_StoresEveryUniquePacketWithoutLoss()
    {
        using var directory = new TemporaryDirectory();
        using var recipient = RelayIdentity.Create("recipient");
        var clock = new ManualTimeProvider(TestNow);
        var path = directory.File("queue.json");
        var packets = Enumerable.Range(0, 48)
            .Select(index => TestPackets.Create(
                recipient.PublicKey,
                clock,
                priority: (RelayPriority)(index % 4),
                payload: $"payload-{index}"))
            .ToArray();

        using (var store = new FileRelayPacketStore(path, clock))
        {
            var results = await Task.WhenAll(
                packets.Select(packet => store.EnqueueAsync(packet).AsTask()));

            Assert.All(results, result => Assert.Equal(RelayEnqueueResult.Added, result));
            Assert.Equal(48, (await store.GetPendingAsync(maxCount: 100)).Count);
        }

        using var reopened = new FileRelayPacketStore(path, clock);
        Assert.Equal(48, (await reopened.GetPendingAsync(maxCount: 100)).Count);
    }

    [Fact]
    public async Task ConcurrentEnqueue_AtomicallyDeduplicatesSamePacket()
    {
        using var directory = new TemporaryDirectory();
        using var recipient = RelayIdentity.Create("recipient");
        var clock = new ManualTimeProvider(TestNow);
        using var store = new FileRelayPacketStore(directory.File("queue.json"), clock);
        var packet = TestPackets.Create(recipient.PublicKey, clock);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => store.EnqueueAsync(packet.Copy()).AsTask()));

        Assert.Equal(1, results.Count(result => result == RelayEnqueueResult.Added));
        Assert.Equal(31, results.Count(result => result == RelayEnqueueResult.Duplicate));
        Assert.Single(await store.GetPendingAsync());
    }

    [Fact]
    public async Task TwoStoreInstancesForSamePath_DoNotLoseConcurrentUpdates()
    {
        using var directory = new TemporaryDirectory();
        using var recipient = RelayIdentity.Create("recipient");
        var clock = new ManualTimeProvider(TestNow);
        var path = directory.File("queue.json");
        using var first = new FileRelayPacketStore(path, clock);
        using var second = new FileRelayPacketStore(path, clock);
        var firstPacket = TestPackets.Create(recipient.PublicKey, clock, payload: "first");
        var secondPacket = TestPackets.Create(recipient.PublicKey, clock, payload: "second");

        var results = await Task.WhenAll(
            first.EnqueueAsync(firstPacket).AsTask(),
            second.EnqueueAsync(secondPacket).AsTask());

        Assert.All(results, result => Assert.Equal(RelayEnqueueResult.Added, result));
        Assert.Equal(2, (await first.GetPendingAsync()).Count);
        Assert.Equal(2, (await second.GetPendingAsync()).Count);
    }

    [Fact]
    public async Task Reopen_MalformedJsonThrowsInvalidDataException()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("queue.json");
        await File.WriteAllTextAsync(path, "{ definitely-not-json");
        using var store = new FileRelayPacketStore(path, new ManualTimeProvider(TestNow));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.GetPendingAsync());

        Assert.Contains("corrupt or incomplete", exception.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task Reopen_PersistedPacketModifiedWithoutDigestUpdateThrowsInvalidDataException()
    {
        using var directory = new TemporaryDirectory();
        using var recipient = RelayIdentity.Create("recipient");
        var path = directory.File("queue.json");
        var clock = new ManualTimeProvider(TestNow);
        var packet = TestPackets.Create(recipient.PublicKey, clock, payload: "persisted secret");
        using (var store = new FileRelayPacketStore(path, clock))
        {
            Assert.Equal(RelayEnqueueResult.Added, await store.EnqueueAsync(packet));
        }

        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var persistedPacket = document["records"]!.AsArray()[0]!["packet"]!.AsObject();
        persistedPacket["ciphertext"] = Convert.ToBase64String(new byte[packet.Ciphertext.Length]);
        await File.WriteAllTextAsync(path, document.ToJsonString());

        using var reopened = new FileRelayPacketStore(path, clock);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reopened.GetPendingAsync());

        Assert.Contains(packet.PacketId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("integrity check", exception.Message, StringComparison.Ordinal);
    }
}
