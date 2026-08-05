using System.Security.Cryptography;
using System.Text;
using RelayOS.Core.Models;

namespace RelayOS.Core.Cryptography;

internal static class RelayPacketCodec
{
    private static readonly byte[] ProtocolLabel = "RelayOS.Packet.v1"u8.ToArray();

    public static byte[] BuildAuthenticatedHeader(RelayPacket packet)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        WriteBytes(writer, ProtocolLabel);
        writer.Write(packet.ProtocolVersion);
        WriteString(writer, packet.PacketId.ToString("N"));
        WriteString(writer, packet.SenderId);
        WriteString(writer, packet.RecipientId);
        writer.Write(packet.CreatedAtUtc.ToUniversalTime().Ticks);
        writer.Write(packet.ExpiresAtUtc.ToUniversalTime().Ticks);
        writer.Write((int)packet.Priority);
        WriteString(writer, packet.ContentType);
        writer.Flush();

        return stream.ToArray();
    }

    public static string CalculateDigest(RelayPacket packet)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        WriteBytes(writer, BuildAuthenticatedHeader(packet));
        WriteBytes(writer, packet.EphemeralPublicKey);
        WriteBytes(writer, packet.KeyDerivationSalt);
        WriteBytes(writer, packet.Nonce);
        WriteBytes(writer, packet.Ciphertext);
        WriteBytes(writer, packet.AuthenticationTag);
        writer.Flush();

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void WriteString(BinaryWriter writer, string value) =>
        WriteBytes(writer, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }
}
