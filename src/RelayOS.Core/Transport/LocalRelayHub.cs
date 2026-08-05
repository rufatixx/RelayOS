namespace RelayOS.Core.Transport;

public sealed class LocalRelayHub
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LocalRelayTransport> _transports =
        new(StringComparer.Ordinal);
    private readonly HashSet<PeerLink> _links = [];

    public LocalRelayTransport CreateTransport(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        lock (_gate)
        {
            if (_transports.ContainsKey(nodeId))
            {
                throw new InvalidOperationException($"A local transport for '{nodeId}' already exists.");
            }

            var transport = new LocalRelayTransport(this, nodeId);
            _transports.Add(nodeId, transport);
            return transport;
        }
    }

    public void Connect(string firstNodeId, string secondNodeId)
    {
        var link = PeerLink.Create(firstNodeId, secondNodeId);
        lock (_gate)
        {
            EnsureRegistered(link.First);
            EnsureRegistered(link.Second);
            _links.Add(link);
        }
    }

    public void Disconnect(string firstNodeId, string secondNodeId)
    {
        var link = PeerLink.Create(firstNodeId, secondNodeId);
        lock (_gate)
        {
            _links.Remove(link);
        }
    }

    public void DisconnectAll()
    {
        lock (_gate)
        {
            _links.Clear();
        }
    }

    internal IReadOnlyList<LocalRelayTransport> GetConnectedPeers(string nodeId)
    {
        lock (_gate)
        {
            EnsureRegistered(nodeId);
            var peerIds = _links
                .Where(link => link.Contains(nodeId))
                .Select(link => link.Other(nodeId))
                .Order(StringComparer.Ordinal)
                .ToArray();
            return peerIds.Select(peerId => _transports[peerId]).ToArray();
        }
    }

    private void EnsureRegistered(string nodeId)
    {
        if (!_transports.ContainsKey(nodeId))
        {
            throw new InvalidOperationException($"No local transport is registered for '{nodeId}'.");
        }
    }

    private readonly record struct PeerLink(string First, string Second)
    {
        public static PeerLink Create(string first, string second)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(first);
            ArgumentException.ThrowIfNullOrWhiteSpace(second);

            if (string.Equals(first, second, StringComparison.Ordinal))
            {
                throw new ArgumentException("A node cannot connect to itself.");
            }

            return string.Compare(first, second, StringComparison.Ordinal) < 0
                ? new PeerLink(first, second)
                : new PeerLink(second, first);
        }

        public bool Contains(string nodeId) =>
            string.Equals(First, nodeId, StringComparison.Ordinal) ||
            string.Equals(Second, nodeId, StringComparison.Ordinal);

        public string Other(string nodeId) =>
            string.Equals(First, nodeId, StringComparison.Ordinal) ? Second : First;
    }
}
