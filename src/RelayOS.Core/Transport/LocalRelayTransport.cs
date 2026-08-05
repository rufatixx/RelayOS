using System.Collections.Concurrent;
using RelayOS.Core.Models;

namespace RelayOS.Core.Transport;

public sealed class LocalRelayTransport : IRelayTransport
{
    private readonly LocalRelayHub _hub;
    private readonly ConcurrentQueue<RelayInboundPacket> _inbox = new();

    internal LocalRelayTransport(LocalRelayHub hub, string localNodeId)
    {
        _hub = hub;
        LocalNodeId = localNodeId;
    }

    public string Name => "local-simulator";

    public string LocalNodeId { get; }

    public ValueTask<RelayTransportSendResult> BroadcastAsync(
        RelayPacket packet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        cancellationToken.ThrowIfCancellationRequested();

        var peers = _hub.GetConnectedPeers(LocalNodeId);
        foreach (var peer in peers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            peer._inbox.Enqueue(new RelayInboundPacket(LocalNodeId, packet.Copy()));
        }

        IReadOnlyList<string> acceptedPeerIds = peers.Select(peer => peer.LocalNodeId).ToArray();
        return ValueTask.FromResult(new RelayTransportSendResult(acceptedPeerIds));
    }

    public ValueTask<IReadOnlyList<RelayInboundPacket>> ReceiveAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        var received = new List<RelayInboundPacket>();
        while (_inbox.TryDequeue(out var inbound))
        {
            cancellationToken.ThrowIfCancellationRequested();
            received.Add(new RelayInboundPacket(inbound.FromPeerId, inbound.Packet.Copy()));
        }

        return ValueTask.FromResult<IReadOnlyList<RelayInboundPacket>>(received);
    }
}
