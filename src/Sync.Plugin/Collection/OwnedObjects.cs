using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Sync.Protocol;

namespace Sync.Plugin.Collection;

public static class OwnedObjects
{
    private const uint NinjaJob = 30;
    private const int TableSize = 200;

    public static unsafe OwnedObject[] Find(IObjectTable objectTable, nint playerAddress, ushort playerIndex)
    {
        if (playerAddress == nint.Zero)
            return [];
        var list = new List<OwnedObject>(4) { new(SyncObjectKind.Player, playerAddress, playerIndex) };

        var minionIndex = playerIndex + 1;
        if (minionIndex < TableSize)
        {
            var minion = objectTable.GetObjectAddress(minionIndex);
            if (minion != nint.Zero)
                list.Add(new OwnedObject(SyncObjectKind.MinionOrMount, minion, (ushort)minionIndex));
        }

        var manager = CharacterManager.Instance();
        if (manager != null)
        {
            var buddy = manager->LookupBuddyByOwnerObject((BattleChara*)playerAddress);
            if (buddy != null)
                list.Add(new OwnedObject(SyncObjectKind.Companion, (nint)buddy, ((GameObject*)buddy)->ObjectIndex));

            if (((Character*)playerAddress)->CharacterData.ClassJob != NinjaJob)
            {
                var pet = manager->LookupPetByOwnerObject((BattleChara*)playerAddress);
                if (pet != null)
                    list.Add(new OwnedObject(SyncObjectKind.Pet, (nint)pet, ((GameObject*)pet)->ObjectIndex));
            }
        }
        return list.ToArray();
    }

    public static unsafe (nint Address, ushort Index)? FindPlayer(IObjectTable objectTable, ulong contentId, ushort hint)
    {
        if (contentId == 0)
            return null;
        if (IsPlayer(objectTable, hint, contentId, out var found))
            return (found, hint);
        var local = objectTable.LocalPlayer?.Address ?? nint.Zero;
        for (ushort i = 0; i < TableSize; i++)
        {
            if (i != hint && IsPlayer(objectTable, i, contentId, out found) && found != local)
                return (found, i);
        }
        return null;
    }

    public static bool StillAt(IObjectTable objectTable, ushort index, nint address) =>
        index < TableSize && objectTable.GetObjectAddress(index) == address;

    public static bool SlotEmpty(IObjectTable objectTable, ushort index) =>
        index < TableSize && objectTable.GetObjectAddress(index) == nint.Zero;

    private static unsafe bool IsPlayer(IObjectTable objectTable, ushort index, ulong contentId, out nint address)
    {
        address = index < TableSize ? objectTable.GetObjectAddress(index) : nint.Zero;
        if (address == nint.Zero)
            return false;
        var go = (GameObject*)address;
        return go->ObjectKind == ObjectKind.Pc && ((Character*)address)->ContentId == contentId;
    }
}
