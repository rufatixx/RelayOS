using System.Security.Cryptography;
using RelayOS.Core.Models;

namespace RelayOS.Core.Cryptography;

public sealed class RelayIdentity : IDisposable
{
    private readonly ECDiffieHellman _keyAgreement;
    private bool _disposed;

    private RelayIdentity(string nodeId, ECDiffieHellman keyAgreement)
    {
        ValidateNodeId(nodeId);
        NodeId = nodeId;
        _keyAgreement = keyAgreement;
    }

    public string NodeId { get; }

    public RelayPublicKey PublicKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new RelayPublicKey(NodeId, _keyAgreement.ExportSubjectPublicKeyInfo());
        }
    }

    public static RelayIdentity Create(string nodeId) =>
        new(nodeId, ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public static RelayIdentity ImportPrivateKey(string nodeId, ReadOnlySpan<byte> privateKeyPkcs8)
    {
        var keyAgreement = ECDiffieHellman.Create();

        try
        {
            keyAgreement.ImportPkcs8PrivateKey(privateKeyPkcs8, out var bytesRead);
            if (bytesRead != privateKeyPkcs8.Length)
            {
                throw new CryptographicException("The private key contains trailing data.");
            }

            return new RelayIdentity(nodeId, keyAgreement);
        }
        catch
        {
            keyAgreement.Dispose();
            throw;
        }
    }

    public byte[] ExportPrivateKeyPkcs8()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _keyAgreement.ExportPkcs8PrivateKey();
    }

    internal byte[] DeriveSharedSecret(ReadOnlySpan<byte> peerPublicKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out var bytesRead);
        if (bytesRead != peerPublicKey.Length)
        {
            throw new CryptographicException("The public key contains trailing data.");
        }

        return _keyAgreement.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _keyAgreement.Dispose();
        _disposed = true;
    }

    private static void ValidateNodeId(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        if (nodeId.Length > RelayProtocol.MaxNodeIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId));
        }
    }
}
