using RelayOS.Core.Models;

namespace RelayOS.Core.Cryptography;

internal static class RelayPacketValidator
{
    public static void Validate(RelayPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.ProtocolVersion != RelayPacket.CurrentProtocolVersion)
        {
            throw new NotSupportedException($"Unsupported RelayOS protocol version {packet.ProtocolVersion}.");
        }

        if (packet.PacketId == Guid.Empty)
        {
            throw new ArgumentException("The packet ID cannot be empty.", nameof(packet.PacketId));
        }

        ValidateText(packet.SenderId, nameof(packet.SenderId), RelayProtocol.MaxNodeIdLength);
        ValidateText(packet.RecipientId, nameof(packet.RecipientId), RelayProtocol.MaxNodeIdLength);
        ValidateText(packet.ContentType, nameof(packet.ContentType), RelayProtocol.MaxContentTypeLength);

        if (packet.CreatedAtUtc.Offset != TimeSpan.Zero || packet.ExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Relay packet timestamps must use UTC offsets.");
        }

        var ttl = packet.ExpiresAtUtc - packet.CreatedAtUtc;
        if (ttl <= TimeSpan.Zero || ttl > RelayProtocol.MaxTimeToLive)
        {
            throw new ArgumentOutOfRangeException(nameof(packet.ExpiresAtUtc), "The packet TTL is outside protocol limits.");
        }

        if (!Enum.IsDefined(packet.Priority))
        {
            throw new ArgumentOutOfRangeException(nameof(packet.Priority));
        }

        if (packet.EphemeralPublicKey is not { Length: >= 32 and <= 1024 })
        {
            throw new ArgumentException("The ephemeral public key has an unexpected size.");
        }

        if (packet.KeyDerivationSalt is not { Length: RelayCryptography.SaltSizeBytes })
        {
            throw new ArgumentException("The key-derivation salt has an unexpected size.");
        }

        if (packet.Nonce is not { Length: RelayCryptography.NonceSizeBytes })
        {
            throw new ArgumentException("The AES-GCM nonce has an unexpected size.");
        }

        if (packet.AuthenticationTag is not { Length: RelayCryptography.TagSizeBytes })
        {
            throw new ArgumentException("The AES-GCM authentication tag has an unexpected size.");
        }

        if (packet.Ciphertext is null || packet.Ciphertext.Length > RelayProtocol.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(packet.Ciphertext));
        }
    }

    private static void ValidateText(string value, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }
    }
}
