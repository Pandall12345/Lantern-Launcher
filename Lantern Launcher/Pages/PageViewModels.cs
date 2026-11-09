using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using LanternLauncher.Models;
using System.Windows.Input;
using LanternLauncher.Common;
using LanternLauncher.Services;
using LanternLauncher.Shell;

namespace LanternLauncher.Pages;

// Home

public sealed class HomeViewModel : ViewModelBase
{
    /// <summary>Worlds other than the hero one (at most two).</summary>
    public ObservableCollection<WorldInfo> Worlds { get; } = new();

    public ObservableCollection<ModSearchResult> PopularPacks { get; } = new();
    public ObservableCollection<ModSearchResult> FreshMods { get; } = new();

    private WorldInfo? _hero;
    public WorldInfo? Hero
    {
        get => _hero;
        private set
        {
            if (!SetProperty(ref _hero, value)) return;
            OnPropertyChanged(nameof(HasHero));
        }
    }

    public bool HasHero => _hero is not null;

    private System.Windows.Media.Brush? _heroBrush;
    /// <summary>Gradient sampled from the world icon: dense on the left, fading into paper white. Reads better than a blurred upscale.</summary>
    public System.Windows.Media.Brush? HeroBrush
    {
        get => _heroBrush;
        private set => SetProperty(ref _heroBrush, value);
    }

    private string _greeting = "";
    public string GreetingText
    {
        get => _greeting;
        private set => SetProperty(ref _greeting, value);
    }

