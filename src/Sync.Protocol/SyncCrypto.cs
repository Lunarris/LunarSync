using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Sync.Protocol;

public static class SyncCrypto
{
    public const int UidByteLength = 16;

    public static string ComputeUid(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != AuthProtocol.PublicKeyLength)
            throw new ArgumentException($"Public key must be {AuthProtocol.PublicKeyLength} bytes.", nameof(publicKey));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(publicKey, hash);
        return Convert.ToHexString(hash[..UidByteLength]);
    }

    public static byte[] GeneratePrivateKey()
    {
        var sk = new byte[AuthProtocol.PrivateKeyLength];
        RandomNumberGenerator.Fill(sk);
        return sk;
    }

    public static byte[] DerivePublicKey(byte[] privateKey)
    {
        RequireLength(privateKey, AuthProtocol.PrivateKeyLength, nameof(privateKey));
        var pk = new byte[AuthProtocol.PublicKeyLength];
        Ed25519.GeneratePublicKey(privateKey, 0, pk, 0);
        return pk;
    }

    public static byte[] Sign(byte[] privateKey, byte[] message)
    {
        RequireLength(privateKey, AuthProtocol.PrivateKeyLength, nameof(privateKey));
        var sig = new byte[AuthProtocol.SignatureLength];
        Ed25519.Sign(privateKey, 0, message, 0, message.Length, sig, 0);
        return sig;
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != AuthProtocol.PublicKeyLength || signature.Length != AuthProtocol.SignatureLength)
            return false;
        try
        {
            return Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public const int X25519KeyLength = 32;

    public static byte[] GenerateX25519PrivateKey()
    {
        var sk = new byte[X25519KeyLength];
        RandomNumberGenerator.Fill(sk);
        return sk;
    }

    public static byte[] DeriveX25519PublicKey(byte[] privateKey)
    {
        RequireLength(privateKey, X25519KeyLength, nameof(privateKey));
        var pk = new byte[X25519KeyLength];
        X25519.ScalarMultBase(privateKey, 0, pk, 0);
        return pk;
    }

    public static byte[]? X25519Agreement(byte[] privateKey, byte[] peerPublicKey)
    {
        RequireLength(privateKey, X25519KeyLength, nameof(privateKey));
        if (peerPublicKey.Length != X25519KeyLength)
            return null;
        var shared = new byte[X25519KeyLength];
        return X25519.CalculateAgreement(privateKey, 0, peerPublicKey, 0, shared, 0) ? shared : null;
    }

    private static void RequireLength(byte[] value, int length, string name)
    {
        if (value.Length != length)
            throw new ArgumentException($"Expected {length} bytes.", name);
    }
}
