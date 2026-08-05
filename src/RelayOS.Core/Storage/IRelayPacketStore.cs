using RelayOS.Core.Models;

namespace RelayOS.Core.Storage;

public interface IRelayPacketStore
{
    ValueTask<RelayEnqueueResult> EnqueueAsync(
        RelayPacket packet,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RelayPacket>> GetPendingAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default);

    ValueTask<bool> MarkDeliveredAsync(
        Guid packetId,
        CancellationToken cancellationToken = default);

    ValueTask<int> PruneExpiredAsync(CancellationToken cancellationToken = default);

    ValueTask<RelayStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

public enum RelayEnqueueResult
{
    Added,
    Duplicate,
    Expired
}

public sealed record RelayStoreStatistics(int Pending, int Delivered, int TotalSeen);