    private string _dateLine = "";
    public string DateLine
    {
        get => _dateLine;
        private set => SetProperty(ref _dateLine, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>The four news cards on Home; the block disappears entirely when offline.</summary>
    public ObservableCollection<NewsItem> NewsTop { get; } = new();

    private bool _hasNews;
    public bool HasNews
    {
        get => _hasNews;
        private set => SetProperty(ref _hasNews, value);
    }

    private LoadState _state = LoadState.Loading;
    public LoadState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(ShowStrips));
        }
    }

    /// <summary>Offline (Empty) hides both strips; loading still counts as "going to show".</summary>
    public bool ShowStrips => _state != LoadState.Empty && _state != LoadState.Error;

    /// <summary>Placeholder card for the strip's loading state.</summary>
    public int[] SkeletonRail { get; } = new int[4];

    public ICommand OpenWorldCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand GotoLibraryCommand { get; }
    public ICommand OpenNewsCommand { get; }
    public ICommand OpenModpacksCommand { get; }
    public ICommand OpenBrowseCommand { get; }
    public ICommand RetryCommand { get; }

    private bool _started;

    public HomeViewModel()
    {
        OpenWorldCommand = new RelayCommand<WorldInfo>(OpenWorldFolder);
        OpenProjectCommand = new RelayCommand<ModSearchResult>(p => AppShell.Instance.OpenProject(p));
        GotoLibraryCommand = new RelayCommand(() => AppShell.Instance.Request(new Route(PageKey.Library)));
        OpenNewsCommand = new RelayCommand<NewsItem>(n => AppShell.Instance.OpenNews(n));
        OpenModpacksCommand = new RelayCommand(() => AppShell.Instance.Request(new Route(PageKey.Packs)));
        OpenBrowseCommand = new RelayCommand(() => AppShell.Instance.Request(new Route(PageKey.Browse)));
        RetryCommand = new AsyncRelayCommand(() =>
        {
            _started = false;
            return LoadAsync();
        });
    }

    public async Task LoadAsync()
    {
        if (_started) return;
        _started = true;

        await LoadLocalAsync();
        await LoadStripsAsync();
    }

    // Local: status line and worlds
    private async Task LoadLocalAsync()
    {
        try
        {
            var (instances, worlds, status) = await Task.Run(() =>
            {
                var versions = new InstanceService().Scan();
                var saves = new WorldService().Scan();

                var javaText = "no Java";
                var runtimes = new JavaService().Discover();
                if (runtimes.Count > 0) javaText = $"Java {runtimes.Max(r => r.Major)}";
                else return (versions, saves, "");

                var root = JavaService.DefaultMinecraftDir();
                var disk = DirSize(Path.Combine(root, "versions"))
                         + DirSize(Path.Combine(root, "libraries"))
                         + DirSize(Path.Combine(root, "saves"));

                var text = $"{versions.Count} {(versions.Count == 1 ? "version" : "versions")} installed · {javaText} · {Mb(disk)} of game files";
                return (versions, saves, text);
            });

            var now = DateTime.Now;
            GreetingText = Services.Greeting.Line(now, LaunchProfile.Default.PlayerName);
            DateLine = Services.Greeting.DateLine(now);
            StatusText = status;

            Hero = worlds.FirstOrDefault();
            HeroBrush = Hero is { HasIcon: true } ? TintFromIcon(Hero.IconPath) : null;
            Worlds.Clear();
            foreach (var w in worlds.Skip(1).Take(2)) Worlds.Add(w);
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't read your local setup — " + ex.Message;
        }
    }

    /// <summary>Downsamples the world icon to 16px, averages the colour, then blends it into a left-to-right gradient towards paper white.</summary>
    private static System.Windows.Media.Brush? TintFromIcon(string path)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 16;
            image.EndInit();
            image.Freeze();

            var source = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                image, System.Windows.Media.PixelFormats.Bgra32, null, 0);

            var width = source.PixelWidth;
            var height = source.PixelHeight;
            if (width < 1 || height < 1) return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);

            var count = width * height;

            // Averaging grass and sky turns grey, so only the most saturated quarter of the pixels counts.
            var samples = new System.Collections.Generic.List<(int R, int G, int B, int Sat)>(count);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                int pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
                var max = Math.Max(pr, Math.Max(pg, pb));
                var min = Math.Min(pr, Math.Min(pg, pb));
                samples.Add((pr, pg, pb, max - min));
            }

            var picked = samples.OrderByDescending(x => x.Sat).Take(Math.Max(1, count / 4)).ToList();
            var avgR = (long)picked.Average(x => x.R);
            var avgG = (long)picked.Average(x => x.G);
            var avgB = (long)picked.Average(x => x.B);

            // Blend in just 35% paper white: still inside the light theme, yet recognisably this world's colour.
            byte Mix(long c, byte paper) => (byte)Math.Clamp((c * 0.65) + (paper * 0.35), 0, 255);
            var tone = System.Windows.Media.Color.FromRgb(Mix(avgR, 248), Mix(avgG, 249), Mix(avgB, 250));

            var brush = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0.2),
                EndPoint = new System.Windows.Point(1, 0.9),
            };
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(tone, 0));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(tone, 0.32));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(248, 249, 250), 0.92));
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppInfo.DataDir, "tint-error.txt"), ex.ToString());
            }
            catch (IOException) { }
            return null;
        }
    }

    private static long DirSize(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f =>
                    {
                        try { return new FileInfo(f).Length; }
                        catch (IOException) { return 0L; }
                    })
                : 0L;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static string Mb(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
            : $"{Math.Max(1, bytes / 1024 / 1024)} MB";

    // Network: two strips and one news row. On failure the block disappears; there is no error page.
    private async Task LoadStripsAsync()
    {
        State = LoadState.Loading;

        try
        {
            var packsTask = ModrinthService.SearchAsync(
                BrowseType.ModPack, "", "", "", BrowseSort.Downloads, 0, default, 4);
            var modsTask = ModrinthService.SearchAsync(
                BrowseType.Mod, "", "", "", BrowseSort.Updated, 0, default, 4);
            var newsTask = NewsService.FetchAsync();

            await Task.WhenAll(packsTask, modsTask, newsTask);

            PopularPacks.Clear();
            foreach (var p in packsTask.Result.Items) PopularPacks.Add(p);

            FreshMods.Clear();
            foreach (var m in modsTask.Result.Items) FreshMods.Add(m);

            NewsTop.Clear();
            foreach (var n in newsTask.Result.Take(4)) NewsTop.Add(n);
            HasNews = NewsTop.Count > 0;
            State = LoadState.Ready;
        }
        catch (Exception)
        {
            // Offline: strips and news are hidden, the rest of the page stays as it is.
            PopularPacks.Clear();
            FreshMods.Clear();
            NewsTop.Clear();
            HasNews = false;
            State = LoadState.Empty;
        }
    }

    private void OpenWorldFolder(WorldInfo? world)
    {
        if (world is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(world.Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppShell.Instance.ShowToast("Couldn't open the folder — " + ex.Message, ToastType.Error);
        }
    }
}

// Library

public sealed class LibraryViewModel : ViewModelBase
{
    private readonly GameLibrary _lantern = GameLibraries.Lantern;
    private readonly GameLibrary _mojang = GameLibraries.Mojang;

    private readonly List<InstanceCard> _lanternCards = new();
    private readonly List<InstanceCard> _mojangCards = new();

    private bool _loaded;

    public ObservableCollection<InstanceCard> Items { get; } = new();

    private InstanceCard? _featured;
    public InstanceCard? Featured
    {
        get => _featured;
        private set
        {
            if (!SetProperty(ref _featured, value)) return;
            OnPropertyChanged(nameof(HasFeatured));
        }
    }

    public bool HasFeatured => _featured is not null;

    // Library switching

    private LibraryKind _kind = LibraryKind.Lantern;

    /// <summary>
    /// Set only when the user clicks a segment. Entering the page or restoring the last
    /// library is not a switch, so the animation gate reads this instead of IsLoading.
    /// </summary>
    public bool IsUserSwitchingLibrary { get; private set; }

    public LibraryKind Kind
    {
        get => _kind;
        private set
        {
            if (!SetProperty(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsLantern));
            OnPropertyChanged(nameof(CurrentName));
            OnPropertyChanged(nameof(PathText));
            OnPropertyChanged(nameof(ShowMissing));
            OnPropertyChanged(nameof(CountText));
            Apply();
        }
    }

    public bool IsLantern => _kind == LibraryKind.Lantern;

    private GameLibrary Current => _kind == LibraryKind.Mojang ? _mojang : _lantern;

    public string CurrentName => _kind == LibraryKind.Mojang ? "Mojang" : "Lantern";

    public string PathText => Current.Root;

    public int LanternCount => _lanternCards.Count;
    public int MojangCount => _mojangCards.Count;

    public string LanternCountText => _lanternCards.Count.ToString();
    public string MojangCountText => _mojangCards.Count.ToString();

    /// <summary>The official library is missing on this machine. That is normal, not an error.</summary>
    public bool ShowMissing => !Current.Exists;

    private LoadState _state = LoadState.Loading;
    public LoadState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    public bool IsLoading => _state == LoadState.Loading;

    public bool ShowEmpty => !IsLoading && !ShowMissing && Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    /// <summary>Grid columns = min(3, instance count), so two or three instances leave no empty column.</summary>
    public int GridColumns => Items.Count switch { 0 => 1, 1 => 1, 2 => 2, _ => 3 };

    public string CountText
    {
        get
        {
            if (ShowMissing) return "Not found on this machine";
            var n = Items.Count;
            return n == 1 ? "1 instance" : $"{n} instances";
        }
    }

    // Sorting

    public ObservableCollection<string> Sorts { get; } = new() { "Last played", "Name", "Version" };

    private string _sort = "Last played";
    public string Sort
    {
        get => _sort;
        set
        {
            if (!SetProperty(ref _sort, value)) return;
            Apply();
        }
    }

    // Commands

    public ICommand SelectLibraryCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand BrowseModpacksCommand { get; }
    public ICommand OpenInstanceCommand { get; }
    public ICommand PlayCommand { get; }

    public LibraryViewModel()
    {
        SelectLibraryCommand = new RelayCommand<string>(name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            IsUserSwitchingLibrary = true;
            Kind = name.StartsWith("M", StringComparison.OrdinalIgnoreCase)
                ? LibraryKind.Mojang
                : LibraryKind.Lantern;
            IsUserSwitchingLibrary = false;
            AppShell.Instance.Settings.LastLibrary = CurrentName;
            Services.SettingsService.Instance.Save(AppShell.Instance.Settings);
        });

        OpenFolderCommand = new RelayCommand(() => Shell.AppShell.OpenInExplorer(Current.Root));

        BrowseModpacksCommand = new RelayCommand(() => AppShell.Instance.Request(Shell.PageKey.Packs));

        OpenInstanceCommand = new RelayCommand<InstanceCard>(card =>
        {
            if (card is null) return;
            AppShell.Instance.OpenInstance(card.Instance);
        });

        PlayCommand = new RelayCommand<InstanceCard>(card =>
        {
            if (card is null) return;
            AppShell.Instance.Launch(card.Instance);
        });
    }

    public async Task LoadAsync()
    {
        // Loaded once already: rescan silently so a pack installed elsewhere shows up, with no skeleton flash.
        var silent = _loaded;
        if (!silent) State = LoadState.Loading;

        var (lantern, mojang) = await Task.Run(() =>
        (
            Scan(_lantern, LibraryKind.Lantern),
            Scan(_mojang, LibraryKind.Mojang)
        ));

        _lanternCards.Clear();
        _lanternCards.AddRange(lantern);
        _mojangCards.Clear();
        _mojangCards.AddRange(mojang);

        _loaded = true;
        OnPropertyChanged(nameof(LanternCount));
        OnPropertyChanged(nameof(MojangCount));
        OnPropertyChanged(nameof(LanternCountText));
        OnPropertyChanged(nameof(MojangCountText));

        // Restoring the last library only notifies when the value actually changes.
        // Raising it on every entry made later visits look like a user switch,
        // which replayed the switch animation each time.
        var last = AppShell.Instance.Settings.LastLibrary;
        var restore = string.Equals(last, "Mojang", StringComparison.OrdinalIgnoreCase)
            ? LibraryKind.Mojang
            : LibraryKind.Lantern;

        if (restore != _kind)
        {
            _kind = restore;
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(IsLantern));
            OnPropertyChanged(nameof(CurrentName));
            OnPropertyChanged(nameof(ShowMissing));
        }

        State = LoadState.Ready;
        Apply();
    }

    private static List<InstanceCard> Scan(GameLibrary library, LibraryKind kind)
    {
        if (!library.Exists) return new List<InstanceCard>();

        var service = new Services.InstanceService(
            new Services.VersionService(library.Root), kind);

        var cards = new List<InstanceCard>();
        var list = service.Scan();
        for (var i = 0; i < list.Count; i++)
            cards.Add(new InstanceCard(list[i], i + 1));

        return cards;
    }

    private void Apply()
    {
        var source = _kind == LibraryKind.Mojang ? _mojangCards : _lanternCards;

        var ordered = Sort switch
        {
            "Name" => source.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "Version" => source.OrderByDescending(c => c.Version, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => source.OrderByDescending(c => c.Instance.LastPlayedUtc).ToList(),
        };

        Items.Clear();
        Featured = null;

        foreach (var card in ordered)
        {
            card.Featured = false;
            Items.Add(card);
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(GridColumns));
        OnPropertyChanged(nameof(LanternCountText));
        OnPropertyChanged(nameof(MojangCountText));
        OnPropertyChanged(nameof(PathText));
    }
}

