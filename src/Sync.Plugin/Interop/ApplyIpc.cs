using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Glamourer.Api.Enums;
using LociApi.Enums;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using GlamourerApplyState = Glamourer.Api.IpcSubscribers.ApplyState;
using GlamourerRevertState = Glamourer.Api.IpcSubscribers.RevertState;
using GlamourerRevertStateName = Glamourer.Api.IpcSubscribers.RevertStateName;
using GlamourerUnlockState = Glamourer.Api.IpcSubscribers.UnlockState;
using GlamourerUnlockStateName = Glamourer.Api.IpcSubscribers.UnlockStateName;
using LociClearManager = LociApi.Ipc.ClearManagerByPtr;
using LociRegister = LociApi.Ipc.RegisterByPtr;
using LociSetManager = LociApi.Ipc.SetManagerByPtr;
using LociUnregister = LociApi.Ipc.UnregisterByPtr;

namespace Sync.Plugin.Interop;

public sealed class ApplyIpc
{
    public const uint GlamourerLock = 0x4C53594E;
    public const string Tag = "LunarSync";

    private readonly CreateTemporaryCollection createCollection;
    private readonly DeleteTemporaryCollection deleteCollection;
    private readonly AssignTemporaryCollection assignCollection;
    private readonly AddTemporaryMod addMod;
    private readonly RemoveTemporaryMod removeMod;
    private readonly RedrawObject redraw;
    private readonly GlamourerApplyState glamourerApply;
    private readonly GlamourerRevertState glamourerRevert;
    private readonly GlamourerUnlockState glamourerUnlock;
    private readonly GlamourerRevertStateName glamourerRevertName;
    private readonly GlamourerUnlockStateName glamourerUnlockName;
    private readonly LociRegister lociRegister;
    private readonly LociUnregister lociUnregister;
    private readonly LociSetManager lociSet;
    private readonly LociClearManager lociClear;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> customizeSet;
    private readonly ICallGateSubscriber<Guid, int> customizeDelete;
    private readonly ICallGateSubscriber<int, string, object?> heelsRegister;
    private readonly ICallGateSubscriber<int, object?> heelsUnregister;
    private readonly ICallGateSubscriber<int, string, object> honorificSet;
    private readonly ICallGateSubscriber<int, object> honorificClear;
    private readonly ICallGateSubscriber<nint, string, object> moodlesSet;
    private readonly ICallGateSubscriber<nint, object> moodlesClear;
    private readonly ICallGateSubscriber<string, object> petNamesSet;
    private readonly ICallGateSubscriber<ushort, object> petNamesClear;

    public ApplyIpc(IDalamudPluginInterface pi)
    {
        createCollection = new CreateTemporaryCollection(pi);
        deleteCollection = new DeleteTemporaryCollection(pi);
        assignCollection = new AssignTemporaryCollection(pi);
        addMod = new AddTemporaryMod(pi);
        removeMod = new RemoveTemporaryMod(pi);
        redraw = new RedrawObject(pi);
        glamourerApply = new GlamourerApplyState(pi);
        glamourerRevert = new GlamourerRevertState(pi);
        glamourerUnlock = new GlamourerUnlockState(pi);
        glamourerRevertName = new GlamourerRevertStateName(pi);
        glamourerUnlockName = new GlamourerUnlockStateName(pi);
        lociRegister = new LociRegister(pi);
        lociUnregister = new LociUnregister(pi);
        lociSet = new LociSetManager(pi);
        lociClear = new LociClearManager(pi);
        customizeSet = pi.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        customizeDelete = pi.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");
        heelsRegister = pi.GetIpcSubscriber<int, string, object?>("SimpleHeels.RegisterPlayer");
        heelsUnregister = pi.GetIpcSubscriber<int, object?>("SimpleHeels.UnregisterPlayer");
        honorificSet = pi.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
        honorificClear = pi.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
        moodlesSet = pi.GetIpcSubscriber<nint, string, object>("Moodles.SetStatusManagerByPtrV2");
        moodlesClear = pi.GetIpcSubscriber<nint, object>("Moodles.ClearStatusManagerByPtrV2");
        petNamesSet = pi.GetIpcSubscriber<string, object>("PetRenamer.SetPlayerData");
        petNamesClear = pi.GetIpcSubscriber<ushort, object>("PetRenamer.ClearPlayerData");
    }

