using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Layerlapse.Core.Models;

namespace Layerlapse.App.ViewModels;

/// <summary>One card in the model grid.</summary>
public partial class ModelItemViewModel(ModelFile model) : ViewModelBase
{
    public event EventHandler? SelectionChanged;

    public ModelFile Model { get; } = model;

    /// <summary>The file name without its extension, exactly as stored on the printer.</summary>
    public string Title => Model.DisplayName;

    public string Kind => Model.IsSliced ? "Sliced" : "Project";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial ModelInfo? Info { get; set; }

    /// <summary>"1.4 MB · ≈ 16 min · 5.0 g PLA" (time and weight are the slicer's estimates).</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { TimelapseItemViewModel.FormatSize(Model.Size) };
            if (Info?.PrintTime is { } time)
            {
                parts.Add("≈ " + TimelapseItemViewModel.FormatDuration(time));
            }

            if (Info?.WeightGrams is { } grams)
            {
                var types = string.Join("/", Info.Filaments.Select(f => f.Type).Distinct());
                parts.Add((grams.ToString("0.#", CultureInfo.CurrentCulture) + " g " + types).Trim());
            }

            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    public partial Bitmap? Preview { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string? DownloadStatus { get; set; }

    /// <summary>Where this model was saved, so it can be opened (in Bambu Studio, for 3MF files).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    public partial string? SavedPath { get; set; }

    public bool CanOpen => SavedPath is not null;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
}