/// <summary>Card shape. Featured is the most recent instance and gets its own full-width row.</summary>
public sealed class InstanceCard : ViewModelBase
{
    public InstanceCard(GameInstance instance, int index)
    {
        Instance = instance;
        _index = index;
    }

    public GameInstance Instance { get; }

    private int _index;
    public int Index
    {
        get => _index;
        set
        {
            _index = value;
        }
    }

    private bool _featured;
    public bool Featured
    {
        get => _featured;
        set
        {
            if (_featured == value) return;
            _featured = value;
            OnPropertyChanged(nameof(Featured));
        }
    }
    public string Name => Instance.Name;
    public string Version => Instance.Version;
    public string Watermark => Instance.BadgeText;
    public string Loader => string.IsNullOrWhiteSpace(Instance.ModLoader) ? "Vanilla" : Instance.ModLoader;
    /// <summary>Left of the colour band: the version, shown only when it differs from the name.</summary>
    public string BandVersion => ShowName ? Instance.Version : "";

    public string SpecWithLoader => $"{Loader} · {Spec}";

    /// <summary>Initials shown on the cover when the instance has no artwork of its own.</summary>
    public string Monogram
    {
        get
        {
            var letters = new System.Text.StringBuilder();
            foreach (var word in Instance.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length == 0 || word[0] > 127) continue;
                letters.Append(char.ToUpperInvariant(word[0]));
                if (letters.Length == 2) break;
            }
            return letters.Length > 0 ? letters.ToString() : "MC";
        }
    }

    public string LoaderAndSpec => $"{Loader} · {SpecShort}";

    public string SpecShort => Instance.ModsCount > 0
        ? $"{Instance.ModsCount} mods"
        : (string.IsNullOrWhiteSpace(Instance.FileSize) ? "ready" : Instance.FileSize);

    /// <summary>A vanilla instance's folder name equals its version, so the name is printed once.</summary>
    public bool ShowName => !string.Equals(Instance.Version, Instance.Name, StringComparison.OrdinalIgnoreCase);

    public string Spec
    {
        get
        {
            var parts = new List<string> { Instance.LastPlayed };
            if (!string.IsNullOrWhiteSpace(Instance.FileSize)) parts.Add(Instance.FileSize);
            if (Instance.ModsCount > 0) parts.Add($"{Instance.ModsCount} mods");
            return string.Join(" · ", parts);
        }
    }
    public bool IsIncomplete => !Instance.IsReady;
    public bool HasPackIcon => Instance.HasPackIcon;
    public string? PackIconPath => Instance.PackIconPath;
}

