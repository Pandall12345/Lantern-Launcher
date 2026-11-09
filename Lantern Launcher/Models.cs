using System.ComponentModel;

namespace LanternLauncher;

public class GameInstance : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _version = string.Empty;
    private string _modLoader = string.Empty;
    private string _coverUrl = string.Empty;
    private string _playTime = string.Empty;
    private string _lastPlayed = string.Empty;
    private string _description = string.Empty;
    private bool _hasUpdate;
    private string _fileSize = string.Empty;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string Version
    {
        get => _version;
        set { _version = value; OnPropertyChanged(nameof(Version)); }
    }

    public string ModLoader
    {
        get => _modLoader;
        set { _modLoader = value; OnPropertyChanged(nameof(ModLoader)); }
    }

    public string CoverUrl
    {
        get => _coverUrl;
        set { _coverUrl = value; OnPropertyChanged(nameof(CoverUrl)); }
    }

    public string PlayTime
    {
        get => _playTime;
        set { _playTime = value; OnPropertyChanged(nameof(PlayTime)); }
    }

    public string LastPlayed
    {
        get => _lastPlayed;
        set { _lastPlayed = value; OnPropertyChanged(nameof(LastPlayed)); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(nameof(Description)); }
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        set { _hasUpdate = value; OnPropertyChanged(nameof(HasUpdate)); }
    }

    public string FileSize
    {
        get => _fileSize;
        set { _fileSize = value; OnPropertyChanged(nameof(FileSize)); }
    }

    /// <summary>Real folder name of the version (Name is only a display label; launching uses this).</summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>Last write time on disk, used for the "recently played" sort.</summary>
    public DateTime LastPlayedUtc { get; set; } = DateTime.UnixEpoch;

    /// <summary>Ready / Incomplete / Missing.</summary>
    public string Status { get; set; } = "Ready";

    /// <summary>Which library this instance belongs to -- decides the scan root, libraries/assets paths and launch classpath.</summary>
    public LibraryKind LibraryKind { get; set; } = LibraryKind.Lantern;

    /// <summary>Root of the owning library's .minecraft; used for launching and for "open folder".</summary>
    public string LibraryRoot { get; set; } = string.Empty;

    /// <summary>Actual path of <c>versions\&lt;id&gt;</c>.</summary>
    public string InstanceDir { get; set; } = string.Empty;

    public int ModsCount { get; set; }

    /// <summary>Pack icon shipped with the instance (icon.png / pack.png / cover.png), or null.</summary>
    public string? PackIconPath { get; set; }

    public bool HasPackIcon => !string.IsNullOrWhiteSpace(PackIconPath) && System.IO.File.Exists(PackIconPath);

    public long SizeBytes { get; set; }

    public bool IsReady => string.Equals(Status, "Ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>When there is no real cover, the leading version segment acts as the badge (26.3-snapshot-9 -> 26.3).</summary>
    public string BadgeText
    {
        get
        {
            var id = string.IsNullOrWhiteSpace(VersionId) ? Version : VersionId;
            if (string.IsNullOrWhiteSpace(id)) return "?";
            var head = id.Split('-')[0];
            return head.Length > 6 ? head[..6] : head;
        }
    }

    public bool HasCover => !string.IsNullOrWhiteSpace(CoverUrl);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// News (Mojang official feed) + Modrinth discovery models

public class NewsItem
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Author { get; set; }
    public string Image { get; set; } = "";
    public string? ImageAltText { get; set; }
    public long Time { get; set; }

    public string DateText => FormatHelper.RelativeTime(DateTimeOffset.FromUnixTimeSeconds(Time).LocalDateTime);
    public string BylineText => string.IsNullOrEmpty(Author) ? DateText : $"{Author} · {DateText}";
}

public class ModSearchResult
{
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public long Downloads { get; set; }
    public long Follows { get; set; }
    public List<string> DisplayCategories { get; set; } = new();
    public string ProjectType { get; set; } = "";   // mod / modpack / shader / resourcepack
    public DateTime DateModified { get; set; }

    /// <summary>The Latin part of the title when the author also wrote one; see TitleText.</summary>
    public string DisplayTitle => Common.TitleText.For(Title);

    public string DownloadsText => FormatHelper.Count(Downloads);
    public string FollowsText => FormatHelper.Count(Follows);
    public string UpdatedText => "Updated " + FormatHelper.RelativeTime(DateModified);
    public string CategoriesText => string.Join("  ·  ", DisplayCategories.Take(3)
        .Select(c => char.ToUpper(c[0]) + c[1..]));
    public string WebsiteUrl => $"https://modrinth.com/{ProjectType}/{Slug}";
}

public class ModVersion
{
    public string Name { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public List<string> Loaders { get; set; } = new();
    public List<string> GameVersions { get; set; } = new();
    public DateTime DatePublished { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? VersionNumber : Name;
    public string LoadersText => string.Join(", ", Loaders);
    public string GameVersionsText => GameVersions.Count > 3
        ? string.Join(", ", GameVersions.Take(3)) + "…"
        : string.Join(", ", GameVersions);
    public string DateText => DatePublished.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
    public string SizeText => FormatHelper.Bytes(SizeBytes);
}

internal static class FormatHelper
{
    public static string Count(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString()
    };

    public static string Bytes(long b) => b switch
    {
        >= 1_073_741_824 => $"{b / 1_073_741_824.0:0.##} GB",
        >= 1_048_576 => $"{b / 1_048_576.0:0.##} MB",
        >= 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b} B"
    };

    public static string RelativeTime(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        if (d.TotalDays < 365) return $"{(int)(d.TotalDays / 30)}mo ago";
        return $"{(int)(d.TotalDays / 365)}y ago";
    }
}
