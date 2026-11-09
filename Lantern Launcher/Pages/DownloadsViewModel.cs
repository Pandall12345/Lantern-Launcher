using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LanternLauncher.Common;
using LanternLauncher.Models;
using LanternLauncher.Services;
using LanternLauncher.Shell;

namespace LanternLauncher.Pages;

/// <summary>
/// Download tasks and their history. Two lists only: what is running and what already
/// finished. Nothing queues, because nothing caps concurrency - a queued section would
/// always be empty.
/// </summary>
public sealed class DownloadsViewModel : ViewModelBase
{
    private const int HistoryLimit = 30;
    private const int ProgressIntervalMs = 200;
    private const long MinFreeSpaceMargin = 64L * 1024 * 1024;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly DispatcherTimer _smoother;

    public ObservableCollection<DownloadTask> Active { get; } = new();
    public ObservableCollection<DownloadTask> Recent { get; } = new();

    public bool HasActive => Active.Count > 0;
    public bool HasRecent => Recent.Count > 0;
    public bool IsEmpty => Active.Count == 0 && Recent.Count == 0;

    // Add download overlay
    private bool _isAddOpen;
    public bool IsAddOpen
    {
        get => _isAddOpen;
        private set => SetProperty(ref _isAddOpen, value);
    }

    private bool _nameIsAuto = true;

    private string _newUrl = "";
    public string NewUrl
    {
        get => _newUrl;
        set
        {
            if (!SetProperty(ref _newUrl, value)) return;

            if (_nameIsAuto)
            {
                var guess = GuessFileName(value);
                if (guess.Length > 0)
                {
                    _newFileName = guess;
                    OnPropertyChanged(nameof(NewFileName));
                }
            }

            OverlayError = null;
            RaiseCanStart();
        }
    }

    private string _newFolder = DefaultDownloadFolder;
    public string NewFolder
    {
        get => _newFolder;
        set { if (SetProperty(ref _newFolder, value)) { OverlayError = null; RaiseCanStart(); } }
    }

    private string _newFileName = "";
    public string NewFileName
    {
        get => _newFileName;
        set
        {
            if (!SetProperty(ref _newFileName, value)) return;
            _nameIsAuto = false;
            OverlayError = null;
            RaiseCanStart();
        }
    }

    private string? _overlayError;
    public string? OverlayError
    {
        get => _overlayError;
        private set { if (SetProperty(ref _overlayError, value)) OnPropertyChanged(nameof(HasOverlayError)); }
    }

    public bool HasOverlayError => !string.IsNullOrEmpty(OverlayError);

    public bool CanStartCustom => string.IsNullOrWhiteSpace(OverlayError)
                                  && LooksLikeUrl(NewUrl)
                                  && !string.IsNullOrWhiteSpace(NewFileName);

    // Commands
    public AsyncRelayCommand AddDownloadCommand { get; }
    public RelayCommand CloseOverlayCommand { get; }
    public AsyncRelayCommand StartCustomCommand { get; }
    public RelayCommand<DownloadTask> DeleteCommand { get; }
    public AsyncRelayCommand<DownloadTask> RetryCommand { get; }
    public RelayCommand<DownloadTask> OpenLocationCommand { get; }
    public RelayCommand ClearRecordsCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand BrowseModpacksCommand { get; }

    public DownloadsViewModel()
    {
        AddDownloadCommand = new AsyncRelayCommand(OpenOverlayAsync);
        CloseOverlayCommand = new RelayCommand(CloseOverlay);
        StartCustomCommand = new AsyncRelayCommand(StartCustomAsync);
        DeleteCommand = new RelayCommand<DownloadTask>(Delete);
        RetryCommand = new AsyncRelayCommand<DownloadTask>(RetryAsync);
        OpenLocationCommand = new RelayCommand<DownloadTask>(OpenLocation);
        ClearRecordsCommand = new RelayCommand(ClearRecords);
        OpenFolderCommand = new RelayCommand(() => AppShell.OpenInExplorer(DefaultDownloadFolder));
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        BrowseModpacksCommand = new RelayCommand(() => AppShell.Instance.Request(new Route(PageKey.Packs)));

        Active.CollectionChanged += (_, _) => RaiseCounts();
        Recent.CollectionChanged += (_, _) => RaiseCounts();

        // A remembered folder that has since been deleted is worse than none: fall back.
        var remembered = SettingsService.Instance.Load().DownloadFolder;
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            if (Directory.Exists(remembered)) _newFolder = remembered;
            else Remember(DefaultDownloadFolder);
        }

        LoadHistory();