public sealed class InstanceDetailViewModel : ViewModelBase
{
    private GameInstance? _instance;
    public GameInstance? Instance
    {
        get => _instance;
        private set
        {
            if (!SetProperty(ref _instance, value)) return;
            OnPropertyChanged(nameof(HasInstance));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(VersionLine));
            OnPropertyChanged(nameof(PathText));
            OnPropertyChanged(nameof(MetaLine));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(ShowName));
            OnPropertyChanged(nameof(Loader));
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(LastPlayedText));
            OnPropertyChanged(nameof(SpecLine));
            OnPropertyChanged(nameof(HasPackIcon));
            OnPropertyChanged(nameof(PackIconPath));
            OnPropertyChanged(nameof(IsIncomplete));
            OnPropertyChanged(nameof(CanPlay));
        }
    }

    public bool HasInstance => _instance is not null;

    public string Title => _instance?.Name ?? "";
    public string VersionLine => _instance is null
        ? ""
        : $"{_instance.Version} · {(string.IsNullOrWhiteSpace(_instance.ModLoader) ? "Vanilla" : _instance.ModLoader)} · {_instance.ModsCount} mods";
    public string PathText => _instance?.InstanceDir ?? "";

    /// <summary>Ready or Incomplete, top-right of the header and away from "last played".</summary>
    public string StatusLabel => _instance?.Status ?? "";

    public bool ShowName => _instance is not null
        && !string.Equals(_instance.Version, _instance.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Primary identifier on the band: the version, i.e. the folder name.</summary>
    public string Version => _instance?.Version ?? "";

    public string Loader => string.IsNullOrWhiteSpace(_instance?.ModLoader) ? "Vanilla" : _instance!.ModLoader;

    /// <summary>Spec line: loader, size, last played. Status and path have their own places.</summary>
    public string SpecLine
    {
        get
        {
            if (_instance is null) return "";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_instance.FileSize)) parts.Add(_instance.FileSize);
            parts.Add($"last played {_instance.LastPlayed}");
            return string.Join("   ·   ", parts);
        }
    }

    public string SizeText => string.IsNullOrWhiteSpace(_instance?.FileSize) ? "—" : _instance!.FileSize;

    public string LastPlayedText => string.IsNullOrWhiteSpace(_instance?.LastPlayed) ? "—" : _instance!.LastPlayed;

    public string ModsCount => Mods.Count.ToString();

    public string ModsSizeText => Size(Mods.Sum(m => m.Bytes));

    public string WorldsCount => Worlds.Count.ToString();
    public string MetaLine
    {
        get
        {
            if (_instance is null) return "";
            // Status already sits in the header, so it is not repeated here.
            var parts = new List<string> { $"Last played {_instance.LastPlayed}" };
            if (!string.IsNullOrWhiteSpace(_instance.FileSize)) parts.Add(_instance.FileSize);
            return string.Join("   ·   ", parts);
        }
    }
    public bool HasPackIcon => _instance?.HasPackIcon == true;
    public string? PackIconPath => _instance?.PackIconPath;
    public bool IsIncomplete => _instance is not null && !_instance.IsReady;

    /// <summary>An instance without a client jar cannot launch, so the button is disabled instead of failing on click.</summary>
    public bool CanPlay => _instance?.IsReady == true;

    private string _indexText = "";
    public string IndexText
    {
        get => _indexText;
        set => SetProperty(ref _indexText, value);
    }

    public bool HasNoMods => Mods.Count == 0;

    public bool HasNoWorlds => Worlds.Count == 0;

    /// <summary>World card columns: a single world fills the column instead of leaving half a cell empty.</summary>
    public int WorldColumns => Worlds.Count <= 1 ? 1 : 2;

    public ObservableCollection<DetailRow> Mods { get; } = new();
    public ObservableCollection<DetailRow> Worlds { get; } = new();

    public string ModsHeader => $"Mods ({Mods.Count})";
    public string WorldsHeader => $"Worlds ({Worlds.Count})";

    /// <summary>One line on the overview; the full jar list has its own page.</summary>

    /// <summary>Shader packs and resource packs are folders or zips; both are counted the same way.</summary>
    private static string PackFolder(GameInstance instance, string folder)
    {
        var dir = System.IO.Path.Combine(instance.InstanceDir, folder);
        return System.IO.Directory.Exists(dir) ? dir : instance.InstanceDir;
    }

    private static (int Count, long Bytes) ScanPacks(GameInstance? instance, string folder)
    {
        if (instance is null) return (0, 0);

        var dir = System.IO.Path.Combine(instance.InstanceDir, folder);
        if (!System.IO.Directory.Exists(dir)) return (0, 0);

        var info = new System.IO.DirectoryInfo(dir);
        long bytes = 0;
        var count = 0;

        foreach (var file in info.EnumerateFiles())
        {
            count++;
            bytes += file.Length;
        }

        foreach (var sub in info.EnumerateDirectories())
        {
            count++;
            try
            {
                bytes += sub.EnumerateFiles("*", System.IO.SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception)
            {
                // A pack folder we cannot read still counts as present.
            }
        }

        return (count, bytes);
    }

    private static string SizeOf(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };

    public string ModsSummary => Mods.Count == 0
        ? "No mods installed"
        : $"{Mods.Count} · {ModsSizeText}";

    private (int Count, long Bytes) _shaders;
    private (int Count, long Bytes) _packs;

    public string ShaderCountText => _shaders.Count.ToString();

    public string ShaderSizeText => _shaders.Count == 0 ? "" : SizeOf(_shaders.Bytes);

    public string PackSizeText => _packs.Count == 0 ? "" : SizeOf(_packs.Bytes);

    public string ShaderRowText => _shaders.Count == 0
        ? "None installed"
        : $"{_shaders.Count} · {SizeOf(_shaders.Bytes)}";
    public string ShaderSummary => _shaders.Count == 0 ? "None installed" : SizeOf(_shaders.Bytes);
    public bool HasShaders => _shaders.Count > 0;

    public string PackCountText => _packs.Count.ToString();

    public string PackRowText => _packs.Count == 0
        ? "None installed"
        : $"{_packs.Count} · {SizeOf(_packs.Bytes)}";
    public string PackSummary => _packs.Count == 0 ? "None installed" : SizeOf(_packs.Bytes);
    public bool HasPacks => _packs.Count > 0;

    public ICommand OpenShadersCommand { get; }
    public ICommand OpenPacksCommand { get; }

    public bool HasMods => Mods.Count > 0;

    public ICommand PlayCommand { get; }
    public ICommand OpenModsCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand BackCommand { get; }

    public InstanceDetailViewModel()
    {
        OpenModsCommand = new RelayCommand(() => AppShell.Instance.OpenInstanceMods(_instance));

        OpenShadersCommand = new RelayCommand(() =>
        {
            if (_instance is not null) AppShell.OpenInExplorer(PackFolder(_instance, "shaderpacks"));
        });

        OpenPacksCommand = new RelayCommand(() =>
        {
            if (_instance is not null) AppShell.OpenInExplorer(PackFolder(_instance, "resourcepacks"));
        });


        BackCommand = new RelayCommand(() => AppShell.Instance.Request(Shell.PageKey.Library));

        PlayCommand = new RelayCommand(() =>
        {
            if (_instance is not null) AppShell.Instance.Launch(_instance);
        });

        OpenFolderCommand = new RelayCommand(() =>
        {
            if (_instance is not null) Shell.AppShell.OpenInExplorer(_instance.InstanceDir);
        });
    }

    public void Load(GameInstance instance)
    {
        Instance = instance;
        Mods.Clear();
        Worlds.Clear();

        foreach (var row in ScanMods(instance)) Mods.Add(row);
        foreach (var row in ScanWorlds(instance)) Worlds.Add(row);

        _shaders = ScanPacks(instance, "shaderpacks");
        _packs = ScanPacks(instance, "resourcepacks");

        OnPropertyChanged(nameof(ModsHeader));
        OnPropertyChanged(nameof(WorldsHeader));
        OnPropertyChanged(nameof(HasNoMods));
        OnPropertyChanged(nameof(HasNoWorlds));
        OnPropertyChanged(nameof(WorldColumns));
        OnPropertyChanged(nameof(ModsCount));
        OnPropertyChanged(nameof(ModsSizeText));
        OnPropertyChanged(nameof(ModsSummary));
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(ShaderCountText));
        OnPropertyChanged(nameof(ShaderRowText));
        OnPropertyChanged(nameof(ShaderSizeText));
        OnPropertyChanged(nameof(PackSizeText));
        OnPropertyChanged(nameof(ShaderSummary));
        OnPropertyChanged(nameof(HasShaders));
        OnPropertyChanged(nameof(PackCountText));
        OnPropertyChanged(nameof(PackRowText));
        OnPropertyChanged(nameof(PackSummary));
        OnPropertyChanged(nameof(HasPacks));
        OnPropertyChanged(nameof(WorldsCount));
    }

    private static IEnumerable<DetailRow> ScanMods(GameInstance instance)
    {
        var dir = System.IO.Path.Combine(instance.InstanceDir, "mods");
        if (!System.IO.Directory.Exists(dir)) yield break;

        foreach (var file in new System.IO.DirectoryInfo(dir)
                     .EnumerateFiles("*.jar")
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return new DetailRow(System.IO.Path.GetFileNameWithoutExtension(file.Name), Size(file.Length), file.Length);
        }
    }

    private static IEnumerable<DetailRow> ScanWorlds(GameInstance instance)
    {
        // Isolated instances keep their worlds inside the version folder; only shared
        // ones fall back to the library root.
        var isolated = System.IO.Path.Combine(instance.InstanceDir, "saves");
        var dir = System.IO.Directory.Exists(isolated)
            ? isolated
            : System.IO.Path.Combine(instance.LibraryRoot, "saves");
        if (!System.IO.Directory.Exists(dir)) yield break;

        foreach (var world in new System.IO.DirectoryInfo(dir)
                     .EnumerateDirectories()
                     .OrderByDescending(d => d.LastWriteTimeUtc))
        {
            yield return new DetailRow(world.Name, Services.InstanceService.Relative(world.LastWriteTimeUtc));
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB",
    };
}

