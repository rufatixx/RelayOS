using System.Security.Cryptography;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;

namespace RelayOS.Core.Tests;

public sealed class RelayCryptographyTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EncryptThenDecrypt_RoundTripsBinaryPayloadAndMetadata()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var clock = new ManualTimeProvider(TestNow);
        var cryptography = new RelayCryptography();
        var payload = new byte[] { 0, 1, 2, 127, 128, 254, 255 };
        var packetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var packet = cryptography.Encrypt(
            "sender",
            recipient.PublicKey,
            payload,
            new RelaySendOptions
            {
                TimeToLive = TimeSpan.FromMinutes(45),
                Priority = RelayPriority.Critical,
                ContentType = "application/x-relayos-test"
            },
            clock,
            packetId);

        var decrypted = cryptography.Decrypt(recipient, packet);

        Assert.Equal(payload, decrypted);
        Assert.Equal(RelayPacket.CurrentProtocolVersion, packet.ProtocolVersion);
        Assert.Equal(packetId, packet.PacketId);
        Assert.Equal("sender", packet.SenderId);
        Assert.Equal("recipient", packet.RecipientId);
        Assert.Equal(TestNow, packet.CreatedAtUtc);
        Assert.Equal(TestNow.AddMinutes(45), packet.ExpiresAtUtc);
        Assert.Equal(RelayPriority.Critical, packet.Priority);
        Assert.Equal("application/x-relayos-test", packet.ContentType);
        Assert.Equal(payload.Length, packet.Ciphertext.Length);
        Assert.Equal(RelayCryptography.NonceSizeBytes, packet.Nonce.Length);
        Assert.Equal(RelayCryptography.TagSizeBytes, packet.AuthenticationTag.Length);
        Assert.NotEqual(payload, packet.Ciphertext);
    }

    [Fact]
    public void Encrypt_RepeatedMessagesUseUniqueNonces()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var cryptography = new RelayCryptography();
        var clock = new ManualTimeProvider(TestNow);

        var nonces = Enumerable.Range(0, 64)
            .Select(_ => cryptography.Encrypt(
                "sender",
                recipient.PublicKey,
                "same plaintext"u8,
                timeProvider: clock))
            .Select(packet => Convert.ToHexString(packet.Nonce))
            .ToArray();

        Assert.Equal(nonces.Length, nonces.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Decrypt_WithDifferentNodeIdRejectsPacketBeforeKeyAgreement()
    {
        using var recipient = RelayIdentity.Create("recipient");
        using var other = RelayIdentity.Create("other");
        var cryptography = new RelayCryptography();
        var packet = cryptography.Encrypt("sender", recipient.PublicKey, "secret"u8);

        var exception = Assert.ThrowsAny<CryptographicException>(
            () => cryptography.Decrypt(other, packet));

        Assert.Contains("not the packet recipient", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decrypt_WithDifferentPrivateKeyForSameNodeIdFailsAuthentication()
    {
        using var recipient = RelayIdentity.Create("recipient");
        using var impostor = RelayIdentity.Create("recipient");
        var cryptography = new RelayCryptography();
        var packet = cryptography.Encrypt("sender", recipient.PublicKey, "secret"u8);

        Assert.ThrowsAny<CryptographicException>(() => cryptography.Decrypt(impostor, packet));
    }

    [Theory]
    [InlineData("packet-id")]
    [InlineData("sender-id")]
    [InlineData("created-at")]
    [InlineData("expires-at")]
    [InlineData("priority")]
    [InlineData("content-type")]
    public void Decrypt_WhenAuthenticatedHeaderIsModifiedFailsAuthentication(string field)
    {
        using var recipient = RelayIdentity.Create("recipient");
        var cryptography = new RelayCryptography();
        var packet = cryptography.Encrypt(
            "sender",
            recipient.PublicKey,
            "secret"u8,
            new RelaySendOptions
            {
                TimeToLive = TimeSpan.FromHours(2),
                Priority = RelayPriority.High,
                ContentType = "text/plain"
            },
            new ManualTimeProvider(TestNow));

        var tampered = field switch
        {
            "packet-id" => packet with { PacketId = Guid.NewGuid() },
            "sender-id" => packet with { SenderId = "different-sender" },
            "created-at" => packet with { CreatedAtUtc = packet.CreatedAtUtc.AddMinutes(1) },
            "expires-at" => packet with { ExpiresAtUtc = packet.ExpiresAtUtc.AddMinutes(1) },
            "priority" => packet with { Priority = RelayPriority.Critical },
            "content-type" => packet with { ContentType = "application/json" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.ThrowsAny<CryptographicException>(() => cryptography.Decrypt(recipient, tampered));
    }

    [Fact]
    public void Decrypt_WhenAuthenticatedRecipientIdIsModifiedFailsAuthentication()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var privateKey = recipient.ExportPrivateKeyPkcs8();
        using var renamedSameKey = RelayIdentity.ImportPrivateKey("renamed-recipient", privateKey);
        CryptographicOperations.ZeroMemory(privateKey);
        var cryptography = new RelayCryptography();
        var packet = cryptography.Encrypt("sender", recipient.PublicKey, "secret"u8);
        var tampered = packet with { RecipientId = renamedSameKey.NodeId };

        Assert.ThrowsAny<CryptographicException>(() => cryptography.Decrypt(renamedSameKey, tampered));
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("authentication-tag")]
    [InlineData("nonce")]
    [InlineData("salt")]
    public void Decrypt_WhenEncryptedMaterialIsModifiedFailsAuthentication(string field)
    {
        using var recipient = RelayIdentity.Create("recipient");
        var cryptography = new RelayCryptography();
        var packet = cryptography.Encrypt("sender", recipient.PublicKey, "secret"u8);

        var tampered = packet.Copy();
        var bytes = field switch
        {
            "ciphertext" => tampered.Ciphertext,
            "authentication-tag" => tampered.AuthenticationTag,
            "nonce" => tampered.Nonce,
            "salt" => tampered.KeyDerivationSalt,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        bytes[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => cryptography.Decrypt(recipient, tampered));
    }
}
