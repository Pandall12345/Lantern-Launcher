using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LanternLauncher.Common;
using LanternLauncher.Pages;
using LanternLauncher.Models;
using LanternLauncher.Services;

namespace LanternLauncher.Shell;

public enum ToastType { Info, Success, Warning, Error }

public sealed class ToastMessage
{
    public string Message { get; set; } = "";
    public ToastType Type { get; set; }
}

/// <summary>
/// Application shell: owns settings, the route stack, toasts and every page view model.
/// Navigation starts here; MainWindow performs the actual page transition.
/// </summary>
public sealed class AppShell : ViewModelBase
{
    public static AppShell Instance { get; } = new();

    public AppSettings Settings { get; }
    public NavigationHost Navigation { get; } = new();
    public ObservableCollection<ToastMessage> Toasts { get; } = new();

    public HomeViewModel Home { get; }
    public LibraryViewModel Library { get; }
    public BrowseViewModel Browse { get; }
    public PacksViewModel Packs { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel SettingsPage { get; }
    public ModDetailViewModel Detail { get; }
    public InstanceDetailViewModel InstanceDetail { get; }
    public InstanceModsViewModel InstanceMods { get; }

    private Route _currentRoute = new(PageKey.Home);
    public Route CurrentRoute
    {
        get => _currentRoute;
        private set
        {
            if (_currentRoute == value) return;
            _currentRoute = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
        }
    }

    public string PageTitle => CurrentRoute.Key == PageKey.ModDetail && Detail.Project is { } p
        ? p.Title
        : PageInfo.Title(CurrentRoute.Key);

    public string PageSubtitle => PageInfo.Subtitle(CurrentRoute.Key);

    /// <summary>Requests navigation. A null route means "go back".</summary>
    public event Action<Route?, NavKind>? NavigationRequested;

    private AppShell()
    {
        Settings = SettingsService.Instance.Load();
        Settings.PropertyChanged += (_, _) => SettingsService.Instance.Save(Settings);

        Home = new HomeViewModel();
        Library = new LibraryViewModel();
        Browse = new BrowseViewModel();
        Packs = new PacksViewModel();
        Downloads = new DownloadsViewModel();
        SettingsPage = new SettingsViewModel(Settings);
        Detail = new ModDetailViewModel();
        InstanceDetail = new InstanceDetailViewModel();
        InstanceMods = new InstanceModsViewModel();
    }

    // Navigation
    public void Request(Route? route, NavKind kind = NavKind.Normal) =>
        NavigationRequested?.Invoke(route, kind);

    public void Request(PageKey key) => Request(new Route(key));

    /// <summary>Called by MainWindow once the transition settles: this is the page actually on screen.</summary>
    public void SetDisplayed(Route route)
    {
        CurrentRoute = route;

        // Fetch on first appearance so startup does not hit the network.
        _ = route.Key switch
        {
            PageKey.Home => Home.LoadAsync(),
            PageKey.Library => Library.LoadAsync(),
            PageKey.Packs => Packs.LoadAsync(),
            PageKey.Browse => Browse.LoadAsync(),
            _ => System.Threading.Tasks.Task.CompletedTask
        };
    }

    public void OpenProject(ModSearchResult? project)
    {
        if (project is null) return;
        Detail.Project = project;                     // placeholder first, so the title exists during the transition
        Request(new Route(PageKey.ModDetail, project), NavKind.Normal);
        _ = Detail.LoadAsync(project);
    }

    /// <summary>Opens the instance detail page. The instance arrives as the argument; the page reads no global state.</summary>
    public void OpenInstance(GameInstance? instance, string indexText = "")
    {
        if (instance is null) return;
        InstanceDetail.Load(instance);
        InstanceDetail.IndexText = indexText;
        Request(new Route(PageKey.InstanceDetail, instance), NavKind.Normal);
    }

    /// <summary>Opens the mod list of an instance. It is its own page because a full jar folder is a browsing job.</summary>
    public void OpenInstanceMods(GameInstance? instance)
    {
        if (instance is null) return;
        InstanceMods.Load(instance);
        Request(new Route(PageKey.InstanceMods, instance), NavKind.Normal);
    }

    /// <summary>Reveals a folder in Explorer, falling back to its parent when the folder is gone.</summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            var target = System.IO.Directory.Exists(path)
                ? path
                : System.IO.Path.GetDirectoryName(path);

            if (string.IsNullOrWhiteSpace(target) || !System.IO.Directory.Exists(target)) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Failing to open Explorer must never interrupt anything else.
        }
    }

    // A fresh service per launch, built from the instance's own library: each library keeps its
    // own libraries and assets, and sharing one instance would look in the wrong .minecraft.
    private LaunchService LauncherFor(GameInstance instance)
        => new(new VersionService(string.IsNullOrWhiteSpace(instance.LibraryRoot)
            ? GameLibraries.LanternRoot
            : instance.LibraryRoot));
    private bool _launchBusy;

    public async void Launch(GameInstance? instance)
    {
        if (instance is null) return;
        if (_launchBusy)
        {
            ShowToast("A launch is already running", ToastType.Warning);
            return;
        }

        var versionId = string.IsNullOrWhiteSpace(instance.VersionId) ? instance.Version : instance.VersionId;
        _launchBusy = true;
            ShowToast($"Launching {instance.Name}", ToastType.Info);
        try
        {
            var outcome = await System.Threading.Tasks.Task.Run(
                () => LauncherFor(instance).Launch(versionId, LaunchProfile.Default));

            if (!outcome.Started)
            ShowToast("Launch failed: " + (outcome.Error ?? "unknown error"), ToastType.Error);
            else
            ShowToast($"{instance.Name} is running", ToastType.Success);
        }
        catch (Exception ex)
        {
            ShowToast("Launch error: " + ex.Message, ToastType.Error);
        }
        finally
        {
            _launchBusy = false;
        }
    }

    public void OpenNews(NewsItem? item)
    {
        if (item is null) return;
        if (!NewsService.IsSafeNewsUrl(item.Url))
        {
            ShowToast("Blocked an untrusted news link.", ToastType.Warning);
            return;
        }
        try { NewsService.OpenInBrowser(item.Url); }
        catch (Exception ex) { ShowToast("Couldn't open the link: " + ex.Message, ToastType.Error); }
    }

    public void ShowToast(string message, ToastType type = ToastType.Info) =>
        Toasts.Add(new ToastMessage { Message = message, Type = type });

    public void DismissToast(ToastMessage message) => Toasts.Remove(message);

    public void ApplyMotionPreference() => OnPropertyChanged(nameof(ReduceMotion));

    public bool ReduceMotion => Settings.ReduceMotion;
}