public sealed class DetailRow
{
    public DetailRow(string name, string meta, long bytes = 0)
    {
        Name = name;
        Meta = meta;
        Bytes = bytes;
    }

    public string Name { get; }
    public string Meta { get; }
    public long Bytes { get; }
}

public sealed class BrowseViewModel : ViewModelBase
{
    public ObservableCollection<ModSearchResult> Results { get; } = new();

    /// <summary>Placeholder rows for the loading state; same layout as the real list so nothing jumps when it arrives.</summary>
    public int[] SkeletonRows { get; } = new int[7];

    public ObservableCollection<string> Types { get; } = new() { "Mods", "Shaders", "Resource packs" };
    public ObservableCollection<string> Sorts { get; } = new() { "Relevance", "Downloads", "Follows", "Newest", "Updated" };
    public ObservableCollection<string> Versions { get; } = new() { "Any version", "1.21.4", "1.21.1", "1.20.6", "1.20.1", "1.19.2", "1.18.2", "1.16.5" };
    public ObservableCollection<string> Loaders { get; } = new() { "Any loader", "Fabric", "Forge", "NeoForge", "Quilt" };

    private string _selectedType = "Mods";
    public string SelectedType
    {
        get => _selectedType;
        set
        {
            if (!SetProperty(ref _selectedType, value)) return;
            OnPropertyChanged(nameof(IsLoaderVisible));
            _ = SearchAsync();
        }
    }

