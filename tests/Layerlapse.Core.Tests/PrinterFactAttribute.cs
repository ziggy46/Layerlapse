namespace Layerlapse.Core.Tests;

/// <summary>
/// A test that talks to a real printer. Skipped unless LAYERLAPSE_IP and LAYERLAPSE_CODE are set.
/// Filter with: dotnet test --filter Category=Printer
/// </summary>
public sealed class PrinterFactAttribute : FactAttribute
{
    public PrinterFactAttribute()
    {
        if (!PrinterEnvironment.IsConfigured)
        {
            Skip = "Set LAYERLAPSE_IP and LAYERLAPSE_CODE to run printer tests.";
        }
    }
}

internal static class PrinterEnvironment
{
    public static string? Host => Environment.GetEnvironmentVariable("LAYERLAPSE_IP");

    // Never log or print this value.
    public static string? AccessCode => Environment.GetEnvironmentVariable("LAYERLAPSE_CODE");

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(AccessCode);
}
