using System.Diagnostics;
using Layerlapse.Core.Models;
using Layerlapse.Core.Printers;
using Xunit.Abstractions;

namespace Layerlapse.Core.Tests;

/// <summary>Models against the real printer. Read-only: only ranged reads inside the archives.</summary>
[Trait("Category", "Printer")]
public sealed class ModelLibraryPrinterTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-real-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PrinterSession Session() =>
        new(new PrinterConnection(PrinterEnvironment.Host!, PrinterEnvironment.AccessCode!), c => new BambuFtpsClient(c));

    [PrinterFact]
    public async Task Lists_models_and_reads_previews()
    {
        await using var session = Session();
        var library = new ModelLibrary(session, _root);
        var clock = Stopwatch.StartNew();
        var models = await library.RefreshAsync();
        output.WriteLine($"{models.Count} models listed (with MDTM) in {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Contains(models, m => m.IsSliced);

        var limit = Environment.GetEnvironmentVariable("LAYERLAPSE_HEAVY_TESTS") == "1" ? models.Count : 12;
        clock.Restart();
        var withPreview = 0;
        var withTime = 0;
        foreach (var model in models.Take(limit))
        {
            var (path, info) = await library.GetPreviewAsync(model);
            withPreview += path is null ? 0 : 1;
            withTime += info.PrintTime is null ? 0 : 1;
        }

        output.WriteLine($"Previews for {withPreview} of {limit} models, print times for {withTime}, in {clock.Elapsed.TotalSeconds:F1} s");
        Assert.True(withPreview > limit / 2, "most models should have a preview");

        clock.Restart();
        foreach (var model in models.Take(limit))
        {
            await library.GetPreviewAsync(model);
        }

        output.WriteLine($"Again from cache in {clock.Elapsed.TotalMilliseconds:F0} ms");
    }
}