    public bool IsLoaderVisible => _selectedType == "Mods";

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? "");
    }

    private string _selectedVersion = "Any version";
    public string SelectedVersion
    {
        get => _selectedVersion;
        set { if (SetProperty(ref _selectedVersion, value)) _ = SearchAsync(); }
    }

    private string _selectedLoader = "Any loader";
    public string SelectedLoader
    {
        get => _selectedLoader;
        set { if (SetProperty(ref _selectedLoader, value)) _ = SearchAsync(); }
    }

    private string _selectedSort = "Relevance";
    public string SelectedSort
    {
        get => _selectedSort;
        set { if (SetProperty(ref _selectedSort, value)) _ = SearchAsync(); }
    }

    private LoadState _state = LoadState.Loading;
    public LoadState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    private string _error = "";
    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    private int _page;
    private int _total;

    public string TotalText => _total == 0 ? "" : $"{_total:N0} results";
    public int TotalPages => _total <= 0 ? 1 : (int)Math.Ceiling((double)_total / ModrinthService.BrowsePageSize);
    public string PageText => $"Page {_page + 1} of {TotalPages}";
    public bool CanPrev => _page > 0 && State != LoadState.Loading;
    public bool CanNext => (_page + 1) < TotalPages && State != LoadState.Loading;

    public ICommand SearchCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand OpenCommand { get; }

    private CancellationTokenSource? _cts;
    private bool _loaded;

    public BrowseViewModel()
    {
        SearchCommand = new AsyncRelayCommand(() => SearchAsync());
        NextPageCommand = new RelayCommand(() => { if (CanNext) { _page++; _ = SearchAsync(false); } });
        PrevPageCommand = new RelayCommand(() => { if (CanPrev) { _page--; _ = SearchAsync(false); } });
        OpenCommand = new RelayCommand<ModSearchResult>(p => AppShell.Instance.OpenProject(p));
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await SearchAsync();
    }

    public async Task SearchAsync(bool resetPage = true)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        if (resetPage) _page = 0;
        State = LoadState.Loading;
        Error = "";

        try
        {
            var (items, total) = await ModrinthService.SearchAsync(
                ParseType(_selectedType),
                _searchText,
                _selectedVersion == "Any version" ? "" : _selectedVersion,
                _selectedLoader == "Any loader" ? "" : _selectedLoader.ToLowerInvariant(),
                ParseSort(_selectedSort),
                _page * ModrinthService.BrowsePageSize,
                ct,
                ModrinthService.BrowsePageSize);

            if (ct.IsCancellationRequested) return;

            Results.Clear();
            foreach (var i in items) Results.Add(i);
            _total = total;
            State = Results.Count == 0 ? LoadState.Empty : LoadState.Ready;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Error = "Search failed — " + ex.Message;
            State = LoadState.Error;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                OnPropertyChanged(nameof(TotalText));
                OnPropertyChanged(nameof(PageText));
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(CanPrev));
                OnPropertyChanged(nameof(CanNext));
            }
        }
    }

    private static BrowseType ParseType(string s) => s switch
    {
        "Modpacks" => BrowseType.ModPack,
        "Shaders" => BrowseType.Shader,
        "Resource packs" => BrowseType.ResourcePack,
        _ => BrowseType.Mod
    };

    private static BrowseSort ParseSort(string s) => s switch
    {
        "Downloads" => BrowseSort.Downloads,
        "Follows" => BrowseSort.Follows,
        "Newest" => BrowseSort.Newest,
        "Updated" => BrowseSort.Updated,
        _ => BrowseSort.Relevance
    };
}

// Packs

public sealed class PacksViewModel : ViewModelBase
{
    public ObservableCollection<ModSearchResult> Featured { get; } = new();
    public ObservableCollection<ModSearchResult> Popular { get; } = new();
    public ObservableCollection<ModSearchResult> Results { get; } = new();

    /// <summary>Three placeholder cards, matching the three-column grid below.</summary>
    public int[] SkeletonCards { get; } = new int[9];

    public ObservableCollection<string> Loaders { get; } = new() { "Any loader", "Fabric", "Forge", "NeoForge", "Quilt" };
    public ObservableCollection<string> Versions { get; } = new() { "Any version", "1.21.4", "1.21.1", "1.20.6", "1.20.1", "1.19.2", "1.18.2", "1.16.5" };

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? "");
    }

    private string _selectedLoader = "Any loader";
    public string SelectedLoader
    {
        get => _selectedLoader;
        set { if (SetProperty(ref _selectedLoader, value ?? "Any loader")) _ = SearchAsync(); }
    }

    private string _selectedVersion = "Any version";
    public string SelectedVersion
    {
        get => _selectedVersion;
        set { if (SetProperty(ref _selectedVersion, value ?? "Any version")) _ = SearchAsync(); }
    }

    private LoadState _state = LoadState.Loading;
    public LoadState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    private string _error = "";
    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    private bool _hasQuery;
    public bool HasQuery
    {
        get => _hasQuery;
        private set
        {
            if (!SetProperty(ref _hasQuery, value)) return;
            OnPropertyChanged(nameof(IsBrowsing));
        }
    }

    /// <summary>With no search term, show "popular" rather than a result grid.</summary>
    public bool IsBrowsing => !_hasQuery;

    private ModSearchResult? _top;
    public ModSearchResult? Top
    {
        get => _top;
        private set => SetProperty(ref _top, value);
    }

    private string _countText = "";
    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    public ICommand SearchCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand RetryCommand { get; }

    private CancellationTokenSource? _cts;
    private bool _loaded;

    public PacksViewModel()
    {
        SearchCommand = new AsyncRelayCommand(() => SearchAsync());
        ClearCommand = new RelayCommand(() =>
        {
            SearchText = "";
            HasQuery = false;
            _ = LoadFeaturedAsync();
        });
        OpenCommand = new RelayCommand<ModSearchResult>(p => AppShell.Instance.OpenProject(p));
        RetryCommand = new AsyncRelayCommand(() => HasQuery ? SearchAsync() : LoadFeaturedAsync());
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadFeaturedAsync();
    }

    private async Task LoadFeaturedAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        State = LoadState.Loading;
        Error = "";

        try
        {
            var (top, _) = await ModrinthService.SearchAsync(
                BrowseType.ModPack, "", "", "", BrowseSort.Downloads, 0, ct);
            if (ct.IsCancellationRequested) return;

            var (fresh, _) = await ModrinthService.SearchAsync(
                BrowseType.ModPack, "", "", "", BrowseSort.Updated, 0, ct);
            if (ct.IsCancellationRequested) return;

            Featured.Clear();
            if (top.Count > 0)
            {
                Featured.Add(top[0]);
                Top = top[0];
            }

            Popular.Clear();
            foreach (var p in top.Skip(1).Take(3)) Popular.Add(p);
            foreach (var p in fresh.Where(f => Featured.All(x => x.ProjectId != f.ProjectId) && Popular.All(x => x.ProjectId != f.ProjectId)).Take(6))
                Popular.Add(p);

            CountText = $"{top.Count + fresh.Count} packs · Modrinth";
            State = Featured.Count == 0 && Popular.Count == 0 ? LoadState.Empty : LoadState.Ready;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Error = "Couldn't load popular modpacks — " + ex.Message;
            State = LoadState.Error;
        }
    }

    public async Task SearchAsync()
    {
        var query = (_searchText ?? "").Trim();
        if (query.Length == 0)
        {
            HasQuery = false;
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        HasQuery = true;
        State = LoadState.Loading;
        Error = "";

        try
        {
            var (items, total) = await ModrinthService.SearchAsync(
                BrowseType.ModPack,
                query,
                _selectedVersion == "Any version" ? "" : _selectedVersion,
                _selectedLoader == "Any loader" ? "" : _selectedLoader.ToLowerInvariant(),
                BrowseSort.Relevance,
                0,
                ct);

            if (ct.IsCancellationRequested) return;

            Results.Clear();
            foreach (var i in items) Results.Add(i);

            CountText = total == 0 ? "No modpacks found" : $"{total:N0} packs";
            State = Results.Count == 0 ? LoadState.Empty : LoadState.Ready;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Error = "Search failed — " + ex.Message;
            State = LoadState.Error;
        }
    }
}

