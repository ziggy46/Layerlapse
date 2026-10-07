using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Layerlapse.Core.Models;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;

namespace Layerlapse.App.ViewModels;

/// <summary>
/// The 3MF projects on the printer: cached list first, previews read from inside each archive in the
/// background, search by name, and downloads that can be opened in the slicer afterwards.
/// </summary>
public partial class ModelsViewModel(
    string cacheRoot,
    Func<string, string?, Task<string?>>? pickFolder = null,
    JsonSettingsStore? settings = null,
    Action<string>? openWithDefaultApp = null,
    Action<string>? openInSlicer = null) : ViewModelBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private List<ModelItemViewModel> _all = [];
    private PrinterSession? _session;
    private ModelLibrary? _library;
    private CancellationTokenSource? _previews;
    private CancellationTokenSource? _downloads;

    public ObservableCollection<ModelItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(NoMatches))]
    public partial bool HasLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial int TotalCount { get; private set; }

    public bool IsConnected => _library is not null;

    public bool IsEmpty => HasLoaded && TotalCount == 0;

    public bool NoMatches => HasLoaded && TotalCount > 0 && Items.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(DownloadSelectedLabel))]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
    public partial int SelectedCount { get; private set; }

    public bool HasSelection => SelectedCount > 0;

    public string DownloadSelectedLabel => SelectedCount == 1 ? "Download 1 model" : $"Download {SelectedCount} models";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand), nameof(DownloadOneCommand), nameof(CancelDownloadsCommand))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadBar), nameof(CanRevealFolder))]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadBar))]
    public partial string? DownloadText { get; private set; }

    [ObservableProperty]
    public partial double DownloadPercent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevealFolder))]
    public partial string? LastDownloadFolder { get; private set; }

    public bool CanRevealFolder => !IsDownloading && LastDownloadFolder is not null && openWithDefaultApp is not null;

    public bool ShowDownloadBar => IsDownloading || DownloadText is not null;

    partial void OnSearchChanged(string value) => ApplyFilter();

    public async Task LoadCachedAsync()
    {
        var models = await ModelLibrary.LoadCachedAsync(cacheRoot, _lifetime.Token);
        if (models.Count > 0)
        {
            Show(models);
            Status = $"{CountText(models.Count)} · updating…";
        }
    }

    /// <summary>Connected: refresh from the printer. The session is shared and owned by the caller.</summary>
    public async Task AttachAsync(PrinterSession session)
    {
        _session = session;
        _library = new ModelLibrary(session, cacheRoot);
        OnPropertyChanged(nameof(IsConnected));
        await RefreshAsync();
    }

    public void MarkOffline()
    {
        if (HasLoaded && !IsRefreshing)
        {
            Status = CountText(_all.Count) + " · from the last visit";
        }
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
            Status = "Loading models…";
        }

        try
        {
            var models = await _library.RefreshAsync(_lifetime.Token);
            Show(models);
            Status = CountText(models.Count) + " · print times and weights are the slicer's estimates";
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
    private Task DownloadOneAsync(ModelItemViewModel item) => DownloadAsync([item]);

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void CancelDownloads() => _downloads?.Cancel();

    [RelayCommand]
    private void RevealFolder()
    {
        if (LastDownloadFolder is { } folder)
        {
            openWithDefaultApp?.Invoke(folder);
        }
    }

    /// <summary>Opens a downloaded model in Bambu Studio (or the default app if Bambu Studio is not installed).</summary>
    [RelayCommand]
    private void Open(ModelItemViewModel item)
    {
        if (item.SavedPath is { } path && File.Exists(path))
        {
            (openInSlicer ?? openWithDefaultApp)?.Invoke(path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _downloads?.Cancel();
        _previews?.Cancel();
        _lifetime.Cancel();
        await Task.CompletedTask;
    }

    private bool CanRefresh() => !IsRefreshing;

    private bool CanDownloadSelected() => SelectedCount > 0 && !IsDownloading;

    private bool CanDownloadOne() => !IsDownloading;

    private void Show(IReadOnlyList<ModelFile> models)
    {
        var existing = _all.ToDictionary(i => i.Model.CacheKey);
        _all = models.Select(m => existing.GetValueOrDefault(m.CacheKey) ?? NewItem(m)).ToList();
        TotalCount = _all.Count;
        SelectedCount = _all.Count(i => i.IsSelected);
        HasLoaded = true;
        ApplyFilter();
        _ = LoadPreviewsAsync();
    }

    private ModelItemViewModel NewItem(ModelFile model)
    {
        var item = new ModelItemViewModel(model);
        item.SelectionChanged += (_, _) => SelectedCount = _all.Count(i => i.IsSelected);
        return item;
    }

    private void ApplyFilter()
    {
        var terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Items.Clear();
        foreach (var item in _all.Where(i => terms.All(t => i.Model.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase))))
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(NoMatches));
    }

    /// <summary>Cached previews first (instant), then the rest from the printer, newest first.</summary>
    private async Task LoadPreviewsAsync()
    {
        _previews?.Cancel();
        _previews = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _previews.Token;
        try
        {
            foreach (var item in _all.Where(i => i.Info is null).ToList())
            {
                var (path, info) = ModelLibrary.LoadCachedPreview(cacheRoot, item.Model);
                if (info is not null)
                {
                    item.Info = info;
                    item.Preview = path is null ? null : await Task.Run(() => LoadBitmap(path), token);
                }
            }

            foreach (var item in _all.Where(i => i.Info is null).ToList())
            {
                token.ThrowIfCancellationRequested();
                if (_library is null)
                {
                    return;
                }

                var (path, info) = await _library.GetPreviewAsync(item.Model, token);
                item.Info = info;
                item.Preview = path is null ? null : await Task.Run(() => LoadBitmap(path), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Previews are optional; the cards show a placeholder.
        }
    }

    private async Task DownloadAsync(IReadOnlyList<ModelItemViewModel> items)
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
        var folder = await pickFolder("Choose where to save models", saved.LastDownloadFolder ?? LastDownloadFolder);
        if (folder is null)
        {
            return;
        }

        if (settings is not null)
        {
            await settings.SaveAsync(saved with { LastDownloadFolder = folder });
        }

        var byPath = new Dictionary<string, ModelItemViewModel>();
        var downloadItems = new List<DownloadItem>();
        foreach (var item in items)
        {
            if (PrinterFileDownloader.SafeFileName(item.Model.Name) is { } name)
            {
                downloadItems.Add(new DownloadItem(item.Model.RemotePath, name, item.Model.Size));
                byPath[item.Model.RemotePath] = item;
                item.DownloadStatus = "Waiting…";
            }
            else
            {
                item.DownloadStatus = "Failed: this file name cannot be saved on this computer.";
            }
        }

        _downloads = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        IsDownloading = true;
        LastDownloadFolder = folder;
        DownloadPercent = 0;
        var finished = false;
        var progress = new Progress<FileDownloadProgress>(p =>
        {
            if (finished)
            {
                return;
            }

            DownloadPercent = p.BatchSize == 0 ? 100 : p.BatchBytes * 100d / p.BatchSize;
            DownloadText = $"Downloading {p.Index + 1} of {p.Count} · {TimelapseItemViewModel.FormatSize(p.BatchBytes)} of {TimelapseItemViewModel.FormatSize(p.BatchSize)}";
            if (byPath.TryGetValue(p.Current.RemotePath, out var current) && p.FileBytes < p.Current.Size)
            {
                current.DownloadStatus = $"Downloading… {p.FileBytes * 100 / Math.Max(1, p.Current.Size)}%";
            }
        });

        try
        {
            var results = await new PrinterFileDownloader(_session).DownloadAsync(downloadItems, folder, progress, _downloads.Token);
            finished = true;
            foreach (var result in results)
            {
                var item = byPath[result.Item.RemotePath];
                item.DownloadStatus = Describe(result);
                item.SavedPath = result.Outcome is DownloadOutcome.Downloaded or DownloadOutcome.SkippedExisting or DownloadOutcome.CopiedFromCache
                    ? result.Path
                    : item.SavedPath;
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
        }
    }

    private static string Describe(FileDownloadResult result) => result.Outcome switch
    {
        DownloadOutcome.Downloaded when result.ResumedFrom > 0 => "Saved (continued from an earlier download)",
        DownloadOutcome.Downloaded or DownloadOutcome.CopiedFromCache => "Saved",
        DownloadOutcome.SkippedExisting => "Already in the folder",
        DownloadOutcome.SkippedConflict => "Skipped: a different file with this name is in the folder",
        DownloadOutcome.Cancelled => "Cancelled. Download again to continue where it stopped.",
        _ => "Failed: " + result.Error,
    };

    private static string Summarize(IReadOnlyList<FileDownloadResult> results)
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

    private static Bitmap? LoadBitmap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 400);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string CountText(int count) => count == 1 ? "1 model" : $"{count} models";
}
