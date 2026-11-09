using System;
using System.Collections.Generic;
using System.IO;

namespace LanternLauncher;

public enum LibraryKind
{
    Lantern = 0,
    Mojang = 1,
}

/// <summary>
/// A library is just a .minecraft root. This build ships two fixed ones; adding your own is not supported.
/// </summary>
public sealed class GameLibrary
{
    public LibraryKind Kind { get; init; }
    public string DisplayName { get; init; } = "";
    public string Root { get; init; } = "";
    public bool Exists { get; init; }
    public string PathHint { get; init; } = "";
}

/// <summary>
/// Resolves the roots of both libraries.
/// The Lantern library travels with the launcher: &lt;install dir&gt;\LanternLibrary\.minecraft; when the install
/// directory is read-only (Program Files and friends) it falls back to %LOCALAPPDATA% instead of throwing.
/// </summary>
public static class GameLibraries
{
    private static readonly Lazy<string> LanternRootLazy = new(ResolveLanternRoot);
    private static readonly Lazy<string> MojangRootLazy = new(ResolveMojangRoot);

    public static string LanternRoot => LanternRootLazy.Value;
    public static string MojangRoot => MojangRootLazy.Value;

    public static GameLibrary Lantern => new()
    {
        Kind = LibraryKind.Lantern,
        DisplayName = "Lantern",
        Root = LanternRoot,
        Exists = Directory.Exists(LanternRoot),
        PathHint = LanternRoot,
    };

    public static GameLibrary Mojang => new()
    {
        Kind = LibraryKind.Mojang,
        DisplayName = "Mojang",
        Root = MojangRoot,
        Exists = Directory.Exists(MojangRoot),
        PathHint = MojangRoot,
    };

    public static IReadOnlyList<GameLibrary> All => new[] { Lantern, Mojang };

    public static GameLibrary For(LibraryKind kind) => kind == LibraryKind.Mojang ? Mojang : Lantern;

    private static string ResolveLanternRoot()
    {
        var preferred = Path.Combine(
            AppContext.BaseDirectory, "LanternLibrary", ".minecraft");

        if (TryPrepare(preferred)) return preferred;

        // Install directory is read-only (Program Files): fall back to the user data folder.
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanternLauncher", "LanternLibrary", ".minecraft");

        TryPrepare(fallback);
        return fallback;
    }

    private static string ResolveMojangRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft");

    /// <summary>Usable means the directory exists (or can be created) and is writable.</summary>
    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "versions"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
