using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Layerlapse.Core.Models;

/// <summary>What a 3MF archive says about itself, read without downloading the whole file.</summary>
/// <param name="PrintTime">Slicer's predicted print time (slice_info.config "prediction").</param>
/// <param name="WeightGrams">Predicted filament weight.</param>
/// <param name="Filaments">Filament type and colour per used slot, such as ("PLA", "#FFFFFF").</param>
/// <param name="PrinterModelId">Model code the project was sliced for, such as "BL-P001".</param>
public sealed record ModelInfo(
    bool HasPreview,
    TimeSpan? PrintTime,
    double? WeightGrams,
    IReadOnlyList<ModelFilament> Filaments,
    string? PrinterModelId)
{
    public static ModelInfo Empty { get; } = new(false, null, null, [], null);
}

public sealed record ModelFilament(string Type, string Color);

/// <summary>Reads previews and slicing details from Bambu Studio 3MF archives.</summary>
public static class ModelArchive
{
    // Sliced projects carry Metadata/plate_N.png; unsliced Bambu projects carry Metadata/thumbnail.png;
    // other 3MF writers use the Auxiliaries thumbnails. The first one present wins.
    private static readonly string[] PreviewCandidates =
    [
        "Metadata/plate_1.png",
        "Metadata/thumbnail.png",
        "Auxiliaries/.thumbnails/thumbnail_middle.png",
        "Auxiliaries/.thumbnails/thumbnail_3mf.png",
    ];

    /// <summary>Extracts the preview PNG (or null) and the slice details from an archive stream.</summary>
    public static (byte[]? Preview, ModelInfo Info) Read(Stream archive)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var preview = FindPreview(zip) is { } entry ? ReadAll(entry) : null;
        var info = zip.GetEntry("Metadata/slice_info.config") is { } slice ? ParseSliceInfo(ReadAll(slice)) : ModelInfo.Empty;
        return (preview, info with { HasPreview = preview is not null });
    }

    private static ZipArchiveEntry? FindPreview(ZipArchive zip)
    {
        foreach (var name in PreviewCandidates)
        {
            if (zip.GetEntry(name) is { } entry)
            {
                return entry;
            }
        }

        // Some projects start at another plate number.
        return zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("Metadata/plate_", StringComparison.Ordinal)
            && e.FullName.EndsWith(".png", StringComparison.Ordinal)
            && !e.FullName.Contains("_small", StringComparison.Ordinal)
            && !e.FullName.Contains("no_light", StringComparison.Ordinal));
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        if (entry.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException($"{entry.FullName} is unexpectedly large.");
        }

        using var stream = entry.Open();
        using var copy = new MemoryStream((int)entry.Length);
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static ModelInfo ParseSliceInfo(byte[] xml)
    {
        try
        {
            var plate = XDocument.Load(new MemoryStream(xml)).Root?.Element("plate");
            if (plate is null)
            {
                return ModelInfo.Empty;
            }

            string? Meta(string key) => plate.Elements("metadata").FirstOrDefault(m => (string?)m.Attribute("key") == key)?.Attribute("value")?.Value;
            double? Number(string key) => double.TryParse(Meta(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

            var filaments = plate.Elements("filament")
                .Select(f => new ModelFilament((string?)f.Attribute("type") ?? "", (string?)f.Attribute("color") ?? ""))
                .Where(f => f.Type.Length > 0)
                .ToList();
            return new ModelInfo(
                false,
                Number("prediction") is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null,
                Number("weight") is { } grams && grams > 0 ? grams : null,
                filaments,
                Meta("printer_model_id"));
        }
        catch (System.Xml.XmlException)
        {
            return ModelInfo.Empty;
        }
    }
}
