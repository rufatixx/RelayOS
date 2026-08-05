using RelayOS.Core.Models;

namespace RelayOS.Core.Transport;

public interface IRelayTransport
{
    string Name { get; }

    string LocalNodeId { get; }

    ValueTask<RelayTransportSendResult> BroadcastAsync(
        RelayPacket packet,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RelayInboundPacket>> ReceiveAvailableAsync(
        CancellationToken cancellationToken = default);
}

public sealed record RelayInboundPacket(string FromPeerId, RelayPacket Packet);

public sealed record RelayTransportSendResult(IReadOnlyList<string> AcceptedPeerIds)
{
    public int AcceptedPeerCount => AcceptedPeerIds.Count;
}
