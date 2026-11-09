using System.Collections.Generic;

namespace LanternLauncher.Models;

/// <summary>
/// Maps the version JSON. Field names follow Mojang's camelCase; deserialization is case-insensitive.
/// Only the parts needed to launch are modelled here.
/// </summary>
public sealed class VersionJson
{
    public string Id { get; set; } = "";
    public string? Type { get; set; }
    public string? MainClass { get; set; }
    public string? Assets { get; set; }
    public AssetIndexJson? AssetIndex { get; set; }
    public JavaVersionJson? JavaVersion { get; set; }
    public string? MinecraftArguments { get; set; }
    public ArgumentsJson? Arguments { get; set; }
    public List<LibraryJson> Libraries { get; set; } = new();
    public VersionDownloadsJson? Downloads { get; set; }
    public string? InheritsFrom { get; set; }
    public string? ReleaseTime { get; set; }
    public string? Time { get; set; }
}

public sealed class AssetIndexJson
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
    public long TotalSize { get; set; }
}

public sealed class JavaVersionJson
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class ArgumentsJson
{
    public List<object> Jvm { get; set; } = new();
    public List<object> Game { get; set; } = new();
}

public sealed class VersionDownloadsJson
{
    public DownloadArtifact? Client { get; set; }
    public DownloadArtifact? Server { get; set; }
}

public sealed class DownloadArtifact
{
    public string? Path { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
}

public sealed class LibraryJson
{
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public List<RuleJson>? Rules { get; set; }
    public LibraryExtract? Extract { get; set; }
}

public sealed class LibraryDownloads
{
    public DownloadArtifact? Artifact { get; set; }
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class LibraryExtract
{
    public List<string>? Exclude { get; set; }
}

public sealed class RuleJson
{
    public string Action { get; set; } = "allow";
    public RuleOsJson? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class RuleOsJson
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Arch { get; set; }
}

/// <summary>A resolved, launchable version: inheritance chain merged and libraries filtered for the platform.</summary>
public sealed class ResolvedVersion
{
    public string Id { get; init; } = "";
    public string BaseId { get; init; } = "";
    public string? Type { get; init; }
    public string MainClass { get; init; } = "";
    public string? AssetIndexId { get; init; }
    public int JavaMajor { get; init; }
    public bool IsModern { get; init; }
    public string? MinecraftArguments { get; init; }
    public List<object> JvmArguments { get; init; } = new();
    public List<object> GameArguments { get; init; } = new();

    /// <summary>Absolute paths of library jars (rule-filtered, de-duplicated, order preserved).</summary>
    public List<string> Classpath { get; init; } = new();

    /// <summary>Natives jars that need extracting: classifier -> absolute path.</summary>
    public List<(string Classifier, string Path)> NativeJars { get; init; } = new();

    /// <summary>Client main jar (<c>versions/&lt;id&gt;/&lt;id&gt;.jar</c>).</summary>
    public string? ClientJar { get; init; }

    /// <summary>Libraries that are actually missing on disk.</summary>
    public List<string> MissingLibraries { get; init; } = new();
}

public sealed class VersionEntry
{
    public string Id { get; init; } = "";
    public string? Type { get; init; }
    public string? InheritsFrom { get; init; }
    public string JsonPath { get; init; } = "";
    public string? ReleaseTime { get; init; }
}

public sealed class JavaRuntimeInfo
{
    public string ExecutablePath { get; init; } = "";
    public int Major { get; init; }
    public string? FullVersion { get; init; }
    public string? Vendor { get; init; }
    public bool Is64Bit { get; init; }
    public string Source { get; init; } = "";

    public string Display => Major > 0 ? $"Java {Major} ({Vendor ?? "unknown"})" : "Java (unknown)";
}
