using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Layerlapse.Core.Setup;

namespace Layerlapse.App.ViewModels;

/// <summary>Preferences: deleting, auto-download, and the About section with the update check.</summary>
public partial class SettingsViewModel(
    JsonSettingsStore? settings,
    UpdateChecker? updates = null,
    Func<string, string?, Task<string?>>? pickFolder = null,
    Func<string, string, string, Task<bool>>? confirm = null,
    Action<string>? openLink = null) : ViewModelBase
{
    private bool _loading;

    /// <summary>Raised after any setting changes, so pages can pick it up.</summary>
    public event EventHandler? Changed;

    [ObservableProperty]
    public partial bool AllowDelete { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoDownloadText))]
    public partial bool AutoDownload { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoDownloadText))]
    public partial string? AutoDownloadFolder { get; private set; }

    public string AutoDownloadText => AutoDownload && AutoDownloadFolder is not null
        ? $"New timelapses are saved to {AutoDownloadFolder} while Layerlapse is open."
        : "Off. When on, timelapses that finish after you turn this on are saved automatically while Layerlapse is open.";

    public string VersionText => $"Layerlapse {UpdateChecker.CurrentVersion}";

    [ObservableProperty]
    public partial string? UpdateText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial AvailableUpdate? Update { get; private set; }

    public bool HasUpdate => Update is not null;

    public async Task LoadAsync()
    {
        var current = settings is null ? new AppSettings() : await settings.LoadAsync();
        _loading = true;
        AllowDelete = current.AllowDelete;
        AutoDownload = current.AutoDownloadEnabled;
        AutoDownloadFolder = current.AutoDownloadFolder;
        _loading = false;
    }

    /// <summary>Quiet check at launch: only says something when a newer version exists.</summary>
    public async Task CheckForUpdatesQuietlyAsync()
    {
        if (updates is { IsConfigured: true } && await updates.CheckAsync() is { } update)
        {
            Update = update;
            UpdateText = $"Layerlapse {update.Version} is available.";
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (updates is not { IsConfigured: true })
        {
            UpdateText = "Update checks are not set up in this build.";
            return;
        }

        UpdateText = "Checking…";
        Update = await updates.CheckAsync();
        UpdateText = Update is null ? "You have the latest version (or the release page could not be reached)." : $"Layerlapse {Update.Version} is available.";
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (Update is not null)
        {
            openLink?.Invoke(Update.Page.ToString());
        }
    }

    [RelayCommand]
    private async Task ChangeAutoDownloadFolderAsync()
    {
        if (pickFolder is not null && await pickFolder("Choose where to save new timelapses", AutoDownloadFolder) is { } folder)
        {
            AutoDownloadFolder = folder;
            await SaveAsync(s => s with { AutoDownloadFolder = folder });
        }
    }

    partial void OnAllowDeleteChanged(bool value)
    {
        if (!_loading)
        {
            _ = SetAllowDeleteAsync(value);
        }
    }

    partial void OnAutoDownloadChanged(bool value)
    {
        if (!_loading)
        {
            _ = SetAutoDownloadAsync(value);
        }
    }

    private async Task SetAllowDeleteAsync(bool value)
    {
        if (value && confirm is not null && !await confirm(
                "Allow deleting timelapses?",
                "Each timelapse card gets a Delete button. Deleting removes the video and its thumbnail from the printer's storage for good, one file at a time, after you confirm. Models and other files are never deleted.",
                "Allow deleting"))
        {
            _loading = true;
            AllowDelete = false;
            _loading = false;
            return;
        }

        await SaveAsync(s => s with { AllowDelete = value });
    }

    private async Task SetAutoDownloadAsync(bool value)
    {
        if (!value)
        {
            await SaveAsync(s => s with { AutoDownloadSince = null });
            return;
        }

        var folder = AutoDownloadFolder;
        if (folder is null && pickFolder is not null)
        {
            folder = await pickFolder("Choose where to save new timelapses", null);
        }

        if (folder is null)
        {
            _loading = true;
            AutoDownload = false;
            _loading = false;
            return;
        }

        AutoDownloadFolder = folder;
        // Only timelapses that finish from now on: turning this on never fetches the whole history.
        await SaveAsync(s => s with { AutoDownloadFolder = folder, AutoDownloadSince = DateTime.UtcNow });
    }

    private async Task SaveAsync(Func<AppSettings, AppSettings> change)
    {
        if (settings is not null)
        {
            await settings.SaveAsync(change(await settings.LoadAsync()));
        }

        OnPropertyChanged(nameof(AutoDownloadText));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
