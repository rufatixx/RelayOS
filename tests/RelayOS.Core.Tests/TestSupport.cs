using System.Text;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;

namespace RelayOS.Core.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        if (utcNow.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The test clock must start in UTC.", nameof(utcNow));
        }

        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        _utcNow += amount;
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The test clock must use UTC.", nameof(value));
        }

        _utcNow = value;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "RelayOS.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class TestPackets
{
    public static RelayPacket Create(
        RelayPublicKey recipient,
        TimeProvider clock,
        Guid? packetId = null,
        RelayPriority priority = RelayPriority.Normal,
        TimeSpan? timeToLive = null,
        string senderId = "sender",
        string payload = "payload",
        string contentType = "text/plain") =>
        new RelayCryptography().Encrypt(
            senderId,
            recipient,
            Encoding.UTF8.GetBytes(payload),
            new RelaySendOptions
            {
                Priority = priority,
                TimeToLive = timeToLive ?? TimeSpan.FromHours(1),
                ContentType = contentType
            },
            clock,
            packetId);
}

internal static class RelayPacketAssertions
{
    public static void Equivalent(RelayPacket expected, RelayPacket actual)
    {
        Assert.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        Assert.Equal(expected.PacketId, actual.PacketId);
        Assert.Equal(expected.SenderId, actual.SenderId);
        Assert.Equal(expected.RecipientId, actual.RecipientId);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.ExpiresAtUtc, actual.ExpiresAtUtc);
        Assert.Equal(expected.Priority, actual.Priority);
        Assert.Equal(expected.ContentType, actual.ContentType);
        Assert.Equal(expected.EphemeralPublicKey, actual.EphemeralPublicKey);
        Assert.Equal(expected.KeyDerivationSalt, actual.KeyDerivationSalt);
        Assert.Equal(expected.Nonce, actual.Nonce);
        Assert.Equal(expected.Ciphertext, actual.Ciphertext);
        Assert.Equal(expected.AuthenticationTag, actual.AuthenticationTag);
    }
}
