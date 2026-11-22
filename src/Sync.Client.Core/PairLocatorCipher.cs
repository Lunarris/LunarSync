using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Sync.Protocol;

namespace Sync.Client.Core;

public sealed class PairLocatorCipher : IDisposable
{
    public const string KdfInfoPrefix = "sync-locator-v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int PlaintextSize = sizeof(ulong);
    public const int LocatorSize = NonceSize + PlaintextSize + TagSize;
    public const int StateKeySize = 32;
    private const int EnvelopePlaintextSize = PlaintextSize + StateKeySize;
    public const int EnvelopeSize = NonceSize + EnvelopePlaintextSize + TagSize;

    private readonly AesGcm _aes;
    private readonly string _myUid;
    private readonly string _peerUid;

    private PairLocatorCipher(byte[] key, string myUid, string peerUid)
    {
        _aes = new AesGcm(key, TagSize);
        _myUid = myUid;
        _peerUid = peerUid;
    }

    public static PairLocatorCipher? Create(IdentityStore me, string peerUid, byte[] peerLocatorPublicKey)
    {
        if (peerUid == me.Uid)
            return null;
        var shared = me.LocatorAgreement(peerLocatorPublicKey);
        if (shared is null)
            return null;
        try
        {
            var first = string.CompareOrdinal(me.Uid, peerUid) < 0 ? me.Uid : peerUid;
            var second = first == me.Uid ? peerUid : me.Uid;
            var info = Encoding.UTF8.GetBytes($"{KdfInfoPrefix}\n{first}\n{second}");
            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt: [], info: info);
            return new PairLocatorCipher(key, me.Uid, peerUid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    public string Encrypt(ulong contentId)
    {
        Span<byte> output = stackalloc byte[LocatorSize];
        var nonce = output[..NonceSize];
        var ciphertext = output.Slice(NonceSize, PlaintextSize);
        var tag = output[(NonceSize + PlaintextSize)..];

        RandomNumberGenerator.Fill(nonce);
        Span<byte> plaintext = stackalloc byte[PlaintextSize];
        BinaryPrimitives.WriteUInt64BigEndian(plaintext, contentId);
        _aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(_myUid, _peerUid));
        CryptographicOperations.ZeroMemory(plaintext);
        return Convert.ToBase64String(output);
    }

    public ulong? Decrypt(string locatorBase64)
    {
        Span<byte> input = stackalloc byte[LocatorSize];
        if (!Convert.TryFromBase64String(locatorBase64, input, out var written) || written != LocatorSize)
            return null;
        Span<byte> plaintext = stackalloc byte[PlaintextSize];
        try
        {
            _aes.Decrypt(input[..NonceSize], input.Slice(NonceSize, PlaintextSize), input[(NonceSize + PlaintextSize)..],
                plaintext, Aad(_peerUid, _myUid));
            return BinaryPrimitives.ReadUInt64BigEndian(plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string SealEnvelope(ulong contentId, ReadOnlySpan<byte> stateKey)
    {
        if (stateKey.Length != StateKeySize)
            throw new ArgumentException($"State key must be {StateKeySize} bytes.", nameof(stateKey));
        Span<byte> output = stackalloc byte[EnvelopeSize];
        Span<byte> plaintext = stackalloc byte[EnvelopePlaintextSize];
        BinaryPrimitives.WriteUInt64BigEndian(plaintext, contentId);
        stateKey.CopyTo(plaintext[PlaintextSize..]);
        var nonce = output[..NonceSize];
        RandomNumberGenerator.Fill(nonce);
        _aes.Encrypt(nonce, plaintext, output.Slice(NonceSize, EnvelopePlaintextSize), output[(NonceSize + EnvelopePlaintextSize)..],
            EnvelopeAad(_myUid, _peerUid));
        CryptographicOperations.ZeroMemory(plaintext);
        return Convert.ToBase64String(output);
    }

    public (ulong ContentId, byte[] StateKey)? OpenEnvelope(string envelopeBase64)
    {
        Span<byte> input = stackalloc byte[EnvelopeSize];
        if (!Convert.TryFromBase64String(envelopeBase64, input, out var written) || written != EnvelopeSize)
            return null;
        Span<byte> plaintext = stackalloc byte[EnvelopePlaintextSize];
        try
        {
            _aes.Decrypt(input[..NonceSize], input.Slice(NonceSize, EnvelopePlaintextSize), input[(NonceSize + EnvelopePlaintextSize)..],
                plaintext, EnvelopeAad(_peerUid, _myUid));
            return (BinaryPrimitives.ReadUInt64BigEndian(plaintext), plaintext[PlaintextSize..].ToArray());
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose() => _aes.Dispose();

    private static byte[] Aad(string senderUid, string recipientUid) =>
        Encoding.UTF8.GetBytes($"{AuthProtocol.SigningVersion}|locator|{senderUid}|{recipientUid}");

    private static byte[] EnvelopeAad(string senderUid, string recipientUid) =>
        Encoding.UTF8.GetBytes($"{AuthProtocol.SigningVersion}|envelope|{senderUid}|{recipientUid}");
}
