namespace RelayOS.Core.Storage;

public sealed class RelayPacketConflictException : IOException
{
    public RelayPacketConflictException(Guid packetId)
        : base($"Packet {packetId} was already seen with different immutable content.")
    {
        PacketId = packetId;
    }

    public Guid PacketId { get; }
}
