namespace Layerlapse.Core.Discovery;

/// <summary>
/// Maps model codes and serial prefixes to model names.
/// Verified on a real printer: BL-P001 and serial prefix 00M (X1 Carbon, 2026-10-06).
/// Every other entry comes from community reports and is unverified; unknown values map to null so the
/// user is asked to pick the model.
/// </summary>
public static class PrinterModels
{
    public const string X1Carbon = "X1 Carbon";

    /// <summary>Models offered when the user has to pick.</summary>
    public static IReadOnlyList<string> All { get; } =
        [X1Carbon, "X1", "X1E", "P1P", "P1S", "A1", "A1 mini", "H2D", "Other"];

    private static readonly Dictionary<string, string> ByModelCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BL-P001"] = X1Carbon, // verified
        ["BL-P002"] = "X1",
        ["C13"] = "X1E",
        ["C11"] = "P1P",
        ["C12"] = "P1S",
        ["N2S"] = "A1",
        ["N1"] = "A1 mini",
        ["O1D"] = "H2D",
    };

    private static readonly Dictionary<string, string> BySerialPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00M"] = X1Carbon, // verified
        ["00W"] = "X1",
        ["03W"] = "X1E",
        ["01S"] = "P1P",
        ["01P"] = "P1S",
        ["039"] = "A1",
        ["030"] = "A1 mini",
        ["094"] = "H2D",
    };

    public static string? FromModelCode(string? code) =>
        code is not null && ByModelCode.TryGetValue(code.Trim(), out var model) ? model : null;

    public static string? FromSerial(string? serial) =>
        serial is { Length: >= 3 } && BySerialPrefix.TryGetValue(serial[..3], out var model) ? model : null;
}
