using System.Security.Cryptography;
using Sync.Protocol;

namespace Sync.Client.Core;

public static class CharacterPush
{
    public static (CharacterData Data, Dictionary<string, string> Envelopes) Build(IdentityStore me,
        Dictionary<SyncObjectKind, List<FileReplacement>> files, CharacterState state, ulong contentId,
        IEnumerable<PairEntry> recipients)
    {
        var (sealedState, key) = CharacterStateSealer.Seal(state);
        try
        {
            var envelopes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in recipients)
            {
                if (pair.State != PairState.Paired)
                    continue;
                using var cipher = CreateCipher(me, pair);
                if (cipher is not null)
                    envelopes[pair.Uid] = cipher.SealEnvelope(contentId, key);
            }
            return (new CharacterData(files, sealedState), envelopes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static (ulong ContentId, CharacterState State)? Open(IdentityStore me, PairEntry sender, CharacterData data, string envelope)
    {
        if (CharacterDataLimits.Validate(data) is not null)
            return null;
        using var cipher = CreateCipher(me, sender);
        if (cipher?.OpenEnvelope(envelope) is not { } opened)
            return null;
        try
        {
            return CharacterStateSealer.Open(data.SealedState, opened.StateKey) is { } state ? (opened.ContentId, state) : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(opened.StateKey);
        }
    }

    private static PairLocatorCipher? CreateCipher(IdentityStore me, PairEntry pair)
    {
        if (string.IsNullOrEmpty(pair.LocatorPublicKey))
            return null;
        try
        {
            return PairLocatorCipher.Create(me, pair.Uid, Convert.FromBase64String(pair.LocatorPublicKey));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
