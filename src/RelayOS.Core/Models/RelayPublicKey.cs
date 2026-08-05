using System.Security.Cryptography;

namespace RelayOS.Core.Models;

public sealed record RelayPublicKey
{
    private readonly byte[] _subjectPublicKeyInfo;

    public RelayPublicKey(string nodeId, byte[] subjectPublicKeyInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfo);

        if (subjectPublicKeyInfo.Length is < 32 or > 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subjectPublicKeyInfo),
                "The encoded public key has an unexpected size.");
        }

        NodeId = nodeId;
        _subjectPublicKeyInfo = (byte[])subjectPublicKeyInfo.Clone();
    }

    public string NodeId { get; }

    public byte[] SubjectPublicKeyInfo => (byte[])_subjectPublicKeyInfo.Clone();

    public string Fingerprint => Convert.ToHexString(
            SHA256.HashData(_subjectPublicKeyInfo))[..16]
        .ToLowerInvariant();
}
