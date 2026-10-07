using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.ViewModels;

/// <summary>One card in the timelapse grid.</summary>
public partial class TimelapseItemViewModel(Timelapse timelapse) : ViewModelBase
{
    /// <summary>Raised when the checkbox changes, so the grid can update its selection count.</summary>
    public event EventHandler? SelectionChanged;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Result of the last download of this card ("Saved", "Already in the folder", ...).</summary>
    [ObservableProperty]
    public partial string? DownloadStatus { get; set; }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);

    public Timelapse Timelapse { get; } = timelapse;

    /// <summary>Start time exactly as the printer recorded it.</summary>
    public string Title => Timelapse.Start.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    public string SizeText => FormatSize(Timelapse.Size);

    public string DurationText => Timelapse.ApproximateDuration is { } d ? "≈ " + FormatDuration(d) : "Duration unknown";

    public string Detail => $"{SizeText} · {DurationText}";

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsDownloading { get; set; }

    public bool IsIdle => !IsDownloading;

    /// <summary>0–100 while downloading.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1024 => (bytes / 1024d).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes + " bytes",
    };

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h {duration.Minutes:00} min" : $"{Math.Max(1, duration.Minutes)} min";
}
