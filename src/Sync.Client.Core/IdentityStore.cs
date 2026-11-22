using System.Text.Json;
using System.Text.Json.Serialization;
using Sync.Protocol;

namespace Sync.Client.Core;

public sealed class IdentityStore
{
    public const string FileFormat = "sync-identity-v2";
    public const string LegacyFileFormatV1 = "sync-identity-v1";

    private readonly byte[] _privateKey;
    private readonly byte[] _locatorPrivateKey;

    private IdentityStore(byte[] privateKey, byte[] locatorPrivateKey)
    {
        _privateKey = privateKey;
        _locatorPrivateKey = locatorPrivateKey;
        PublicKey = SyncCrypto.DerivePublicKey(privateKey);
        PublicKeyBase64 = Convert.ToBase64String(PublicKey);
        Uid = SyncCrypto.ComputeUid(PublicKey);
        LocatorPublicKey = SyncCrypto.DeriveX25519PublicKey(locatorPrivateKey);
        LocatorPublicKeyBase64 = Convert.ToBase64String(LocatorPublicKey);
    }

    public byte[] PublicKey { get; }
    public string PublicKeyBase64 { get; }
    public string Uid { get; }

    public byte[] LocatorPublicKey { get; }
    public string LocatorPublicKeyBase64 { get; }

    public static IdentityStore Generate() =>
        new(SyncCrypto.GeneratePrivateKey(), SyncCrypto.GenerateX25519PrivateKey());

    public static IdentityStore FromExport(string exported) => FromParsed(ParseKeyFile(exported));

    public static IdentityStore Load(string path) => Load(path, out _);

    public static IdentityStore Load(string path, out bool upgraded)
    {
        var parsed = ParseKeyFile(File.ReadAllText(path));
        var identity = FromParsed(parsed);
        upgraded = parsed.LocatorPrivateKey is null;
        if (upgraded)
            identity.OverwriteKeyFile(path);
        return identity;
    }

    public static IdentityStore LoadOrCreate(string path, out bool created)
    {
        created = false;
        if (File.Exists(path))
            return Load(path);

        var identity = Generate();
        try
        {
            identity.ExportTo(path);
            created = true;
            return identity;
        }
        catch (IOException) when (File.Exists(path))
        {
            return Load(path);
        }
    }

    public byte[] Sign(byte[] data) => SyncCrypto.Sign(_privateKey, data);

    public byte[]? LocatorAgreement(byte[] peerLocatorPublicKey) =>
        SyncCrypto.X25519Agreement(_locatorPrivateKey, peerLocatorPublicKey);

    public string Export() => JsonSerializer.Serialize(
        new KeyFile(FileFormat,
            Convert.ToBase64String(_privateKey), PublicKeyBase64,
            Convert.ToBase64String(_locatorPrivateKey), LocatorPublicKeyBase64),
        KeyFileJson.Default.KeyFile);

    public void ExportTo(string path) => WriteKeyFile(path, overwrite: false);

    private void OverwriteKeyFile(string path) => WriteKeyFile(path, overwrite: true);

    private void WriteKeyFile(string path, bool overwrite)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, Export());
            File.Move(temp, full, overwrite);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static IdentityStore FromParsed(ParsedKeys keys) =>
        new(keys.PrivateKey, keys.LocatorPrivateKey ?? SyncCrypto.GenerateX25519PrivateKey());

    private sealed record ParsedKeys(byte[] PrivateKey, byte[]? LocatorPrivateKey);

    private static ParsedKeys ParseKeyFile(string text)
    {
        KeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize(text, KeyFileJson.Default.KeyFile);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Key file is not valid JSON.");
        }

        if (file is null || file.Format is not (FileFormat or LegacyFileFormatV1))
            throw new InvalidDataException($"Key file format is not {FileFormat}.");

        var sk = DecodeKey(file.PrivateKey, AuthProtocol.PrivateKeyLength, "private key");
        if (file.PublicKey is not null && file.PublicKey != Convert.ToBase64String(SyncCrypto.DerivePublicKey(sk)))
            throw new InvalidDataException("Key file public key does not match its private key.");

        if (file.Format == LegacyFileFormatV1)
            return new ParsedKeys(sk, null);

        var locator = DecodeKey(file.LocatorPrivateKey, SyncCrypto.X25519KeyLength, "locator private key");
        if (file.LocatorPublicKey is not null &&
            file.LocatorPublicKey != Convert.ToBase64String(SyncCrypto.DeriveX25519PublicKey(locator)))
            throw new InvalidDataException("Key file locator public key does not match its private key.");
        return new ParsedKeys(sk, locator);
    }

    private static byte[] DecodeKey(string? base64, int length, string name)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"Key file {name} is not valid base64.");
        }
        if (bytes.Length != length)
            throw new InvalidDataException($"Key file {name} has the wrong length.");
        return bytes;
    }

    internal sealed record KeyFile(
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("privateKey")] string? PrivateKey,
        [property: JsonPropertyName("publicKey")] string? PublicKey,
        [property: JsonPropertyName("locatorPrivateKey")] string? LocatorPrivateKey = null,
        [property: JsonPropertyName("locatorPublicKey")] string? LocatorPublicKey = null);
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IdentityStore.KeyFile))]
internal sealed partial class KeyFileJson : JsonSerializerContext;
