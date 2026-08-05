using System.Security.Cryptography;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;
using RelayOS.Core.Storage;
using RelayOS.Core.Transport;

namespace RelayOS.Core;

public sealed class RelayNode
{
    private readonly RelayIdentity _identity;
    private readonly IRelayPacketStore _store;
    private readonly IRelayTransport _transport;
    private readonly RelayCryptography _cryptography;
    private readonly TimeProvider _timeProvider;

    public RelayNode(
        RelayIdentity identity,
        IRelayPacketStore store,
        IRelayTransport transport,
        RelayCryptography? cryptography = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);

        if (!string.Equals(identity.NodeId, transport.LocalNodeId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The identity and transport must use the same node ID.");
        }

        _identity = identity;
        _store = store;
        _transport = transport;
        _cryptography = cryptography ?? new RelayCryptography();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string NodeId => _identity.NodeId;

    public async ValueTask<RelayPacket> SendAsync(
        RelayPublicKey recipient,
        ReadOnlyMemory<byte> payload,
        RelaySendOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packet = _cryptography.Encrypt(
            NodeId,
            recipient,
            payload.Span,
            options,
            _timeProvider);
        var result = await _store.EnqueueAsync(packet, cancellationToken).ConfigureAwait(false);

        if (result != RelayEnqueueResult.Added)
        {
            throw new InvalidOperationException($"A newly created packet could not be queued: {result}.");
        }

        return packet.Copy();
    }

    public async ValueTask<RelayReceiveReport> ReceiveAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        var inboundPackets = await _transport.ReceiveAvailableAsync(cancellationToken).ConfigureAwait(false);
        var delivered = new List<RelayReceivedMessage>();
        var acceptedForRelay = 0;
        var duplicates = 0;
        var expired = 0;
        var invalid = 0;
        var inboundPeers = new Dictionary<Guid, string>();

        foreach (var inbound in inboundPackets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RelayPacketValidator.Validate(inbound.Packet);
                if (inbound.Packet.IsExpired(_timeProvider.GetUtcNow()))
                {
                    expired++;
                    continue;
                }

                var isLocalRecipient = string.Equals(
                    inbound.Packet.RecipientId,
                    NodeId,
                    StringComparison.Ordinal);
                if (isLocalRecipient)
                {
                    // Authenticate before accepting a destination packet into durable storage.
                    _ = _cryptography.Decrypt(_identity, inbound.Packet);
                    inboundPeers[inbound.Packet.PacketId] = inbound.FromPeerId;
                }

                var result = await _store.EnqueueAsync(inbound.Packet, cancellationToken).ConfigureAwait(false);
                switch (result)
                {
                    case RelayEnqueueResult.Expired:
                        expired++;
                        continue;
                    case RelayEnqueueResult.Duplicate:
                        duplicates++;
                        continue;
                    case RelayEnqueueResult.Added when !isLocalRecipient:
                        acceptedForRelay++;
                        continue;
                    case RelayEnqueueResult.Added:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception exception) when (IsRejectedPacket(exception))
            {
                invalid++;
            }
        }

        // Also recover destination packets persisted before an earlier process stopped.
        var pending = await _store.GetPendingAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        foreach (var packet in pending.Where(packet =>
                     string.Equals(packet.RecipientId, NodeId, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var plaintext = _cryptography.Decrypt(_identity, packet);
                if (!await _store.MarkDeliveredAsync(packet.PacketId, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                delivered.Add(new RelayReceivedMessage(
                    packet.Copy(),
                    plaintext,
                    inboundPeers.GetValueOrDefault(packet.PacketId, "local-queue")));
            }
            catch (Exception exception) when (IsRejectedPacket(exception))
            {
                invalid++;
            }
        }

        return new RelayReceiveReport(
            inboundPackets.Count,
            acceptedForRelay,
            duplicates,
            expired,
            invalid,
            delivered);
    }

    public async ValueTask<RelayForwardReport> ForwardPendingAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        await _store.PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
        var pending = await _store.GetPendingAsync(maxCount, cancellationToken).ConfigureAwait(false);
        var attempted = 0;
        var peerDeliveries = 0;
        var withoutPeers = 0;

        foreach (var packet in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (packet.IsExpired(_timeProvider.GetUtcNow()) ||
                string.Equals(packet.RecipientId, NodeId, StringComparison.Ordinal))
            {
                continue;
            }

            attempted++;
            var result = await _transport.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
            peerDeliveries += result.AcceptedPeerCount;
            if (result.AcceptedPeerCount == 0)
            {
                withoutPeers++;
            }
        }

        return new RelayForwardReport(attempted, peerDeliveries, withoutPeers);
    }

    private static bool IsRejectedPacket(Exception exception) =>
        exception is CryptographicException or ArgumentException or NotSupportedException or
            RelayPacketConflictException;
}

public sealed record RelayReceivedMessage(
    RelayPacket Packet,
    byte[] Payload,
    string FromPeerId);

public sealed record RelayReceiveReport(
    int InboundCount,
    int AcceptedForRelay,
    int Duplicates,
    int Expired,
    int Invalid,
    IReadOnlyList<RelayReceivedMessage> Delivered);

public sealed record RelayForwardReport(
    int PacketsAttempted,
    int PeerDeliveries,
    int PacketsWithoutPeers);