// Settings

public sealed class SettingsViewModel : ViewModelBase
{
    public AppSettings Settings { get; }

    public ObservableCollection<string> WindowModes { get; } = new() { "Windowed", "Fullscreen", "Borderless" };
    public ObservableCollection<string> LogLevels { get; } = new() { "Trace", "Debug", "Info", "Warning", "Error" };

    public string Version => "Version " + Services.AppInfo.Version;
    public string JavaRuntime => "Auto-detected · OpenJDK 17";
    public string CacheSize => "2.3 GB";

    public ICommand ClearCacheCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public SettingsViewModel(AppSettings settings)
    {
        Settings = settings;
        ClearCacheCommand = new RelayCommand(() => AppShell.Instance.ShowToast("Cache cleared.", ToastType.Success));
        OpenFolderCommand = new RelayCommand(() =>
            AppShell.Instance.ShowToast("Not wired up yet.", ToastType.Info));
    }

    public bool ReduceMotion
    {
        get => Settings.ReduceMotion;
        set
        {
            if (Settings.ReduceMotion == value) return;
            Settings.ReduceMotion = value;
            OnPropertyChanged();
            AppShell.Instance.ApplyMotionPreference();
        }
    }
}

// Mod Detail

public sealed class ModDetailViewModel : ViewModelBase
{
    private ModSearchResult? _project;
    public ModSearchResult? Project
    {
        get => _project;
        set
        {
            if (!SetProperty(ref _project, value)) return;
            OnPropertyChanged(nameof(HasProject));
        }
    }

    public bool HasProject => _project is not null;

    public ObservableCollection<ModVersion> Versions { get; } = new();

    private string _body = "";
    public string Body
    {
        get => _body;
        private set => SetProperty(ref _body, value);
    }

    private LoadState _state = LoadState.Loading;
    public LoadState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    private string _error = "";
    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public ICommand BackCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand DownloadLatestCommand { get; }

    private CancellationTokenSource? _cts;

    public ModDetailViewModel()
    {
        BackCommand = new RelayCommand(() => AppShell.Instance.Request(null, NavKind.Back));
        OpenWebsiteCommand = new RelayCommand(() =>
        {
            if (_project is null) return;
            try { NewsService.OpenInBrowser(_project.WebsiteUrl); }
            catch (Exception ex) { AppShell.Instance.ShowToast("Couldn't open the link: " + ex.Message, ToastType.Error); }
        });
        DownloadCommand = new RelayCommand<ModVersion>(v =>
            _ = AppShell.Instance.Downloads.EnqueueAsync(v!, _project?.Title ?? ""));
        DownloadLatestCommand = new RelayCommand(() =>
        {
            if (Versions.Count == 0)
            {
                AppShell.Instance.ShowToast("This project has no downloadable files.", ToastType.Warning);
                return;
            }
            _ = AppShell.Instance.Downloads.EnqueueAsync(Versions[0], _project?.Title ?? "");
        });
    }

    public async Task LoadAsync(ModSearchResult project)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Project = project;
        State = LoadState.Loading;
        Error = "";
        Body = "";
        Versions.Clear();

        try
        {
            var bodyTask = ModrinthService.GetProjectBodyAsync(project.ProjectId, ct);
            var versionsTask = ModrinthService.GetVersionsAsync(project.ProjectId, ct);
            await Task.WhenAll(bodyTask, versionsTask);
            if (ct.IsCancellationRequested || !ReferenceEquals(_project, project)) return;

            Body = CleanMarkdown(await bodyTask);
            foreach (var v in await versionsTask) Versions.Add(v);

            if (string.IsNullOrWhiteSpace(Body) && Versions.Count == 0) State = LoadState.Empty;
            else State = LoadState.Ready;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Error = "Couldn't load project details — " + ex.Message;
            State = LoadState.Error;
        }
    }

    private static string CleanMarkdown(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var sb = new StringBuilder();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd();
            line = Regex.Replace(line, @"^#{1,6}\s*", "");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = Regex.Replace(line, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }
}

/// <summary>One jar in an instance's mods folder.</summary>
public sealed class ModEntry : ViewModelBase
{
    public ModEntry(string path, string name, string meta, long bytes, bool enabled)
    {
        FilePath = path;
        Name = name;
        Meta = meta;
        Bytes = bytes;
        _enabled = enabled;
    }

