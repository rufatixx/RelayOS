using System.Text;
using System.Text.Json;
using RelayOS.Core.Storage;

namespace RelayOS.DeliveryLab.Tests;

public sealed class DeliveryLabSessionTests
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NewSession_HasEmptyQueuesAndStableReadOnlySnapshot()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);

        var initial = await session.GetSnapshotAsync();
        var reloaded = await session.GetSnapshotAsync();

        Assert.NotEqual(Guid.Empty, initial.SessionId);
        Assert.Equal(StartTime, initial.SimulatedUtcNow);
        AssertFresh(initial);
        AssertSameState(initial, reloaded);
        Assert.NotEqual(directory.Path, session.DataDirectory);
        Assert.Equal(directory.Path, Directory.GetParent(session.DataDirectory)!.FullName);
        Assert.True(Directory.Exists(session.DataDirectory));
    }

    [Fact]
    public async Task QueueWhileOffline_PersistsCiphertextWithoutDeliveringOrLoggingPlaintext()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        const string message = "Private delivery note 7b30227f: meet at the north gate.";

        var queued = await session.ExecuteAsync(initial.SessionId, "queue", message);
        var offline = await session.ExecuteAsync(initial.SessionId, "offline");
        var offlineAgain = await session.ExecuteAsync(initial.SessionId, "offline");

        Assert.Equal("queued", queued.Status);
        Assert.NotNull(queued.PacketId);
        Assert.Equal(StartTime.AddHours(1), queued.ExpiresAtUtc);
        Assert.Equal(new RelayStoreStatistics(1, 0, 1), queued.Station);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), queued.Courier);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), queued.Desk);
        Assert.Equal(queued.PacketId, offline.PacketId);
        Assert.Equal(queued.PacketId, offlineAgain.PacketId);
        Assert.Equal(queued.Station, offlineAgain.Station);
        Assert.Equal(queued.Courier, offlineAgain.Courier);
        Assert.Equal(queued.Desk, offlineAgain.Desk);
        Assert.Null(offlineAgain.DeliveredMessage);
        Assert.Equal(0, offlineAgain.DeliveryCount);
        Assert.DoesNotContain(offlineAgain.Events, entry => entry.Contains(message, StringComparison.Ordinal));

        var queueDocuments = await ReadQueueDocumentsAsync(session.DataDirectory);
        Assert.NotEmpty(queueDocuments);
        Assert.All(queueDocuments.Values, json => Assert.DoesNotContain(message, json, StringComparison.Ordinal));
        Assert.Equal(1, CountPersistedPackets(queueDocuments.Values, queued.PacketId!.Value));
    }

    [Fact]
    public async Task DeliverBeforePickup_DoesNotCreateADirectStationToDeskPath()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        var queued = await session.ExecuteAsync(initial.SessionId, "queue", "Take the courier route.");

        var attempted = await session.ExecuteAsync(initial.SessionId, "deliver");

        Assert.Equal("queued", attempted.Status);
        Assert.Equal(queued.Station, attempted.Station);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), attempted.Courier);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), attempted.Desk);
        Assert.Null(attempted.DeliveredMessage);
        Assert.Equal(0, attempted.DeliveryCount);
    }

    [Fact]
    public async Task TwoHops_ReopenedCourierRetainsCiphertextAndDeskReceivesExactNote()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        const string message = "  Привет, RelayOS!\nDeliver this note intact. 📬  ";
        var queued = await session.ExecuteAsync(initial.SessionId, "queue", message);

        var carried = await session.ExecuteAsync(initial.SessionId, "pickup");
        var persistedBeforeReopen = await ReadQueueDocumentsAsync(session.DataDirectory);
        var reopened = await session.ExecuteAsync(initial.SessionId, "reopen");
        var persistedAfterReopen = await ReadQueueDocumentsAsync(session.DataDirectory);
        var delivered = await session.ExecuteAsync(initial.SessionId, "deliver");

        Assert.Equal("carried", carried.Status);
        Assert.Equal(queued.PacketId, carried.PacketId);
        Assert.Equal(new RelayStoreStatistics(1, 0, 1), carried.Station);
        Assert.Equal(new RelayStoreStatistics(1, 0, 1), carried.Courier);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), carried.Desk);
        Assert.Null(carried.DeliveredMessage);
        Assert.True(carried.CourierRejectedAsRecipient);
        Assert.Equal(2, CountPersistedPackets(persistedBeforeReopen.Values, queued.PacketId!.Value));
        Assert.Equal(carried.PacketId, reopened.PacketId);
        Assert.Equal(carried.Courier, reopened.Courier);
        Assert.Equal(1, reopened.CourierReopenCount);
        Assert.True(reopened.CourierRejectedAsRecipient);
        Assert.Equal(persistedBeforeReopen.Keys.Order(), persistedAfterReopen.Keys.Order());
        foreach (var (path, content) in persistedBeforeReopen)
        {
            Assert.Equal(content, persistedAfterReopen[path]);
        }

        Assert.Equal("delivered", delivered.Status);
        Assert.Equal(queued.PacketId, delivered.PacketId);
        Assert.Equal(message, delivered.DeliveredMessage);
        Assert.Equal(1, delivered.DeliveryCount);
        Assert.Equal(0, delivered.DuplicateCount);
        Assert.Equal(new RelayStoreStatistics(0, 1, 1), delivered.Desk);
        Assert.True(delivered.CourierRejectedAsRecipient);
        Assert.DoesNotContain(delivered.Events, entry => entry.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfflineAfterPickup_DoesNotKeepThePreviousLinkConnected()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        await session.ExecuteAsync(initial.SessionId, "queue", "Wait for the next contact.");
        var carried = await session.ExecuteAsync(initial.SessionId, "pickup");

        var offline = await session.ExecuteAsync(initial.SessionId, "offline");

        Assert.Equal("carried", offline.Status);
        Assert.Equal(carried.Station, offline.Station);
        Assert.Equal(carried.Courier, offline.Courier);
        Assert.Equal(carried.Desk, offline.Desk);
        Assert.Equal(carried.DuplicateCount, offline.DuplicateCount);
        Assert.Equal(0, offline.DeliveryCount);
        Assert.Null(offline.DeliveredMessage);
    }

    [Fact]
    public async Task RepeatedDelivery_IsDeduplicatedWithoutRepeatingRecipientDelivery()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        await session.ExecuteAsync(initial.SessionId, "queue", "Deliver only once.");
        await session.ExecuteAsync(initial.SessionId, "pickup");
        var firstDelivery = await session.ExecuteAsync(initial.SessionId, "deliver");

        var secondDelivery = await session.ExecuteAsync(initial.SessionId, "deliver");
        var thirdDelivery = await session.ExecuteAsync(initial.SessionId, "deliver");

        Assert.Equal(1, firstDelivery.DeliveryCount);
        Assert.Equal(1, secondDelivery.DeliveryCount);
        Assert.Equal(1, thirdDelivery.DeliveryCount);
        Assert.Equal(firstDelivery.DuplicateCount + 1, secondDelivery.DuplicateCount);
        Assert.Equal(firstDelivery.DuplicateCount + 2, thirdDelivery.DuplicateCount);
        Assert.Equal("delivered", thirdDelivery.Status);
        Assert.Equal(firstDelivery.PacketId, thirdDelivery.PacketId);
        Assert.Equal(firstDelivery.DeliveredMessage, thirdDelivery.DeliveredMessage);
        Assert.Equal(new RelayStoreStatistics(0, 1, 1), thirdDelivery.Desk);
    }

    [Fact]
    public async Task RepeatedPickup_DeduplicatesCourierQueueAndStillDeliversOnce()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        await session.ExecuteAsync(initial.SessionId, "queue", "A single carried packet.");
        var firstPickup = await session.ExecuteAsync(initial.SessionId, "pickup");

        var secondPickup = await session.ExecuteAsync(initial.SessionId, "pickup");
        var delivered = await session.ExecuteAsync(initial.SessionId, "deliver");

        Assert.Equal(firstPickup.PacketId, secondPickup.PacketId);
        Assert.Equal(new RelayStoreStatistics(1, 0, 1), secondPickup.Courier);
        Assert.Equal(1, delivered.DeliveryCount);
        Assert.Equal(new RelayStoreStatistics(0, 1, 1), delivered.Desk);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvanceBeyondTtl_PrunesUndeliveredCopiesAndPreventsDelivery(bool pickUpFirst)
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        var queued = await session.ExecuteAsync(initial.SessionId, "queue", "This note expires.");
        if (pickUpFirst)
        {
            await session.ExecuteAsync(initial.SessionId, "pickup");
        }

        var expired = await session.ExecuteAsync(initial.SessionId, "advance");
        await session.ExecuteAsync(initial.SessionId, "pickup");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteAsync(initial.SessionId, "reopen"));
        var attempted = await session.ExecuteAsync(initial.SessionId, "deliver");

        Assert.Equal(StartTime.AddMinutes(61), expired.SimulatedUtcNow);
        Assert.True(expired.ClockAdvanced);
        Assert.Equal(queued.ExpiresAtUtc, expired.ExpiresAtUtc);
        Assert.Equal(queued.PacketId, expired.PacketId);
        Assert.Equal("expired", expired.Status);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), expired.Station);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), expired.Courier);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), expired.Desk);
        Assert.Equal("expired", attempted.Status);
        Assert.Null(attempted.DeliveredMessage);
        Assert.Equal(0, attempted.DeliveryCount);
        Assert.Equal(0, CountPersistedPackets(
            (await ReadQueueDocumentsAsync(session.DataDirectory)).Values, queued.PacketId!.Value));
    }

    [Fact]
    public async Task AdvanceAfterDelivery_PreservesDeliveryHistoryWhilePruningQueues()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        await session.ExecuteAsync(initial.SessionId, "queue", "Already delivered.");
        await session.ExecuteAsync(initial.SessionId, "pickup");
        var delivered = await session.ExecuteAsync(initial.SessionId, "deliver");

        var advanced = await session.ExecuteAsync(initial.SessionId, "advance");
        var attempted = await session.ExecuteAsync(initial.SessionId, "deliver");

        Assert.Equal("delivered", advanced.Status);
        Assert.Equal(delivered.DeliveredMessage, advanced.DeliveredMessage);
        Assert.Equal(delivered.DeliveryCount, advanced.DeliveryCount);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), advanced.Station);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), advanced.Courier);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), advanced.Desk);
        Assert.Equal(1, attempted.DeliveryCount);
        Assert.Equal(delivered.DeliveredMessage, attempted.DeliveredMessage);
    }

    [Fact]
    public async Task Reset_ReplacesSessionAndClearsQueuesCountersAndPriorDelivery()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var dataDirectory = session.DataDirectory;
        var initial = await session.GetSnapshotAsync();
        var queued = await session.ExecuteAsync(initial.SessionId, "queue", "Previous session note.");
        await session.ExecuteAsync(initial.SessionId, "pickup");
        await session.ExecuteAsync(initial.SessionId, "reopen");
        await session.ExecuteAsync(initial.SessionId, "deliver");
        await session.ExecuteAsync(initial.SessionId, "deliver");
        await session.ExecuteAsync(initial.SessionId, "advance");

        var reset = await session.ExecuteAsync(initial.SessionId, "reset");
        var reloaded = await session.GetSnapshotAsync();

        Assert.NotEqual(initial.SessionId, reset.SessionId);
        Assert.Equal(StartTime, reset.SimulatedUtcNow);
        Assert.Equal(dataDirectory, session.DataDirectory);
        AssertFresh(reset);
        AssertSameState(reset, reloaded);
        Assert.Equal(0, CountPersistedPackets(
            (await ReadQueueDocumentsAsync(session.DataDirectory)).Values, queued.PacketId!.Value));

        var nextNote = await session.ExecuteAsync(reset.SessionId, "queue", "New session note.");
        Assert.Equal("queued", nextNote.Status);
        Assert.NotEqual(queued.PacketId, nextNote.PacketId);
        Assert.Equal(reset.SessionId, nextNote.SessionId);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("offline")]
    [InlineData("pickup")]
    [InlineData("reopen")]
    [InlineData("deliver")]
    [InlineData("advance")]
    [InlineData("reset")]
    public async Task StaleSessionAction_IsRejectedWithoutChangingTheNewSession(string action)
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var old = await session.GetSnapshotAsync();
        var current = await session.ExecuteAsync(old.SessionId, "reset");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteAsync(old.SessionId, action, "Stale tab note."));

        AssertSameState(current, await session.GetSnapshotAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task EmptyQueueMessage_IsRejectedWithoutChangingState(string? message)
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            session.ExecuteAsync(initial.SessionId, "queue", message));

        AssertSameState(initial, await session.GetSnapshotAsync());
    }

    [Theory]
    [InlineData("a", 1024)]
    [InlineData("📬", 256)]
    public async Task PayloadAtUtf8ByteLimit_IsAcceptedAndDeliveredIntact(string character, int repetitions)
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        var message = string.Concat(Enumerable.Repeat(character, repetitions));
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(message));

        await session.ExecuteAsync(initial.SessionId, "queue", message);
        await session.ExecuteAsync(initial.SessionId, "pickup");
        var delivered = await session.ExecuteAsync(initial.SessionId, "deliver");

        Assert.Equal(message, delivered.DeliveredMessage);
        Assert.Equal(1, delivered.DeliveryCount);
    }

    [Theory]
    [InlineData("a", 1025)]
    [InlineData("📬", 257)]
    public async Task PayloadAboveUtf8ByteLimit_IsRejectedWithoutChangingState(string character, int repetitions)
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        var message = string.Concat(Enumerable.Repeat(character, repetitions));
        Assert.True(Encoding.UTF8.GetByteCount(message) > 1024);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            session.ExecuteAsync(initial.SessionId, "queue", message));

        AssertSameState(initial, await session.GetSnapshotAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    [InlineData("QUEUE")]
    public async Task InvalidAction_IsRejectedWithoutChangingState(string action)
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            session.ExecuteAsync(initial.SessionId, action, "No mutation."));

        AssertSameState(initial, await session.GetSnapshotAsync());
    }

    [Fact]
    public async Task SecondQueueMessage_IsRejectedWithoutReplacingTheOriginal()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        var queued = await session.ExecuteAsync(initial.SessionId, "queue", "Original note.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteAsync(initial.SessionId, "queue", "Replacement note."));

        AssertSameState(queued, await session.GetSnapshotAsync());
        await session.ExecuteAsync(initial.SessionId, "pickup");
        var delivered = await session.ExecuteAsync(initial.SessionId, "deliver");
        Assert.Equal("Original note.", delivered.DeliveredMessage);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("pickup")]
    [InlineData("reopen")]
    [InlineData("deliver")]
    [InlineData("advance")]
    public async Task ContactOrClockActionWithoutANote_IsRejectedWithoutChangingState(string action)
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteAsync(initial.SessionId, action));

        AssertSameState(initial, await session.GetSnapshotAsync());
    }

    [Fact]
    public async Task ReopenBeforePickup_IsRejectedWithoutChangingState()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        var queued = await session.ExecuteAsync(initial.SessionId, "queue", "Not picked up yet.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteAsync(initial.SessionId, "reopen"));

        AssertSameState(queued, await session.GetSnapshotAsync());
    }

    [Fact]
    public async Task SecondClockAdvance_IsRejectedWithoutChangingState()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        await session.ExecuteAsync(initial.SessionId, "queue", "A bounded time simulation.");
        var advanced = await session.ExecuteAsync(initial.SessionId, "advance");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteAsync(initial.SessionId, "advance"));

        AssertSameState(advanced, await session.GetSnapshotAsync());
    }

    [Fact]
    public async Task SnapshotsDoNotExposeMutableSessionEventHistory()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        var originalEvents = initial.Events.ToArray();
        var queued = await session.ExecuteAsync(initial.SessionId, "queue", "Do not mutate history.");

        Assert.Equal(originalEvents, initial.Events);
        Assert.NotEqual(initial.Events.Count, queued.Events.Count);
        if (queued.Events is string[] mutableCopy)
        {
            mutableCopy[0] = "Mutated outside the session.";
        }

        Assert.DoesNotContain("Mutated outside the session.", (await session.GetSnapshotAsync()).Events);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("offline")]
    [InlineData("pickup")]
    [InlineData("reopen")]
    [InlineData("deliver")]
    [InlineData("advance")]
    [InlineData("reset")]
    public async Task PreCanceledAction_DoesNotMutateState(string action)
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ExecuteAsync(initial.SessionId, action, "Canceled note.", cancellation.Token));

        AssertSameState(initial, await session.GetSnapshotAsync());
    }

    [Fact]
    public async Task PreCanceledSnapshot_DoesNotPreventSubsequentAccess()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.GetSnapshotAsync(cancellation.Token));

        AssertSameState(initial, await session.GetSnapshotAsync());
    }

    [Fact]
    public async Task ConcurrentQueueRequests_OnlyOneNoteIsAccepted()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        var messages = Enumerable.Range(1, 8).Select(index => $"Concurrent note {index}.").ToArray();

        var outcomes = await Task.WhenAll(messages.Select(async message =>
        {
            try
            {
                return (Message: message, Error: (Exception?)null,
                    Snapshot: await session.ExecuteAsync(initial.SessionId, "queue", message));
            }
            catch (Exception error)
            {
                return (Message: message, Error: error, Snapshot: (DeliveryLabSnapshot?)null);
            }
        }));

        var accepted = Assert.Single(outcomes, outcome => outcome.Error is null);
        Assert.All(outcomes.Where(outcome => outcome.Error is not null), outcome =>
            Assert.IsType<InvalidOperationException>(outcome.Error));
        Assert.Equal(new RelayStoreStatistics(1, 0, 1), (await session.GetSnapshotAsync()).Station);
        await session.ExecuteAsync(initial.SessionId, "pickup");
        var delivered = await session.ExecuteAsync(initial.SessionId, "deliver");
        Assert.Equal(accepted.Message, delivered.DeliveredMessage);
        Assert.Equal(accepted.Snapshot!.PacketId, delivered.PacketId);
    }

    [Fact]
    public async Task ConcurrentResetAndOldSessionActions_CannotLeakIntoTheFreshSession()
    {
        using var directory = new TemporaryDirectory();
        await using var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        await session.ExecuteAsync(initial.SessionId, "queue", "Old concurrent note.");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new[] { "pickup", "reopen", "offline", "deliver", "reset", "pickup", "deliver" };
        var pending = actions.Select(async action =>
        {
            await start.Task;
            try
            {
                return (Action: action, Error: (Exception?)null,
                    Snapshot: await session.ExecuteAsync(initial.SessionId, action));
            }
            catch (Exception error)
            {
                return (Action: action, Error: error, Snapshot: (DeliveryLabSnapshot?)null);
            }
        }).ToArray();

        start.SetResult();
        var outcomes = await Task.WhenAll(pending);
        var reset = Assert.Single(outcomes, outcome => outcome.Action == "reset");

        Assert.Null(reset.Error);
        Assert.NotNull(reset.Snapshot);
        Assert.All(outcomes.Where(outcome => outcome.Error is not null), outcome =>
            Assert.IsType<InvalidOperationException>(outcome.Error));
        Assert.All(outcomes.Where(outcome => outcome.Action != "reset" && outcome.Error is null), outcome =>
            Assert.Equal(initial.SessionId, outcome.Snapshot!.SessionId));
        var current = await session.GetSnapshotAsync();
        Assert.Equal(reset.Snapshot.SessionId, current.SessionId);
        Assert.NotEqual(initial.SessionId, current.SessionId);
        AssertFresh(current);
        var freshNote = await session.ExecuteAsync(current.SessionId, "queue", "Fresh concurrent note.");
        Assert.Equal("queued", freshNote.Status);
        Assert.Equal(0, freshNote.DeliveryCount);
    }

    [Fact]
    public async Task SessionsUseSeparateDirectories_AndDisposeLeavesBaseDirectoryUntouched()
    {
        using var directory = new TemporaryDirectory();
        var sentinel = Path.Combine(directory.Path, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "Caller-owned data.");
        var first = new DeliveryLabSession(directory.Path, StartTime);
        var second = new DeliveryLabSession(directory.Path, StartTime);
        var firstPath = first.DataDirectory;
        var secondPath = second.DataDirectory;
        try
        {
            Assert.NotEqual(firstPath, secondPath);
            var initial = await first.GetSnapshotAsync();
            await first.ExecuteAsync(initial.SessionId, "queue", "First session only.");
            AssertFresh(await second.GetSnapshotAsync());

            await first.DisposeAsync();

            Assert.False(Directory.Exists(firstPath));
            Assert.True(Directory.Exists(secondPath));
            Assert.Equal("Caller-owned data.", await File.ReadAllTextAsync(sentinel));
            AssertFresh(await second.GetSnapshotAsync());
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        Assert.False(Directory.Exists(firstPath));
        Assert.False(Directory.Exists(secondPath));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task DisposedSession_RejectsFurtherAccessAndAllowsRepeatedDisposal()
    {
        using var directory = new TemporaryDirectory();
        var session = new DeliveryLabSession(directory.Path, StartTime);
        var initial = await session.GetSnapshotAsync();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetSnapshotAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.ExecuteAsync(initial.SessionId, "queue", "Already disposed."));
        await session.DisposeAsync();

        Assert.False(Directory.Exists(session.DataDirectory));
        Assert.True(Directory.Exists(directory.Path));
    }

    private static void AssertFresh(DeliveryLabSnapshot snapshot)
    {
        Assert.Equal("empty", snapshot.Status);
        Assert.Null(snapshot.PacketId);
        Assert.Null(snapshot.ExpiresAtUtc);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), snapshot.Station);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), snapshot.Courier);
        Assert.Equal(new RelayStoreStatistics(0, 0, 0), snapshot.Desk);
        Assert.Null(snapshot.DeliveredMessage);
        Assert.Equal(0, snapshot.DeliveryCount);
        Assert.Equal(0, snapshot.DuplicateCount);
        Assert.Equal(0, snapshot.CourierReopenCount);
        Assert.False(snapshot.CourierRejectedAsRecipient);
        Assert.False(snapshot.ClockAdvanced);
    }

    private static void AssertSameState(DeliveryLabSnapshot expected, DeliveryLabSnapshot actual)
    {
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.SimulatedUtcNow, actual.SimulatedUtcNow);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.PacketId, actual.PacketId);
        Assert.Equal(expected.ExpiresAtUtc, actual.ExpiresAtUtc);
        Assert.Equal(expected.Station, actual.Station);
        Assert.Equal(expected.Courier, actual.Courier);
        Assert.Equal(expected.Desk, actual.Desk);
        Assert.Equal(expected.DeliveredMessage, actual.DeliveredMessage);
        Assert.Equal(expected.DeliveryCount, actual.DeliveryCount);
        Assert.Equal(expected.DuplicateCount, actual.DuplicateCount);
        Assert.Equal(expected.CourierReopenCount, actual.CourierReopenCount);
        Assert.Equal(expected.CourierRejectedAsRecipient, actual.CourierRejectedAsRecipient);
        Assert.Equal(expected.ClockAdvanced, actual.ClockAdvanced);
        Assert.Equal(expected.Events, actual.Events);
    }

    private static async Task<Dictionary<string, string>> ReadQueueDocumentsAsync(string directory)
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            documents.Add(path, await File.ReadAllTextAsync(path));
        }

        return documents;
    }

    private static int CountPersistedPackets(IEnumerable<string> queueDocuments, Guid packetId)
    {
        var count = 0;
        foreach (var json in queueDocuments)
        {
            using var document = JsonDocument.Parse(json);
            foreach (var record in document.RootElement.GetProperty("records").EnumerateArray())
            {
                if (record.GetProperty("packet").GetProperty("packetId").GetGuid() == packetId)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
