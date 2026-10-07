using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.ViewModels;

/// <summary>
/// The timelapse grid for one printer. Shows the cached listing immediately, refreshes once connected,
/// fills in thumbnails in the background, and plays a video by downloading it to the cache first.
/// </summary>
public partial class TimelapsesViewModel(
    string printerId,
    TimelapseCache cache,
    IVideoPlayer player,
    Func<string?, Task<string?>>? pickFolder = null,
    JsonSettingsStore? settings = null,
    Action<string>? revealFolder = null) : ViewModelBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _downloads;
    private List<TimelapseItemViewModel> _all = [];
    private PrinterSession? _session;
    private TimelapseLibrary? _library;
    private CancellationTokenSource? _thumbnails;

    public string PrinterId { get; } = printerId;

    /// <summary>The visible cards: all timelapses within the date filter, newest first.</summary>
    public ObservableCollection<TimelapseItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial DateTime? From { get; set; }

    [ObservableProperty]
    public partial DateTime? To { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial int TotalCount { get; private set; }

    public bool IsConnected => _library is not null;

    /// <summary>The printer has no timelapses at all (not just none in the date range).</summary>
    public bool IsEmpty => HasLoaded && TotalCount == 0;

    /// <summary>There are timelapses, but none in the chosen dates.</summary>
    public bool IsFilteredEmpty => HasLoaded && TotalCount > 0 && Items.Count == 0;

    /// <summary>The connection failed: the cached list stays, labelled as such.</summary>
    public void MarkOffline()
    {
        if (HasLoaded && !IsRefreshing)
        {
            Status = CountText(_all.Count) + " · from the last visit";
        }
    }

    public bool IsFiltered => From is not null || To is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(DownloadSelectedLabel))]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
    public partial int SelectedCount { get; private set; }

    public bool HasSelection => SelectedCount > 0;

    public string DownloadSelectedLabel => SelectedCount == 1 ? "Download 1 timelapse" : $"Download {SelectedCount} timelapses";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand), nameof(DownloadOneCommand), nameof(CancelDownloadsCommand))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadBar))]
    public partial bool IsDownloading { get; private set; }

    /// <summary>"Downloading 2 of 5 · 42.1 of 120.3 MB" while running; the summary afterwards.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadBar))]
    public partial string? DownloadText { get; private set; }

    /// <summary>0–100 across the whole batch.</summary>
    [ObservableProperty]
    public partial double DownloadPercent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevealFolder))]
    public partial string? LastDownloadFolder { get; private set; }

    public bool CanRevealFolder => !IsDownloading && LastDownloadFolder is not null && revealFolder is not null;

    public bool ShowDownloadBar => IsDownloading || DownloadText is not null;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in _all)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadSelected))]
    private Task DownloadSelectedAsync() => DownloadAsync(_all.Where(i => i.IsSelected).ToList());

    [RelayCommand(CanExecute = nameof(CanDownloadOne))]
    private Task DownloadOneAsync(TimelapseItemViewModel item) => DownloadAsync([item]);

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void CancelDownloads() => _downloads?.Cancel();

    [RelayCommand]
    private void RevealFolder()
    {
        if (LastDownloadFolder is { } folder)
        {
            revealFolder?.Invoke(folder);
        }
    }

    private bool CanDownloadSelected() => SelectedCount > 0 && !IsDownloading;

    private bool CanDownloadOne() => !IsDownloading;

    /// <summary>Asks where to save, then downloads one file at a time with progress, skipping files already there.</summary>
    private async Task DownloadAsync(IReadOnlyList<TimelapseItemViewModel> items)
    {
        if (items.Count == 0 || pickFolder is null)
        {
            return;
        }

        if (_session is null)
        {
            DownloadText = "Connect to the printer to download.";
            return;
        }

        var saved = settings is null ? new AppSettings() : await settings.LoadAsync();
        var suggested = saved.LastDownloadFolder ?? LastDownloadFolder;
        var folder = await pickFolder(suggested);
        if (folder is null)
        {
            return;
        }

        if (settings is not null)
        {
            await settings.SaveAsync(saved with { LastDownloadFolder = folder });
        }

        _downloads = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        IsDownloading = true;
        LastDownloadFolder = folder;
        DownloadPercent = 0;
        foreach (var item in items)
        {
            item.DownloadStatus = "Waiting…";
        }

        var byName = items.ToDictionary(i => i.Timelapse.Name);
        var finished = false;
        var progress = new Progress<DownloadProgress>(p =>
        {
            // Progress<T> posts reports asynchronously; ignore any that arrive after the summary is shown.
            if (finished)
            {
                return;
            }

            DownloadPercent = p.BatchSize == 0 ? 100 : p.BatchBytes * 100d / p.BatchSize;
            DownloadText = $"Downloading {p.Index + 1} of {p.Count} · {TimelapseItemViewModel.FormatSize(p.BatchBytes)} of {TimelapseItemViewModel.FormatSize(p.BatchSize)}";
            if (byName.TryGetValue(p.Current.Name, out var current) && p.FileBytes < p.Current.Size)
            {
                current.DownloadStatus = $"Downloading… {p.FileBytes * 100 / Math.Max(1, p.Current.Size)}%";
            }
        });

        try
        {
            var results = await new TimelapseDownloader(_session, cache).DownloadAsync(items.Select(i => i.Timelapse).ToList(), folder, progress, _downloads.Token);
            finished = true;
            foreach (var result in results)
            {
                byName[result.Timelapse.Name].DownloadStatus = Describe(result);
            }

            DownloadText = Summarize(results);
            if (results.All(r => r.Outcome is not (DownloadOutcome.Failed or DownloadOutcome.Cancelled)))
            {
                ClearSelection();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            finished = true;
            DownloadText = "Download failed: " + e.Message;
        }
        finally
        {
            finished = true;
            IsDownloading = false;
            OnPropertyChanged(nameof(CanRevealFolder));
        }
    }

    private static string Describe(DownloadResult result) => result.Outcome switch
    {
        DownloadOutcome.Downloaded when result.ResumedFrom > 0 => "Saved (continued from an earlier download)",
        DownloadOutcome.Downloaded or DownloadOutcome.CopiedFromCache => "Saved",
        DownloadOutcome.SkippedExisting => "Already in the folder",
        DownloadOutcome.SkippedConflict => "Skipped: a different file with this name is in the folder",
        DownloadOutcome.Cancelled => "Cancelled. Download again to continue where it stopped.",
        _ => "Failed: " + result.Error,
    };

    private static string Summarize(IReadOnlyList<DownloadResult> results)
    {
        var parts = new List<string>();
        void Add(int count, string text)
        {
            if (count > 0)
            {
                parts.Add($"{count} {text}");
            }
        }

        Add(results.Count(r => r.Outcome is DownloadOutcome.Downloaded or DownloadOutcome.CopiedFromCache), "saved");
        Add(results.Count(r => r.Outcome == DownloadOutcome.SkippedExisting), "already in the folder");
        Add(results.Count(r => r.Outcome == DownloadOutcome.SkippedConflict), "skipped (name in use)");
        Add(results.Count(r => r.Outcome == DownloadOutcome.Failed), "failed");
        Add(results.Count(r => r.Outcome == DownloadOutcome.Cancelled), "cancelled");
        return string.Join(" · ", parts);
    }

    partial void OnFromChanged(DateTime? value) => ApplyFilter();

    partial void OnToChanged(DateTime? value) => ApplyFilter();

    /// <summary>Shows what the cache knows, before any network access.</summary>
    public async Task LoadCachedAsync()
    {
        var listing = await TimelapseLibrary.LoadCachedAsync(cache, _lifetime.Token);
        if (listing.UpdatedUtc is not null)
        {
            Show(listing);
            Status = $"{CountText(listing.Timelapses.Count)} · updating…";
        }
    }

    /// <summary>Connected: refresh from the printer.</summary>
    public async Task AttachAsync(PrinterSession session)
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        _session = session;
        _library = new TimelapseLibrary(session, cache);
        OnPropertyChanged(nameof(IsConnected));
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_library is null)
        {
            return;
        }

        IsRefreshing = true;
        Error = null;
        if (!HasLoaded)
        {
            Status = "Loading timelapses…";
        }

        try
        {
            var listing = await _library.RefreshAsync(_lifetime.Token);
            Show(listing);
            Status = CountText(listing.Timelapses.Count) + (listing.Clock?.Source == ClockOffsetSource.ComputerTimeZone
                ? " · durations (≈) are approximate and assume the printer uses this computer's time zone"
                : " · durations (≈) are approximate");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = "Could not update the list: " + e.Message;
            Status = HasLoaded ? CountText(_all.Count) + " · from the last visit" : null;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task PlayAsync(TimelapseItemViewModel item)
    {
        if (item.IsDownloading)
        {
            return;
        }

        item.Error = null;
        try
        {
            string path;
            if (cache.HasVideo(item.Timelapse))
            {
                path = cache.VideoPath(item.Timelapse);
            }
            else if (_library is null)
            {
                item.Error = "Connect to the printer to download this video.";
                return;
            }
            else
            {
                item.IsDownloading = true;
                item.Progress = 0;
                var progress = new Progress<long>(bytes => item.Progress = item.Timelapse.Size == 0 ? 100 : bytes * 100d / item.Timelapse.Size);
                path = await _library.GetVideoAsync(item.Timelapse, progress, _lifetime.Token);
            }

            player.Play(path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            item.Error = e.Message;
        }
        finally
        {
            item.IsDownloading = false;
        }
    }

    [RelayCommand]
    private void ClearFilter()
    {
        From = null;
        To = null;
    }

    public async ValueTask DisposeAsync()
    {
        _downloads?.Cancel();
        _lifetime.Cancel();
        _thumbnails?.Cancel();
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    private bool CanRefresh() => !IsRefreshing;

    private void Show(TimelapseListing listing)
    {
        var existing = _all.ToDictionary(i => i.Timelapse.Name);
        _all = listing.Timelapses
            .Select(t => existing.TryGetValue(t.Name, out var item) && item.Timelapse == t ? item : NewItem(t, existing.GetValueOrDefault(t.Name)))
            .ToList();
        SelectedCount = _all.Count(i => i.IsSelected);
        TotalCount = _all.Count;
        HasLoaded = true;
        ApplyFilter();
        _ = LoadThumbnailsAsync();
    }

    private TimelapseItemViewModel NewItem(Timelapse timelapse, TimelapseItemViewModel? previous)
    {
        var item = new TimelapseItemViewModel(timelapse) { Thumbnail = previous?.Thumbnail, IsSelected = previous?.IsSelected ?? false };
        item.SelectionChanged += (_, _) => SelectedCount = _all.Count(i => i.IsSelected);
        return item;
    }

    private void ApplyFilter()
    {
        OnPropertyChanged(nameof(IsFiltered));
        var from = From?.Date;
        var to = To?.Date.AddDays(1);
        Items.Clear();
        foreach (var item in _all.Where(i => (from is null || i.Timelapse.Start >= from) && (to is null || i.Timelapse.Start < to)))
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(IsFilteredEmpty));
    }

    /// <summary>Cached thumbnails first (instant), then downloads for the rest, newest first.</summary>
    private async Task LoadThumbnailsAsync()
    {
        _thumbnails?.Cancel();
        _thumbnails = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _thumbnails.Token;
        try
        {
            foreach (var item in _all.Where(i => i.Thumbnail is null).ToList())
            {
                token.ThrowIfCancellationRequested();
                var path = cache.ThumbnailPath(item.Timelapse);
                if (!File.Exists(path))
                {
                    if (_library is null)
                    {
                        continue;
                    }

                    path = await _library.GetThumbnailAsync(item.Timelapse, token);
                    if (path is null)
                    {
                        continue;
                    }
                }

                item.Thumbnail = await Task.Run(() => LoadBitmap(path), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Thumbnails are optional; the cards show a placeholder.
        }
    }

    private static Bitmap? LoadBitmap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 440);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string CountText(int count) => count == 1 ? "1 timelapse" : $"{count} timelapses";
}
