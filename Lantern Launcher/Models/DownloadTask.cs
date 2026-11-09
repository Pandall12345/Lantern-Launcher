using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LanternLauncher.Models;

public enum DownloadState
{
    Queued,
    Connecting,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// One download. Everything that the page shows is derived from byte counts and a
/// state, never from a pre-formatted string: sorting, retries and the resumed
/// history file all need the numbers, not the text.
/// </summary>
public sealed class DownloadTask : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public string TargetPath { get; set; } = "";

    /// <summary>Expected SHA1, when the source publishes one. Empty means no verification.</summary>
    public string? Sha1 { get; set; }

    private long _totalBytes = -1;
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes == value) return;
            _totalBytes = value;
            Raise(nameof(TotalBytes), nameof(IsIndeterminate), nameof(Progress), nameof(SizeText), nameof(ProgressText), nameof(MetaText));
        }
    }

    private long _receivedBytes;
    public long ReceivedBytes
    {
        get => _receivedBytes;
        set
        {
            if (_receivedBytes == value) return;
            _receivedBytes = value;
            Raise(nameof(ReceivedBytes), nameof(Progress), nameof(SizeText), nameof(ProgressText), nameof(MetaText));
        }
    }

    private double _smoothProgress;
    /// <summary>Rendered value: chases <see cref="Progress"/> so the bar glides instead of stepping.</summary>
    public double SmoothProgress
    {
        get => _smoothProgress;
        set
        {
            if (Math.Abs(_smoothProgress - value) < 0.01) return;
            _smoothProgress = value;
            Raise(nameof(SmoothProgress), nameof(ProgressText));
        }
    }

    private double _displayReceived;
    /// <summary>Chases <see cref="ReceivedBytes"/> so the size figure counts up instead of stepping.</summary>
    public double DisplayReceived
    {
        get => _displayReceived;
        set
        {
            if (Math.Abs(_displayReceived - value) < 0.5) return;
            _displayReceived = value;
            Raise(nameof(DisplayReceived), nameof(SizeText), nameof(MetaText));
        }
    }

    private double _displaySpeed;
    /// <summary>Chases <see cref="SpeedBytesPerSecond"/>; a raw speed jumps every sample.</summary>
    public double DisplaySpeed
    {
        get => _displaySpeed;
        set
        {
            if (Math.Abs(_displaySpeed - value) < 100) return;
            _displaySpeed = value;
            Raise(nameof(DisplaySpeed), nameof(SpeedText), nameof(MetaText));
        }
    }

    public bool IsIndeterminate => TotalBytes <= 0;

    public double Progress => TotalBytes > 0
        ? Math.Clamp(ReceivedBytes * 100.0 / TotalBytes, 0, 100)
        : 0;

    private DownloadState _state = DownloadState.Queued;
    public DownloadState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State), nameof(StateText), nameof(IsActive), nameof(CanRetry), nameof(IsFinished));
        }
    }

    public string? Error { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }

    public double SpeedBytesPerSecond { get; set; }
    public int ActiveConnections { get; set; }

    public bool IsActive => State is DownloadState.Queued or DownloadState.Connecting
        or DownloadState.Downloading or DownloadState.Verifying;

    public bool IsFinished => !IsActive;

    public bool CanRetry => State is DownloadState.Failed or DownloadState.Cancelled;

    public string StateText => State switch
    {
        DownloadState.Queued => "Queued",
        DownloadState.Connecting => "Connecting",
        DownloadState.Downloading => "Downloading",
        DownloadState.Verifying => "Verifying",
        DownloadState.Completed => "Completed",
        DownloadState.Failed => "Failed",
        DownloadState.Cancelled => "Cancelled",
        _ => ""
    };

    /// <summary>
    /// One decimal at every scale, and KB below a megabyte, so a figure never reads as
    /// "1023 KB" next to "1 MB" on the same line.
    /// </summary>
    public static string Size(double bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824:0.0} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576:0.0} MB";
        if (bytes <= 0) return "0 KB";
        return $"{bytes / 1024.0:0.0} KB";
    }

    /// <summary>Live rows show progress against the total; finished rows only the file size.</summary>
    public string SizeText
    {
        get
        {
            // A cancelled or failed row reports what actually landed, not the size it aimed for.
            if (!IsActive)
                return Size(State == DownloadState.Completed ? Math.Max(TotalBytes, ReceivedBytes) : ReceivedBytes);

            return TotalBytes > 0
                ? $"{Size(DisplayReceived)} / {Size(TotalBytes)}"
                : Size(DisplayReceived);
        }
    }

    /// <summary>The whole second line: size, speed, ETA while running; state, age, size after.</summary>
    public string MetaText
    {
        get
        {
            if (IsActive)
            {
                var parts = new[] { SizeText, SpeedText, EtaText }
                    .Where(p => !string.IsNullOrWhiteSpace(p));
                return string.Join("  ·  ", parts);
            }

            var done = new[] { StateText, WhenText, SizeText }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join("  ·  ", done);
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>Reads off the smoothed value, so the percentage eases with the bar.</summary>
    public string ProgressText => TotalBytes > 0
        ? Math.Round(SmoothProgress).ToString("0", CultureInfo.InvariantCulture) + "%"
        : "";

    public string SpeedText
    {
        get
        {
            if (!IsActive || DisplaySpeed < 512) return "";
            return Size(DisplaySpeed) + "/s";
        }
    }

    public string EtaText
    {
        get
        {
            if (!IsActive || TotalBytes <= 0 || SpeedBytesPerSecond < 1024) return "";
            var seconds = (TotalBytes - ReceivedBytes) / SpeedBytesPerSecond;
            if (seconds <= 0 || seconds > 35999) return "";
            if (seconds < 60) return $"{seconds:0}s left";
            return $"{seconds / 60:0}m left";
        }
    }

    public string WhenText
    {
        get
        {
            var when = FinishedAt ?? StartedAt;
            var span = DateTime.Now - when;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
            return when.ToString("MMM d", CultureInfo.InvariantCulture);
        }
    }

    public void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
