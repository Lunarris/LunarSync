using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sync.Protocol;

namespace Sync.Client.Core;

public static class CharacterStateSealer
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxOpenedBytes = 32 * 1024 * 1024;
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes($"{AuthProtocol.SigningVersion}|state");

    public static (string Sealed, byte[] Key) Seal(CharacterState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var brotli = new BrotliStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                brotli.Write(json);
            compressed = buffer.ToArray();
        }

        var key = RandomNumberGenerator.GetBytes(PairLocatorCipher.StateKeySize);
        var output = new byte[NonceSize + compressed.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, compressed, output.AsSpan(NonceSize, compressed.Length), output.AsSpan(NonceSize + compressed.Length), Aad);
        return (Convert.ToBase64String(output), key);
    }

    public static CharacterState? Open(string sealedBase64, byte[] key)
    {
        byte[] input;
        try
        {
            input = Convert.FromBase64String(sealedBase64);
        }
        catch (FormatException)
        {
            return null;
        }
        if (input.Length < NonceSize + TagSize || key.Length != PairLocatorCipher.StateKeySize)
            return null;

        var compressed = new byte[input.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, compressed.Length),
                input.AsSpan(NonceSize + compressed.Length), compressed, Aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }

        try
        {
            using var brotli = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var json = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = brotli.Read(chunk)) > 0)
            {
                if (json.Length + read > MaxOpenedBytes)
                    return null;
                json.Write(chunk, 0, read);
            }
            return JsonSerializer.Deserialize<CharacterState>(json.ToArray());
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return null;
        }
    }
}
