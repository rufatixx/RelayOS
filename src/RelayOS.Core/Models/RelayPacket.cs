namespace RelayOS.Core.Models;

public sealed record RelayPacket
{
    public const int CurrentProtocolVersion = 1;

    public required int ProtocolVersion { get; init; }

    public required Guid PacketId { get; init; }

    public required string SenderId { get; init; }

    public required string RecipientId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required RelayPriority Priority { get; init; }

    public required string ContentType { get; init; }

    public required byte[] EphemeralPublicKey { get; init; }

    public required byte[] KeyDerivationSalt { get; init; }

    public required byte[] Nonce { get; init; }

    public required byte[] Ciphertext { get; init; }

    public required byte[] AuthenticationTag { get; init; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;

    public RelayPacket Copy() => this with
    {
        EphemeralPublicKey = (byte[])EphemeralPublicKey.Clone(),
        KeyDerivationSalt = (byte[])KeyDerivationSalt.Clone(),
        Nonce = (byte[])Nonce.Clone(),
        Ciphertext = (byte[])Ciphertext.Clone(),
        AuthenticationTag = (byte[])AuthenticationTag.Clone()
    };
}
