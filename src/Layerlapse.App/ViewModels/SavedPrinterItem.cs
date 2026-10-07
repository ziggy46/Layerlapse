using Layerlapse.Core.Setup;

namespace Layerlapse.App.ViewModels;

/// <summary>One row in the list of saved printers.</summary>
public sealed class SavedPrinterItem(PrinterProfile profile, bool isCurrent)
{
    public PrinterProfile Profile { get; } = profile;

    public bool IsCurrent { get; } = isCurrent;

    public bool IsOther => !IsCurrent;

    public string Title => Profile.DisplayName;

    public string Detail => string.Join(" · ", new[] { Profile.Model, Profile.Host, IsCurrent ? "connected now" : null }.Where(p => p is not null));
}
