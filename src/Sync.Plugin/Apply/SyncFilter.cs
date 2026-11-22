using System;
using System.IO;
using System.Linq;

namespace Sync.Plugin.Apply;

[Flags]
public enum SyncFilter
{
    None = 0,
    Animations = 1,
    Vfx = 2,
    Sounds = 4,
}

public static class SyncFilters
{
    public static readonly (SyncFilter Filter, string Name, string Types)[] All =
    [
        (SyncFilter.Animations, "Animations", ".pap, .tmb"),
        (SyncFilter.Vfx, "VFX", ".avfx, .atex"),
        (SyncFilter.Sounds, "Sounds", ".scd"),
    ];

    public static SyncFilter Of(string gamePath) => Path.GetExtension(gamePath).ToLowerInvariant() switch
    {
        ".pap" or ".tmb" => SyncFilter.Animations,
        ".avfx" or ".atex" => SyncFilter.Vfx,
        ".scd" => SyncFilter.Sounds,
        _ => SyncFilter.None,
    };

    public static bool Blocks(SyncFilter blocked, string gamePath)
    {
        var filter = Of(gamePath);
        return filter != SyncFilter.None && (blocked & filter) != 0;
    }

    public static string Describe(SyncFilter blocked) =>
        string.Join(", ", All.Where(f => (blocked & f.Filter) != 0).Select(f => f.Name));
}