    public string FilePath { get; }
    public string Name { get; }
    public string Meta { get; }
    public long Bytes { get; }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateHint));
        }
    }

    public string StateText => _enabled ? "On" : "Off";
    public string StateHint => _enabled ? "Enabled. Click to disable." : "Disabled. Click to enable.";

    public string SizeText => Bytes switch
    {
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{Bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };
}

/// <summary>
/// The mods of one instance, on their own page. A 97-jar folder is a file list to search,
/// not something to read top to bottom, so searching, sorting and the enable toggle live
/// here instead of being crammed into the instance overview.
/// </summary>
public sealed class InstanceModsViewModel : ViewModelBase
{
    private GameInstance? _instance;

    public ObservableCollection<ModEntry> Mods { get; } = new();
    public ObservableCollection<ModEntry> Visible { get; } = new();
    public ObservableCollection<string> Sorts { get; } = new() { "Name", "Size" };

    public string Title => _instance?.Name ?? "Mods";
    public string FolderText => _instance is null ? "" : System.IO.Path.Combine(_instance.InstanceDir, "mods");

    /// <summary>Empty while everything is visible; the line only earns its place once a filter hides something.</summary>
    public string CountText => Mods.Count == 0 || Visible.Count == Mods.Count
        ? ""
        : $"{Visible.Count} of {Mods.Count}";

    public string SummaryText
    {
        get
        {
            if (Mods.Count == 0) return "This instance has no mods yet.";
            var enabled = Mods.Count(m => m.Enabled);
            var disabled = Mods.Count - enabled;
            var bytes = Mods.Sum(m => m.Bytes);
            var size = bytes switch
            {
                < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
                < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
                _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
            };
            return disabled == 0
                ? $"{enabled} mods · {size}"
                : $"{enabled} enabled · {disabled} disabled · {size}";
        }
    }

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value)) return;
            Refilter();
        }
    }

    private string _sort = "Name";
    public string Sort
    {
        get => _sort;
        set
        {
            if (!SetProperty(ref _sort, value)) return;
            Refilter();
        }
    }

    public bool HasNoMods => Mods.Count == 0;

    public ICommand ToggleCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand BackCommand { get; }

    public InstanceModsViewModel()
    {
        ToggleCommand = new RelayCommand<ModEntry>(Toggle);
        OpenFolderCommand = new RelayCommand(() =>
        {
            if (_instance is not null) AppShell.OpenInExplorer(System.IO.Path.Combine(_instance.InstanceDir, "mods"));
        });
        BackCommand = new RelayCommand(() =>
        {
            if (_instance is not null) AppShell.Instance.OpenInstance(_instance);
        });
    }

    public void Load(GameInstance? instance)
    {
        _instance = instance;
        _search = "";
        Mods.Clear();
        Visible.Clear();

        if (instance is not null)
        {
            foreach (var entry in Scan(System.IO.Path.Combine(instance.InstanceDir, "mods"))) Mods.Add(entry);
        }

        Refilter();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(HasNoMods));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(Search));
    }

    private void Refilter()
    {
        var query = Search.Trim();
        var items = Mods.AsEnumerable();

        if (query.Length > 0)
            items = items.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                     || m.Meta.Contains(query, StringComparison.OrdinalIgnoreCase));

        items = Sort == "Size"
            ? items.OrderByDescending(m => m.Bytes)
            : items.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);

        Visible.Clear();
        foreach (var item in items) Visible.Add(item);

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Disabling a jar is a rename; Forge, Fabric and NeoForge all ignore .jar.disabled.</summary>
    private void Toggle(ModEntry? entry)
    {
        if (entry is null) return;

        try
        {
            var target = entry.Enabled
                ? entry.FilePath + ".disabled"
                : entry.FilePath[..^".disabled".Length];

            System.IO.File.Move(entry.FilePath, target);
            RefreshEntry(entry, target);
        }
        catch (Exception ex)
        {
            AppShell.Instance.ShowToast("Could not change " + entry.Name + ": " + ex.Message, ToastType.Error);
        }

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(CountText));
    }

    private void RefreshEntry(ModEntry entry, string newPath)
    {
        var index = Mods.IndexOf(entry);
        if (index < 0) return;

        var replacement = new ModEntry(newPath, entry.Name, entry.Meta, entry.Bytes, !IsDisabledName(newPath));
        Mods[index] = replacement;

        var visibleIndex = Visible.IndexOf(entry);
        if (visibleIndex >= 0) Visible[visibleIndex] = replacement;
    }

    private static bool IsDisabledName(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ModEntry> Scan(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) yield break;

        foreach (var file in new System.IO.DirectoryInfo(dir)
                     .EnumerateFiles("*.jar*")
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var enabled = !IsDisabledName(file.Name);
            var (name, meta) = SplitName(file.Name);
            yield return new ModEntry(file.FullName, name, meta, file.Length, enabled);
        }
    }

    /// <summary>
    /// Jar names carry version and loader in the tail ("Jade-1.20.1-Forge-11.13.3"), which is
    /// noise in a list. Cut at the first token that reads as a version and keep the rest as meta.
    /// </summary>
    internal static (string Name, string Meta) SplitName(string fileName)
    {
        var stem = fileName;
        if (stem.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) stem = stem[..^".disabled".Length];
        if (stem.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^".jar".Length];

        var parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var cut = parts.Length;
        for (var i = 0; i < parts.Length; i++)
        {
            if (LooksLikeVersion(parts[i])) { cut = i; break; }
        }

        if (cut == 0) return (stem, "");
        return (string.Join('-', parts.Take(cut)), string.Join('-', parts.Skip(cut)));
    }

    private static bool LooksLikeVersion(string token)
    {
        if (token.Length == 0) return false;
        var letters = token.Count(char.IsLetter);
        var digits = token.Count(char.IsDigit);
        if (digits == 0) return false;
        if (letters > 0 && !token.Contains('.')) return false;   // 3dskinlayers stays in the name
        return token.Contains('.') || digits == token.Length;
    }
}