        _smoother = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _smoother.Tick += (_, _) => StepSmoothing();
        _smoother.Start();
    }

    public string DefaultFolder => DefaultDownloadFolder;

    public static string DefaultDownloadFolder
    {
        get
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    // Add download
    private Task OpenOverlayAsync()
    {
        IsAddOpen = true;
        return Task.CompletedTask;
    }

    private void CloseOverlay()
    {
        IsAddOpen = false;
        ResetForm();
    }

    /// <summary>Dismissing the overlay drops the half-typed link and name; the folder stays.</summary>
    private void ResetForm()
    {
        OverlayError = null;
        _nameIsAuto = true;
        NewUrl = "";
        NewFileName = "";
    }

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose download folder" };
        if (Directory.Exists(NewFolder)) dialog.InitialDirectory = NewFolder;
        if (dialog.ShowDialog() != true) return;

        NewFolder = dialog.FolderName;
        Remember(NewFolder);
    }

    private static void Remember(string folder)
    {
        var settings = SettingsService.Instance.Load();
        if (string.Equals(settings.DownloadFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        settings.DownloadFolder = folder;
        SettingsService.Instance.Save(settings);
    }

    private async Task StartCustomAsync()
    {
        if (!LooksLikeUrl(NewUrl))
        {
            OverlayError = "Enter an http or https URL.";
            return;
        }

        var name = SanitizeFileName(NewFileName);
        if (name.Length == 0)
        {
            OverlayError = "Enter a file name.";
            return;
        }

        var folder = string.IsNullOrWhiteSpace(NewFolder) ? DefaultDownloadFolder : NewFolder.Trim();

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            OverlayError = "Cannot use that folder: " + ex.Message;
            return;
        }

        Remember(folder);

        var target = UniquePath(Path.Combine(folder, name));
        var task = new DownloadTask
        {
            Title = Path.GetFileName(target),
            FileName = Path.GetFileName(target),
            Url = NewUrl.Trim(),
            TargetPath = target
        };

        IsAddOpen = false;
        ResetForm();
        await StartAsync(task);
    }

    // Entry points used by Browse and Modpacks
    public async Task EnqueueAsync(ModVersion version, string projectTitle)
    {
        if (version is null || string.IsNullOrWhiteSpace(version.DownloadUrl))
        {
            AppShell.Instance.ShowToast("No file available for this version.", ToastType.Warning);
            return;
        }

        var ext = Path.GetExtension(version.FileName);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = version.FileName,
            Title = "Save " + version.VersionNumber,
            Filter = string.IsNullOrEmpty(ext)
                ? "All files|*.*"
                : $"{ext.TrimStart('.').ToUpperInvariant()} files|*{ext}|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var label = string.IsNullOrWhiteSpace(projectTitle)
            ? version.VersionNumber
            : $"{projectTitle} {version.VersionNumber}";

        var task = new DownloadTask
        {
            Title = label,
            FileName = Path.GetFileName(dialog.FileName),
            Url = version.DownloadUrl,
            TargetPath = dialog.FileName,
            TotalBytes = version.SizeBytes
        };

        await StartAsync(task);
    }

    // Running a task
    private async Task StartAsync(DownloadTask task)
    {
        if (!HasRoomFor(task.TotalBytes, task.TargetPath, out var spaceError))
        {
            task.State = DownloadState.Failed;
            task.Error = spaceError;
            task.FinishedAt = DateTime.Now;
            AddRecent(task);
            AppShell.Instance.ShowToast(spaceError, ToastType.Error);
            return;
        }

        task.State = DownloadState.Connecting;
        task.StartedAt = DateTime.Now;
        Active.Insert(0, task);

        var cts = new CancellationTokenSource();
        _running[task.Id] = cts;

        try
        {
            await DownloadAsync(task, cts.Token);
        }
        finally
        {
            _running.Remove(task.Id);
        }
    }

    private async Task DownloadAsync(DownloadTask task, CancellationToken token)
    {
        var temp = task.TargetPath + ".part";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await TransferAsync(task, temp, token);
                Verify(task, temp);

                if (File.Exists(task.TargetPath)) File.Delete(task.TargetPath);
                File.Move(temp, task.TargetPath);

                task.State = DownloadState.Completed;
                task.FinishedAt = DateTime.Now;
                task.SpeedBytesPerSecond = 0;
                task.ActiveConnections = 0;
                MoveToRecent(task);
                SaveHistory();
                NotifyOutside("Saved " + task.FileName, ToastType.Success);
                return;
            }
            catch (OperationCanceledException)
            {
                CleanupTemp(temp);
                task.State = DownloadState.Cancelled;
                task.FinishedAt = DateTime.Now;
                task.SpeedBytesPerSecond = 0;
                MoveToRecent(task);
                SaveHistory();
                return;
            }
            catch (ChecksumException ex)
            {
                CleanupTemp(temp);
                if (attempt == 0)
                {
                    task.ReceivedBytes = 0;
                    task.SmoothProgress = 0;
                    continue;
                }

                Fail(task, ex);
                return;
            }
            catch (Exception ex)
            {
                CleanupTemp(temp);
                Fail(task, ex);
                return;
            }
        }
    }

    private async Task TransferAsync(DownloadTask task, string temp, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        ClassifyAndThrow(response);

        var total = response.Content.Headers.ContentLength ?? task.TotalBytes;
        task.TotalBytes = total;

        // A URL path such as /__down tells us nothing. When the server names the file, use that.
        var served = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (!string.IsNullOrWhiteSpace(served) && !Path.HasExtension(task.FileName))
        {
            var folder = Path.GetDirectoryName(task.TargetPath) ?? DefaultDownloadFolder;
            task.TargetPath = UniquePath(Path.Combine(folder, SanitizeFileName(served)));
            task.FileName = Path.GetFileName(task.TargetPath);
            task.Title = task.FileName;
            task.Raise(nameof(DownloadTask.Title), nameof(DownloadTask.FileName));
        }

        if (!HasRoomFor(total, temp, out var spaceError)) throw new IOException(spaceError);

        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        task.State = DownloadState.Downloading;

        var buffer = new byte[81920];
        var lastUi = Environment.TickCount64;
        var lastBytes = 0L;
        var lastTick = lastUi;
        var speed = 0.0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read <= 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), token);
            task.ReceivedBytes += read;

            var now = Environment.TickCount64;
            if (now - lastUi < ProgressIntervalMs) continue;

            var elapsed = Math.Max(1, now - lastTick) / 1000.0;
            speed = (task.ReceivedBytes - lastBytes) / elapsed;
            task.SpeedBytesPerSecond = speed;

            lastUi = now;
            lastTick = now;
            lastBytes = task.ReceivedBytes;
            task.Raise(nameof(DownloadTask.SpeedText), nameof(DownloadTask.EtaText));
        }

        task.TotalBytes = task.ReceivedBytes > 0 ? task.ReceivedBytes : task.TotalBytes;
        task.SmoothProgress = 100;
    }

    private static void Verify(DownloadTask task, string temp)
    {
        if (string.IsNullOrWhiteSpace(task.Sha1)) return;
        task.State = DownloadState.Verifying;

        using var stream = File.OpenRead(temp);
        var hash = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, task.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new ChecksumException("Checksum mismatch.");
    }

    private void Fail(DownloadTask task, Exception ex)
    {
        task.State = DownloadState.Failed;
        task.Error = Describe(ex);
        task.FinishedAt = DateTime.Now;
        task.SpeedBytesPerSecond = 0;
        task.ActiveConnections = 0;
        MoveToRecent(task);
        SaveHistory();
        NotifyOutside("Download failed: " + task.Error, ToastType.Error);
    }

    // Task actions
    private void Delete(DownloadTask? task)
    {
        if (task is null) return;

        if (task.IsActive)
        {
            // Cancelling is one click: stop the transfer, drop the partial file, drop the row.
            if (_running.TryGetValue(task.Id, out var cts)) cts.Cancel();
            CleanupTemp(task.TargetPath + ".part");
            Active.Remove(task);
            RaiseCounts();
            return;
        }

        Recent.Remove(task);
        SaveHistory();
        RaiseCounts();
    }

    private async Task RetryAsync(DownloadTask? task)
    {
        if (task is null || !task.CanRetry) return;

        Recent.Remove(task);
        RaiseCounts();

        task.Error = null;
        task.ReceivedBytes = 0;
        task.SmoothProgress = 0;
        task.FinishedAt = null;
        await StartAsync(task);
    }

    private void OpenLocation(DownloadTask? task)
    {
        if (task is null) return;

        if (File.Exists(task.TargetPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + task.TargetPath + "\"",
                    UseShellExecute = true
                });
                return;
            }
            catch (Exception)
            {
                // fall through to the containing folder
            }
        }

        var folder = Path.GetDirectoryName(task.TargetPath);
        if (!string.IsNullOrEmpty(folder)) AppShell.OpenInExplorer(folder);
    }

    private void ClearRecords()
    {
        Recent.Clear();
        SaveHistory();
        RaiseCounts();
    }

    /// <summary>Called when the window is closing. Returns false to keep it open.</summary>
    public bool PrepareExit()
    {
        if (!HasActive) return true;

        var answer = MessageBox.Show(
            "Downloads are still running. Closing the launcher cancels them.",
            "Close Lantern Launcher",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return false;

        foreach (var cts in _running.Values) cts.Cancel();
        return true;
    }

    // History
    private void MoveToRecent(DownloadTask task)
    {
        Active.Remove(task);
        Recent.Insert(0, task);

        while (Recent.Count > HistoryLimit) Recent.RemoveAt(Recent.Count - 1);
        RaiseCounts();
    }

    private void AddRecent(DownloadTask task)
    {
        Recent.Insert(0, task);
        while (Recent.Count > HistoryLimit) Recent.RemoveAt(Recent.Count - 1);
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
    }

    public string SummaryText
    {
        get
        {
            if (!HasActive) return "";

            var speed = Active.Sum(t => t.SpeedBytesPerSecond);
            var count = Active.Count == 1 ? "1 download" : $"{Active.Count} downloads";
            return speed >= 512 ? $"{count} · {DownloadTask.Size(speed)}/s" : count;
        }
    }

    private void StepSmoothing()
    {
        var running = false;

        foreach (var task in Active)
        {
            var delta = task.Progress - task.SmoothProgress;
            if (Math.Abs(delta) < 0.15)
            {
                task.SmoothProgress = task.Progress;
            }
            else
            {
                task.SmoothProgress += delta * 0.32;
                running = true;
            }

            // Figures follow the same idea as the bar: chase the real value, never snap to it.
            task.DisplayReceived = Chase(task.DisplayReceived, task.ReceivedBytes);
            task.DisplaySpeed = Chase(task.DisplaySpeed, task.SpeedBytesPerSecond);
        }

        if (running) OnPropertyChanged(nameof(SummaryText));
    }

    private static double Chase(double current, double target) =>
        Math.Abs(target - current) < 1 ? target : current + (target - current) * 0.26;

    private void LoadHistory()
    {
        try
        {
            var path = AppInfo.SettingsFile("downloads.json");
            if (!File.Exists(path)) return;

            var records = JsonSerializer.Deserialize<List<DownloadTask>>(File.ReadAllText(path));
            if (records is null) return;

            foreach (var record in records.Take(HistoryLimit))
            {
                record.State = record.State is DownloadState.Completed or DownloadState.Failed
                    or DownloadState.Cancelled ? record.State : DownloadState.Cancelled;
                record.FinishedAt ??= record.StartedAt;
                Recent.Add(record);
            }

            RaiseCounts();
        }
        catch (Exception)
        {
            // A corrupt history file is not worth an error dialog; start clean.
        }
    }

    private void SaveHistory()
    {
        try
        {
            var path = AppInfo.SettingsFile("downloads.json");
            File.WriteAllText(path, JsonSerializer.Serialize(Recent.Take(HistoryLimit).ToList()));
        }
        catch (Exception)
        {
            // History is a convenience; never let it break a download.
        }
    }

    // Helpers
    private void NotifyOutside(string message, ToastType type)
    {
        if (AppShell.Instance.CurrentRoute.Key == PageKey.Downloads) return;
        AppShell.Instance.ShowToast(message, type);
    }

    private static void ClassifyAndThrow(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        throw (int)response.StatusCode switch
        {
            403 => new UnauthorizedAccessException("The server refused the request (403)."),
            404 => new FileNotFoundException("The file is gone (404)."),
            429 => new IOException("Too many requests (429). Try again in a minute."),
            >= 500 => new IOException($"The server failed ({(int)response.StatusCode})."),
            _ => new IOException($"Request failed ({(int)response.StatusCode}).")
        };
    }

    private static string Describe(Exception ex) => ex switch
    {
        ChecksumException => "Checksum mismatch.",
        UnauthorizedAccessException => ex.Message,
        FileNotFoundException => ex.Message,
        TaskCanceledException => "Timed out.",
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) => io.Message,
        HttpRequestException http => "Network error: " + http.Message,
        _ => ex.Message
    };

    private static bool HasRoomFor(long bytes, string path, out string error)
    {
        error = "";
        if (bytes <= 0) return true;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return true;

            if (drive.AvailableFreeSpace < bytes + MinFreeSpaceMargin)
            {
                error = $"Not enough space on {drive.Name.TrimEnd('\\')}: needs {FormatHelper.Bytes(bytes)}, free {FormatHelper.Bytes(drive.AvailableFreeSpace)}.";
                return false;
            }
        }
        catch (Exception)
        {
            // Cannot measure: do not block the download over it.
        }

        return true;
    }

    private static void CleanupTemp(string temp)
    {
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception)
        {
            // Leftover temp files are harmless.
        }
    }

    private static bool LooksLikeUrl(string url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string GuessFileName(string url)
    {
        if (!LooksLikeUrl(url)) return "";
        var name = Uri.UnescapeDataString(Path.GetFileName(new Uri(url.Trim()).AbsolutePath));
        return SanitizeFileName(name);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var cleaned = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '-');
        return cleaned.Trim('.', ' ');
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return path;
    }

    private void RaiseCanStart() => OnPropertyChanged(nameof(CanStartCustom));

    private sealed class ChecksumException(string message) : Exception(message);
}
