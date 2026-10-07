namespace Layerlapse.Core.Models;

/// <summary>A 3MF project on the printer's storage (loose in the root folder).</summary>
/// <param name="ModifiedUtc">Exact modification time (MDTM) when known, else the listing time.</param>
public sealed record ModelFile(string Name, long Size, DateTime ModifiedUtc)
{
    public string RemotePath => "/" + Name;

    /// <summary>Sliced projects (".gcode.3mf") hold G-code and previews but no mesh.</summary>
    public bool IsSliced => Name.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name without ".gcode.3mf" or ".3mf". Names are shown as they are, never parsed.</summary>
    public string DisplayName =>
        Name.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase) ? Name[..^".gcode.3mf".Length]
        : Name.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase) ? Name[..^".3mf".Length]
        : Name;

    /// <summary>Stable key for cached previews: names can repeat after truncation, so include size and time.</summary>
    public string CacheKey => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{Name}\n{Size}\n{ModifiedUtc:O}")))[..32];

    public static bool IsModel(string name) => name.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase);
}
