using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using LociApi.Enums;
using Penumbra.Api.Enums;
using GlamourerStateChanged = Glamourer.Api.IpcSubscribers.StateChanged;
using LociManagerChanged = LociApi.Ipc.ManagerChanged;
using PenumbraModSettingChanged = Penumbra.Api.IpcSubscribers.ModSettingChanged;
using PenumbraRedrawn = Penumbra.Api.IpcSubscribers.GameObjectRedrawn;
using PenumbraResolved = Penumbra.Api.IpcSubscribers.GameObjectResourcePathResolved;

namespace Sync.Plugin.Interop;

public sealed class ChangeEvents : IDisposable
{
    private readonly List<IDisposable> subscriptions = [];
    private readonly List<Action> unsubscribes = [];
    private readonly Func<nint, bool> isOwned;
    private readonly Func<Guid, bool> isPairCollection;
    private readonly Action<string> dirty;

    public ChangeEvents(IDalamudPluginInterface pi, Func<nint, bool> isOwned, Func<Guid, bool> isPairCollection,
        Action<string> dirty, Action<nint, string, string> resourceResolved)
    {
        this.isOwned = isOwned;
        this.isPairCollection = isPairCollection;
        this.dirty = dirty;

        subscriptions.Add(PenumbraResolved.Subscriber(pi, (address, gamePath, resolved) =>
        {
            if (address != nint.Zero && isOwned(address))
                resourceResolved(address, gamePath, resolved);
        }));
        subscriptions.Add(PenumbraModSettingChanged.Subscriber(pi, OnModSetting));
        subscriptions.Add(PenumbraRedrawn.Subscriber(pi, (address, _) => Owned(address, "Penumbra")));
        subscriptions.Add(GlamourerStateChanged.Subscriber(pi, address => Owned(address, "Glamourer")));
        subscriptions.Add(LociManagerChanged.Subscriber(pi, (address, _) => Owned(address, "Loci")));

        Gate(pi.GetIpcSubscriber<ushort, Guid, object>("CustomizePlus.Profile.OnUpdate"), (ushort _, Guid _) => dirty("Customize+"));
        Gate(pi.GetIpcSubscriber<string, object?>("SimpleHeels.LocalChanged"), (string _) => dirty("Simple Heels"));
        Gate(pi.GetIpcSubscriber<string, object>("Honorific.LocalCharacterTitleChanged"), (string _) => dirty("Honorific"));
        Gate(pi.GetIpcSubscriber<nint, object>("Moodles.StatusManagerModified"), (nint address) => Owned(address, "Moodles"));
        Gate(pi.GetIpcSubscriber<string, object>("PetRenamer.OnPlayerDataChanged"), (string _) => dirty("Pet Nicknames"));
    }

    public void Dispose()
    {
        foreach (var subscription in subscriptions)
            subscription.Dispose();
        foreach (var unsubscribe in unsubscribes)
        {
            try
            {
                unsubscribe();
            }
            catch (Exception)
            {
            }
        }
        subscriptions.Clear();
        unsubscribes.Clear();
    }

    private void OnModSetting(ModSettingChange change, Guid collection, string mod, bool inherited)
    {
        if (!isPairCollection(collection))
            dirty("Penumbra");
    }

    private void Owned(nint address, string source)
    {
        if (address != nint.Zero && isOwned(address))
            dirty(source);
    }

    private void Gate<T1, TRet>(ICallGateSubscriber<T1, TRet> gate, Action<T1> handler)
    {
        gate.Subscribe(handler);
        unsubscribes.Add(() => gate.Unsubscribe(handler));
    }

    private void Gate<T1, T2, TRet>(ICallGateSubscriber<T1, T2, TRet> gate, Action<T1, T2> handler)
    {
        gate.Subscribe(handler);
        unsubscribes.Add(() => gate.Unsubscribe(handler));
    }
}
