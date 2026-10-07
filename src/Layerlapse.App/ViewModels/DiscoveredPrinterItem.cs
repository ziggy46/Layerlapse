using Layerlapse.Core.Discovery;

namespace Layerlapse.App.ViewModels;

/// <summary>One row in the list of printers found on the network.</summary>
public sealed class DiscoveredPrinterItem(DiscoveredPrinter printer)
{
    public DiscoveredPrinter Printer { get; } = printer;

    public string Title => Printer.Name ?? Printer.Model ?? Printer.Serial;

    public bool ShowModelLine => !string.Equals(Title, ModelText, StringComparison.Ordinal);

    public string ModelText => Printer.Model ?? (Printer.ModelCode is { } code ? $"Unknown model ({code})" : "Unknown model");

    public string Detail => Printer.Source == DiscoverySource.PortScan
        ? $"{Printer.Host} · {Printer.Serial} · found by network scan"
        : $"{Printer.Host} · {Printer.Serial}";
}