    public string? LastError { get; private set; }

    public Guid CreateCollection(string name)
    {
        Guid id = Guid.Empty;
        Run("Penumbra", () =>
        {
            var result = createCollection.Invoke(Tag, name, out var created);
            if (result == PenumbraApiEc.Success)
                id = created;
        });
        return id;
    }

    public void DeleteCollection(Guid id)
    {
        if (id != Guid.Empty)
            Run("Penumbra", () => deleteCollection.Invoke(id));
    }

    public bool AssignCollection(Guid id, int objectIndex) =>
        id != Guid.Empty && Run("Penumbra", () => assignCollection.Invoke(id, objectIndex, true) == PenumbraApiEc.Success);

    public bool SetMod(Guid id, Dictionary<string, string> paths, string manipulations) => Run("Penumbra", () =>
    {
        removeMod.Invoke(Tag, id, 0);
        return addMod.Invoke(Tag, id, paths, manipulations, 0) == PenumbraApiEc.Success;
    });

    public void Redraw(int objectIndex) => Run("Penumbra", () => redraw.Invoke(objectIndex, RedrawType.Redraw));

    public void GlamourerApply(int objectIndex, string state) =>
        Run("Glamourer", () => glamourerApply.Invoke(state, objectIndex, GlamourerLock));

    public void GlamourerRevert(int objectIndex) => Run("Glamourer", () =>
    {
        glamourerUnlock.Invoke(objectIndex, GlamourerLock);
        glamourerRevert.Invoke(objectIndex, GlamourerLock);
    });

    public void GlamourerRevertByName(string name) => Run("Glamourer", () =>
    {
        glamourerUnlockName.Invoke(name, GlamourerLock);
        glamourerRevertName.Invoke(name, GlamourerLock);
    });

    public Guid? CustomizeSet(ushort objectIndex, string profile) =>
        Run("Customize+", () => customizeSet.InvokeFunc(objectIndex, profile).Item2);

    public void CustomizeDelete(Guid id) => Run("Customize+", () => customizeDelete.InvokeFunc(id));

    public void HeelsSet(int objectIndex, string data) => Run("Simple Heels", () => heelsRegister.InvokeAction(objectIndex, data));

    public void HeelsClear(int objectIndex) => Run("Simple Heels", () => heelsUnregister.InvokeAction(objectIndex));

    public void HonorificSet(int objectIndex, string data) => Run("Honorific", () => honorificSet.InvokeAction(objectIndex, data));

    public void HonorificClear(int objectIndex) => Run("Honorific", () => honorificClear.InvokeAction(objectIndex));

    public void MoodlesSet(nint address, string data) => Run("Moodles", () => moodlesSet.InvokeAction(address, data));

    public void MoodlesClear(nint address) => Run("Moodles", () => moodlesClear.InvokeAction(address));

    public void PetNamesSet(string data) => Run("Pet Nicknames", () => petNamesSet.InvokeAction(data));

    public void PetNamesClear(ushort objectIndex) => Run("Pet Nicknames", () => petNamesClear.InvokeAction(objectIndex));

    public void LociSet(nint address, string data) => Run("Loci", () =>
    {
        var registered = lociRegister.Invoke(address, Tag);
        if (registered is LociApiEc.Success or LociApiEc.NoChange)
            lociSet.Invoke(address, data);
    });

    public void LociClear(nint address) => Run("Loci", () =>
    {
        lociClear.Invoke(address);
        lociUnregister.Invoke(address, Tag, true);
    });

    private void Run(string plugin, Action call) => Run(plugin, () =>
    {
        call();
        return true;
    });

    private T? Run<T>(string plugin, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError or Dalamud.Plugin.Ipc.Exceptions.IpcTypeMismatchError)
        {
            return default;
        }
        catch (Exception ex)
        {
            LastError = $"{plugin}: {ex.Message}";
            Plugin.Log.Error(ex, "{Plugin} apply call failed", plugin);
            return default;
        }
    }
}
