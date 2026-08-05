using System.Security.Cryptography;
using RelayOS.Core.Models;

namespace RelayOS.Core.Cryptography;

public sealed class RelayCryptography
{
    public const int KeySizeBytes = 32;
    public const int SaltSizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    private static readonly byte[] KdfContext = "RelayOS/ECDH-P256/HKDF-SHA256/AES-256-GCM/v1"u8.ToArray();

    public RelayPacket Encrypt(
        string senderId,
        RelayPublicKey recipient,
        ReadOnlySpan<byte> plaintext,
        RelaySendOptions? options = null,
        TimeProvider? timeProvider = null,
        Guid? packetId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);
        ArgumentNullException.ThrowIfNull(recipient);

        options ??= new RelaySendOptions();
        timeProvider ??= TimeProvider.System;
        ValidateSend(senderId, recipient, plaintext.Length, options);

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var expiresAt = now + options.TimeToLive;
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

        using var ephemeralIdentity = RelayIdentity.Create("ephemeral");
        var ephemeralPublicKey = ephemeralIdentity.PublicKey.SubjectPublicKeyInfo;
        var sharedSecret = ephemeralIdentity.DeriveSharedSecret(recipient.SubjectPublicKeyInfo);
        var encryptionKey = new byte[KeySizeBytes];

        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, encryptionKey, salt, KdfContext);

            var packet = new RelayPacket
            {
                ProtocolVersion = RelayPacket.CurrentProtocolVersion,
                PacketId = packetId ?? Guid.NewGuid(),
                SenderId = senderId,
                RecipientId = recipient.NodeId,
                CreatedAtUtc = now,
                ExpiresAtUtc = expiresAt,
                Priority = options.Priority,
                ContentType = options.ContentType,
                EphemeralPublicKey = ephemeralPublicKey,
                KeyDerivationSalt = salt,
                Nonce = nonce,
                Ciphertext = new byte[plaintext.Length],
                AuthenticationTag = new byte[TagSizeBytes]
            };

            var authenticatedHeader = RelayPacketCodec.BuildAuthenticatedHeader(packet);
            using var aesGcm = new AesGcm(encryptionKey, TagSizeBytes);
            aesGcm.Encrypt(nonce, plaintext, packet.Ciphertext, packet.AuthenticationTag, authenticatedHeader);
            return packet;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(encryptionKey);
        }
    }

    public byte[] Decrypt(RelayIdentity recipient, RelayPacket packet)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        RelayPacketValidator.Validate(packet);

        if (!string.Equals(recipient.NodeId, packet.RecipientId, StringComparison.Ordinal))
        {
            throw new CryptographicException("This identity is not the packet recipient.");
        }

        var sharedSecret = recipient.DeriveSharedSecret(packet.EphemeralPublicKey);
        var encryptionKey = new byte[KeySizeBytes];

        try
        {
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedSecret,
                encryptionKey,
                packet.KeyDerivationSalt,
                KdfContext);

            var plaintext = new byte[packet.Ciphertext.Length];
            var authenticatedHeader = RelayPacketCodec.BuildAuthenticatedHeader(packet);
            using var aesGcm = new AesGcm(encryptionKey, TagSizeBytes);
            aesGcm.Decrypt(
                packet.Nonce,
                packet.Ciphertext,
                packet.AuthenticationTag,
                plaintext,
                authenticatedHeader);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(encryptionKey);
        }
    }

    private static void ValidateSend(
        string senderId,
        RelayPublicKey recipient,
        int payloadSize,
        RelaySendOptions options)
    {
        if (senderId.Length > RelayProtocol.MaxNodeIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(senderId));
        }

        if (recipient.NodeId.Length > RelayProtocol.MaxNodeIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(recipient));
        }

        if (payloadSize > RelayProtocol.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadSize));
        }

        if (options.TimeToLive <= TimeSpan.Zero || options.TimeToLive > RelayProtocol.MaxTimeToLive)
        {
            throw new ArgumentOutOfRangeException(nameof(options.TimeToLive));
        }

        if (!Enum.IsDefined(options.Priority))
        {
            throw new ArgumentOutOfRangeException(nameof(options.Priority));
        }

        if (string.IsNullOrWhiteSpace(options.ContentType) ||
            options.ContentType.Length > RelayProtocol.MaxContentTypeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ContentType));
        }
    }
}
