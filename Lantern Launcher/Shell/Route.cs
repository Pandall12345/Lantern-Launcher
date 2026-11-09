namespace LanternLauncher.Shell;

public enum PageKey
{
    Home,
    Library,
    Browse,
    Packs,
    Downloads,
    Settings,
    ModDetail,
    InstanceDetail,
    InstanceMods
}

/// <summary>One navigation step: a page plus an optional argument (detail pages read Arg instead of global state).</summary>
public readonly record struct Route(PageKey Key, object? Arg = null);

public enum NavKind
{
    Normal,
    Back,
    Forward
}

public static class PageInfo
{
    public static string Title(PageKey key) => key switch
    {
        PageKey.Home => "Home",
        PageKey.Library => "Library",
        PageKey.Browse => "Browse",
        PageKey.Packs => "Modpacks",
        PageKey.Downloads => "Downloads",
        PageKey.Settings => "Settings",
        PageKey.ModDetail => "Project",
        PageKey.InstanceDetail => "Instance",
        PageKey.InstanceMods => "Mods",
        _ => "Home"
    };

    public static string Subtitle(PageKey key) => key switch
    {
        PageKey.Home => "Pick up where you left off",
        PageKey.Library => "Every instance on this machine",
        PageKey.Browse => "Mods, modpacks, shaders and resource packs from Modrinth",
        PageKey.Packs => "Curated packs worth a fresh world",
        PageKey.Downloads => "Everything you've pulled down this session",
        PageKey.Settings => "Preferences for this launcher",
        PageKey.ModDetail => "Project details and versions",
        PageKey.InstanceDetail => "Installed instance",
        PageKey.InstanceMods => "What this instance loads",
        _ => ""
    };
}
