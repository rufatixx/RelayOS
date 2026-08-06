using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;

namespace RelayOS.Core.Tests;

public sealed class RelayProtocolLimitsTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static ManualTimeProvider Clock() => new(TestNow);

    private static RelaySendOptions Options(TimeSpan? timeToLive = null) =>
        new()
        {
            TimeToLive = timeToLive ?? TimeSpan.FromHours(1),
            ContentType = "text/plain",
            Priority = RelayPriority.Normal,
        };

    [Fact]
    public void Payload_ExactlyAtMaxPayloadBytes_IsAccepted()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var payload = new byte[RelayProtocol.MaxPayloadBytes];

        var packet = new RelayCryptography().Encrypt(
            "sender",
            recipient.PublicKey,
            payload,
            Options(),
            Clock());

        Assert.Equal(payload.Length, packet.Ciphertext.Length);
    }

    [Fact]
    public void Payload_OneByteAboveMaxPayloadBytes_IsRejected()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var payload = new byte[RelayProtocol.MaxPayloadBytes + 1];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RelayCryptography().Encrypt("sender", recipient.PublicKey, payload, Options(), Clock()));
    }

    [Fact]
    public void NodeId_ExactlyAtMaxLength_IsAccepted()
    {
        var senderId = new string('s', RelayProtocol.MaxNodeIdLength);
        using var recipient = RelayIdentity.Create(new string('r', RelayProtocol.MaxNodeIdLength));

        var packet = new RelayCryptography().Encrypt(
            senderId,
            recipient.PublicKey,
            "payload"u8,
            Options(),
            Clock());

        Assert.Equal(senderId, packet.SenderId);
        Assert.Equal(recipient.NodeId, packet.RecipientId);
    }

    [Fact]
    public void NodeId_OneCharacterAboveMaxLength_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RelayIdentity.Create(new string('r', RelayProtocol.MaxNodeIdLength + 1)));

        using var recipient = RelayIdentity.Create("recipient");
        var senderId = new string('s', RelayProtocol.MaxNodeIdLength + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RelayCryptography().Encrypt(senderId, recipient.PublicKey, "payload"u8, Options(), Clock()));
    }

    [Fact]
    public void ContentType_ExactlyAtMaxLength_IsAccepted()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var contentType = new string('c', RelayProtocol.MaxContentTypeLength);

        var packet = new RelayCryptography().Encrypt(
            "sender",
            recipient.PublicKey,
            "payload"u8,
            Options() with { ContentType = contentType },
            Clock());

        Assert.Equal(contentType, packet.ContentType);
    }

    [Fact]
    public void ContentType_OneCharacterAboveMaxLength_IsRejected()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var contentType = new string('c', RelayProtocol.MaxContentTypeLength + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RelayCryptography().Encrypt(
                "sender",
                recipient.PublicKey,
                "payload"u8,
                Options() with { ContentType = contentType },
                Clock()));
    }

    [Fact]
    public void TimeToLive_ExactlyAtMaxTimeToLive_IsAccepted()
    {
        using var recipient = RelayIdentity.Create("recipient");

        var packet = new RelayCryptography().Encrypt(
            "sender",
            recipient.PublicKey,
            "payload"u8,
            Options(RelayProtocol.MaxTimeToLive),
            Clock());

        Assert.Equal(TestNow + RelayProtocol.MaxTimeToLive, packet.ExpiresAtUtc);
    }

    [Fact]
    public void TimeToLive_ZeroAndAboveMax_AreRejected()
    {
        using var recipient = RelayIdentity.Create("recipient");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RelayCryptography().Encrypt(
                "sender",
                recipient.PublicKey,
                "payload"u8,
                Options(TimeSpan.Zero),
                Clock()));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RelayCryptography().Encrypt(
                "sender",
                recipient.PublicKey,
                "payload"u8,
                Options(RelayProtocol.MaxTimeToLive + TimeSpan.FromSeconds(1)),
                Clock()));
    }

    [Fact]
    public void EmptyRequiredIdentifiers_AreRejected()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var cryptography = new RelayCryptography();

        Assert.Throws<ArgumentException>(() =>
            cryptography.Encrypt("", recipient.PublicKey, "payload"u8, Options(), Clock()));
        Assert.Throws<ArgumentException>(() =>
            cryptography.Encrypt("   ", recipient.PublicKey, "payload"u8, Options(), Clock()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            cryptography.Encrypt(
                "sender",
                recipient.PublicKey,
                "payload"u8,
                Options() with { ContentType = "" },
                Clock()));
    }

    [Fact]
    public void Decrypt_RejectsPacketWithCiphertextAboveMaxPayloadBytes()
    {
        using var recipient = RelayIdentity.Create("recipient");
        var cryptography = new RelayCryptography();
        var packet = cryptography.Encrypt("sender", recipient.PublicKey, "payload"u8, Options(), Clock());
        var oversized = packet with { Ciphertext = new byte[RelayProtocol.MaxPayloadBytes + 1] };

        Assert.Throws<ArgumentOutOfRangeException>(() => cryptography.Decrypt(recipient, oversized));
    }
}
