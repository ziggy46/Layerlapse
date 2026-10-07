using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.ViewModels;

/// <summary>
/// The timelapse grid for one printer. Shows the cached listing immediately, refreshes once connected,
/// fills in thumbnails in the background, and plays a video by downloading it to the cache first.
/// </summary>
public partial class TimelapsesViewModel(string printerId, TimelapseCache cache, IVideoPlayer player) : ViewModelBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
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

    public bool IsFiltered => From is not null || To is not null;

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
            .Select(t => existing.TryGetValue(t.Name, out var item) && item.Timelapse == t ? item : new TimelapseItemViewModel(t) { Thumbnail = existing.GetValueOrDefault(t.Name)?.Thumbnail })
            .ToList();
        TotalCount = _all.Count;
        HasLoaded = true;
        ApplyFilter();
        _ = LoadThumbnailsAsync();
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
