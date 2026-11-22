using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using LociApi.Ipc;
using Penumbra.Api.IpcSubscribers;
using GlamourerGetState = Glamourer.Api.IpcSubscribers.GetStateBase64;

namespace Sync.Plugin.Collection;

public sealed class PluginIpc
{
    private readonly GetEnabledState penumbraEnabled;
    private readonly GetGameObjectResourcePaths penumbraResourcePaths;
    private readonly GetPlayerMetaManipulations penumbraMeta;
    private readonly ResolvePlayerPaths penumbraResolvePlayer;
    private readonly ResolveGameObjectPath penumbraResolveObject;
    private readonly GlamourerGetState glamourerState;
    private readonly GetManagerByPtr lociManager;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> customizeActiveProfile;
    private readonly ICallGateSubscriber<Guid, (int, string?)> customizeProfile;
    private readonly ICallGateSubscriber<string> heelsLocal;
    private readonly ICallGateSubscriber<string> honorificLocal;
    private readonly ICallGateSubscriber<nint, string> moodlesStatus;
    private readonly ICallGateSubscriber<string> petNamesLocal;

    public PluginIpc(IDalamudPluginInterface pi)
    {
        penumbraEnabled = new GetEnabledState(pi);
        penumbraResourcePaths = new GetGameObjectResourcePaths(pi);
        penumbraMeta = new GetPlayerMetaManipulations(pi);
        penumbraResolvePlayer = new ResolvePlayerPaths(pi);
        penumbraResolveObject = new ResolveGameObjectPath(pi);
        glamourerState = new GlamourerGetState(pi);
        lociManager = new GetManagerByPtr(pi);
        customizeActiveProfile = pi.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        customizeProfile = pi.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
        heelsLocal = pi.GetIpcSubscriber<string>("SimpleHeels.GetLocalPlayer");
        honorificLocal = pi.GetIpcSubscriber<string>("Honorific.GetLocalCharacterTitle");
        moodlesStatus = pi.GetIpcSubscriber<nint, string>("Moodles.GetStatusManagerByPtrV2");
        petNamesLocal = pi.GetIpcSubscriber<string>("PetRenamer.GetPlayerData");
    }

    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

    public bool PenumbraAvailable => Try("Penumbra", () => penumbraEnabled.Invoke(), false);

    public Dictionary<string, HashSet<string>>? ResourcePaths(ushort objectIndex) =>
        Try("Penumbra", () => penumbraResourcePaths.Invoke(objectIndex) is { Length: > 0 } result ? result[0] : null, null);

    public string[]? ResolvePlayer(string[] gamePaths) =>
        Try("Penumbra", () => penumbraResolvePlayer.Invoke(gamePaths, []).Item1, null);

    public string? ResolveFor(string gamePath, int objectIndex) =>
        Try("Penumbra", () => penumbraResolveObject.Invoke(gamePath, objectIndex), null);

    public string PenumbraMeta() => Try("Penumbra", () => penumbraMeta.Invoke() ?? string.Empty, string.Empty);

    public string Glamourer(ushort objectIndex) =>
        Try("Glamourer", () => glamourerState.Invoke(objectIndex).Item2 ?? string.Empty, string.Empty);

    public string CustomizePlus(ushort objectIndex) => Try("Customize+", () =>
    {
        var (code, id) = customizeActiveProfile.InvokeFunc(objectIndex);
        if (code != 0 || id is null)
            return string.Empty;
        return customizeProfile.InvokeFunc(id.Value).Item2 ?? string.Empty;
    }, string.Empty);

    public string Loci(nint address) =>
        address == nint.Zero ? string.Empty : Try("Loci", () => lociManager.Invoke(address).Item2 ?? string.Empty, string.Empty);

    public string Heels() => Try("Simple Heels", () => heelsLocal.InvokeFunc() ?? string.Empty, string.Empty);

    public string Honorific() => Try("Honorific", () => honorificLocal.InvokeFunc() ?? string.Empty, string.Empty);

    public string Moodles(nint address) =>
        address == nint.Zero ? string.Empty : Try("Moodles", () => moodlesStatus.InvokeFunc(address) ?? string.Empty, string.Empty);

    public string PetNames() => Try("Pet Nicknames", () => petNamesLocal.InvokeFunc() ?? string.Empty, string.Empty);

    private T Try<T>(string name, Func<T> call, T fallback)
    {
        try
        {
            var result = call();
            Missing.Remove(name);
            return result;
        }
        catch (Exception)
        {
            Missing.Add(name);
            return fallback;
        }
    }
}
