using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Util;

namespace Sync.Plugin.Collection;

public static class AnimationCheck
{
    public const int Unchecked = -1;

    private static readonly ConcurrentDictionary<string, int> MaxBoneByHash = new(StringComparer.Ordinal);

    public static unsafe int SkeletonBoneCount(nint player)
    {
        if (player == 0)
            return 0;
        var draw = (CharacterBase*)((Character*)player)->GameObject.DrawObject;
        if (draw == null || draw->GetModelType() != CharacterBase.ModelType.Human || draw->Skeleton == null)
            return 0;
        var skeleton = draw->Skeleton;
        var most = 0;
        for (var i = 0; i < skeleton->PartialSkeletonCount; i++)
        {
            var pose = skeleton->PartialSkeletons[i].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null)
                continue;
            most = Math.Max(most, pose->Skeleton->Bones.Length);
        }
        return most;
    }

    public static bool TryCached(string hash, out int maxBone) => MaxBoneByHash.TryGetValue(hash, out maxBone);

    public static int MaxBoneIndex(string hash, string path)
    {
        if (MaxBoneByHash.TryGetValue(hash, out var cached))
            return cached;
        var result = Read(path);
        MaxBoneByHash[hash] = result;
        return result;
    }

    public static bool Fits(int maxBone, int skeletonBones) =>
        maxBone == Unchecked || skeletonBones <= 0 || maxBone < skeletonBones;

    private static int Read(string path)
    {
        byte[] havok;
        try
        {
            using var reader = new BinaryReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            reader.ReadInt32();
            reader.ReadInt32();
            reader.ReadInt16();
            reader.ReadInt16();
            if (reader.ReadByte() != 0)
                return Unchecked;
            reader.ReadByte();
            reader.ReadInt32();
            var start = reader.ReadInt32();
            var end = reader.ReadInt32();
            if (start <= 0 || end <= start + 8 || end > reader.BaseStream.Length)
                return Unchecked;
            reader.BaseStream.Position = start;
            havok = reader.ReadBytes(end - start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return Unchecked;
        }
        return LoadMaxBone(havok);
    }

    private static unsafe int LoadMaxBone(byte[] havok)
    {
        var temp = Path.Combine(Path.GetTempPath(), "LunarSync-" + Guid.NewGuid().ToString("N") + ".hkx");
        var tempAnsi = nint.Zero;
        try
        {
            File.WriteAllBytes(temp, havok);
            tempAnsi = Marshal.StringToHGlobalAnsi(temp);
            var options = stackalloc hkSerializeUtil.LoadOptions[1];
            options->TypeInfoRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
            options->ClassNameRegistry = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry();
            options->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int> { Storage = (int)hkSerializeUtil.LoadOptionBits.Default };
            var resource = hkSerializeUtil.LoadFromFile((byte*)tempAnsi, null, options);
            if (resource == null)
                return Unchecked;
            try
            {
                var most = Unchecked;
                fixed (byte* rootName = "hkRootLevelContainer"u8)
                fixed (byte* animName = "hkaAnimationContainer"u8)
                {
                    var root = (hkRootLevelContainer*)resource->GetContentsPointer(rootName, hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry());
                    if (root == null)
                        return Unchecked;
                    var container = (hkaAnimationContainer*)root->findObjectByName(animName, null);
                    if (container == null)
                        return Unchecked;
                    for (var i = 0; i < container->Bindings.Length; i++)
                    {
                        var binding = container->Bindings[i].ptr;
                        if (binding == null)
                            continue;
                        var bones = binding->TransformTrackToBoneIndices;
                        for (var b = 0; b < bones.Length; b++)
                            most = Math.Max(most, bones[b]);
                    }
                }
                return most;
            }
            finally
            {
                resource->RemoveReference();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Animation check failed");
            return Unchecked;
        }
        finally
        {
            if (tempAnsi != nint.Zero)
                Marshal.FreeHGlobal(tempAnsi);
            try { File.Delete(temp); }
            catch (IOException) { }
        }
    }
}
