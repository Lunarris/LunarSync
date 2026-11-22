using System.Globalization;
using Sync.Protocol;

namespace Sync.Client.Core;

public sealed record AuthHeaders(string PublicKey, string Timestamp, string Signature)
{
    public string? Protocol { get; init; } = AuthProtocol.ProtocolVersion.ToString(CultureInfo.InvariantCulture);

    public IEnumerable<KeyValuePair<string, string>> AsPairs()
    {
        yield return new(AuthProtocol.PublicKeyHeader, PublicKey);
        yield return new(AuthProtocol.TimestampHeader, Timestamp);
        yield return new(AuthProtocol.SignatureHeader, Signature);
        if (Protocol is not null)
            yield return new(AuthProtocol.ProtocolHeader, Protocol);
    }
}

public interface IAuthHeaderSource
{
    AuthHeaders Create();
}

public sealed class SignedAuthHeaderSource : IAuthHeaderSource
{
    private readonly IdentityStore _identity;
    private readonly TimeProvider _time;

    public SignedAuthHeaderSource(IdentityStore identity, TimeProvider? time = null)
    {
        _identity = identity;
        _time = time ?? TimeProvider.System;
    }

    public AuthHeaders Create() => CreateFor(_identity, _time.GetUtcNow().ToUnixTimeSeconds());

    public static AuthHeaders CreateFor(IdentityStore identity, long unixSeconds)
    {
        var signature = identity.Sign(AuthProtocol.BuildSigningBytes(unixSeconds));
        return new AuthHeaders(
            identity.PublicKeyBase64,
            unixSeconds.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(signature));
    }
}
