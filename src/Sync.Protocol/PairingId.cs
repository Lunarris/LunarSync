using System.Security.Cryptography;

namespace Sync.Protocol;

public static class PairingId
{
    public const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int CodeLength = 8;

    public const int AliasMinLength = 3;
    public const int AliasMaxLength = 20;

    public static string GenerateCode()
    {
        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    public static string FormatCode(string code) =>
        code.Length == CodeLength ? $"{code[..4]}-{code[4..]}" : code;

    public static string? NormalizeCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        Span<char> buffer = stackalloc char[CodeLength];
        var n = 0;
        foreach (var raw in input)
        {
            if (raw is '-' or ' ')
                continue;
            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (n == CodeLength || !CodeAlphabet.Contains(c))
                return null;
            buffer[n++] = c;
        }
        return n == CodeLength ? new string(buffer) : null;
    }

    public static string? ValidateAlias(string alias)
    {
        if (alias.Length < AliasMinLength || alias.Length > AliasMaxLength)
            return AliasError.Invalid;
        if (!char.IsAsciiLetter(alias[0]))
            return AliasError.Invalid;
        foreach (var c in alias)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
                return AliasError.Invalid;
        }
        return null;
    }

    public static string NormalizeAlias(string alias) => alias.ToLowerInvariant();
}

public static class AliasError
{
    public const string Invalid = "alias_invalid";
    public const string Taken = "alias_taken";
}

public static class LocatorKeyError
{
    public const string Invalid = "locator_key_invalid";
}

public sealed record PairingIdInfo(string PairCode, string? Alias)
{
    public string DisplayCode => PairingId.FormatCode(PairCode);
}

public sealed record SetAliasResult(string? Error, PairingIdInfo Current)
{
    public bool Success => Error is null;
}

public sealed record SetLocatorKeyResult(string? Error)
{
    public bool Success => Error is null;
}
