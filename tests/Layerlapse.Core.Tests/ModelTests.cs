using System.IO.Compression;
using System.Text;
using Layerlapse.Core.Models;
using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Tests;

public class ModelFileTests
{
    [Theory]
    [InlineData("Under_Desk_Cable_Clip_(cable_management).gcode.3mf", "Under_Desk_Cable_Clip_(cable_management)", true)]
    [InlineData("Bracket v2.3mf", "Bracket v2", false)]
    [InlineData("挂钩 Größe & Co #2....gcode.3mf", "挂钩 Größe & Co #2...", true)]
    public void Shows_names_without_the_extension(string name, string display, bool sliced)
    {
        var model = new ModelFile(name, 10, DateTime.UtcNow);

        Assert.Equal(display, model.DisplayName);
        Assert.Equal(sliced, model.IsSliced);
        Assert.Equal("/" + name, model.RemotePath);
    }

    [Fact]
    public void Cache_key_differs_for_same_name_with_different_content()
    {
        var a = new ModelFile("Same....gcode.3mf", 10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = a with { Size = 11 };

        Assert.NotEqual(a.CacheKey, b.CacheKey);
    }
}

public class RemoteFileStreamTests
{
    [Fact]
    public void Reads_any_range_through_cached_blocks()
    {
        var data = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        var reads = 0;
        using var stream = new RemoteFileStream((offset, length) => { reads++; return data.AsSpan((int)offset, length).ToArray(); }, data.Length, blockSize: 65536);

        var buffer = new byte[100_000];
        stream.Seek(150_000, SeekOrigin.Begin);
        Assert.Equal(100_000, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(data.AsSpan(150_000, 100_000).ToArray(), buffer);

        stream.Seek(-10, SeekOrigin.End);
        Assert.Equal(10, stream.Read(buffer, 0, 100));
        Assert.Equal(0, stream.Read(buffer, 0, 100));
        var before = reads;
        stream.Seek(160_000, SeekOrigin.Begin);
        _ = stream.Read(buffer, 0, 1000);
        Assert.Equal(before, reads); // block already cached
    }
}

public sealed class ModelLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-models-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    internal static byte[] Archive(bool preview = true, string? sliceInfo = null, int gcodeBytes = 500_000)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var s = zip.CreateEntry(name, level).Open();
                s.Write(content);
            }

            Add("[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>"));
            if (preview)
            {
                Add("Metadata/plate_1.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4], CompressionLevel.NoCompression);
                Add("Metadata/plate_1_small.png", [9, 9, 9]);
            }

            var random = new Random(1);
            var gcode = new byte[gcodeBytes];
            random.NextBytes(gcode); // incompressible, like a big G-code part of the archive
            Add("Metadata/plate_1.gcode", gcode, CompressionLevel.NoCompression);
            if (sliceInfo is not null)
            {
                Add("Metadata/slice_info.config", Encoding.UTF8.GetBytes(sliceInfo));
            }
        }

        return memory.ToArray();
    }

    internal const string SliceInfo = """
        <?xml version="1.0" encoding="UTF-8"?>
        <config>
          <plate>
            <metadata key="printer_model_id" value="BL-P001"/>
            <metadata key="prediction" value="959"/>
            <metadata key="weight" value="4.99"/>
            <filament id="1" type="PLA" color="#FFFFFF" used_g="4.99"/>
          </plate>
        </config>
        """;

    private ModelLibrary Library() =>
        new(new PrinterSession(new PrinterConnection("192.168.1.50", _printer.AccessCode), _printer.Create), _root);

    private void AddModel(string name, byte[] data, DateTime modified) =>
        _printer.Files["/" + name] = new FakeFile(data, modified);

    [Fact]
    public async Task Lists_models_newest_first_and_ignores_other_files()
    {
        AddModel("Old.3mf", Archive(), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        AddModel("New.gcode.3mf", Archive(), new DateTime(2026, 9, 1, 12, 0, 30, DateTimeKind.Utc));
        _printer.Files["/verify_job"] = new FakeFile(new byte[16], DateTime.UtcNow);

        var models = await Library().RefreshAsync();

        Assert.Equal(["New.gcode.3mf", "Old.3mf"], models.Select(m => m.Name));
        Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 30, DateTimeKind.Utc), models[0].ModifiedUtc); // exact, from MDTM
        Assert.Equal(models, await ModelLibrary.LoadCachedAsync(_root));
    }

    [Fact]
    public async Task Reads_preview_and_slice_details_without_downloading_the_archive()
    {
        var data = Archive(sliceInfo: SliceInfo, gcodeBytes: 2_000_000);
        AddModel("Clip.gcode.3mf", data, DateTime.UtcNow);
        var library = Library();
        var model = (await library.RefreshAsync()).Single();

        var (path, info) = await library.GetPreviewAsync(model);

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4], await File.ReadAllBytesAsync(path!));
        Assert.True(info.HasPreview);
        Assert.Equal(TimeSpan.FromSeconds(959), info.PrintTime);
        Assert.Equal(4.99, info.WeightGrams);
        Assert.Equal(new ModelFilament("PLA", "#FFFFFF"), Assert.Single(info.Filaments));
        Assert.Equal("BL-P001", info.PrinterModelId);
        Assert.True(_printer.BytesSent < data.Length / 4, $"sent {_printer.BytesSent} of {data.Length}");
        Assert.Equal(0, _printer.Downloads);
    }

    [Fact]
    public async Task Previews_are_cached()
    {
        AddModel("Clip.gcode.3mf", Archive(sliceInfo: SliceInfo), DateTime.UtcNow);
        var library = Library();
        var model = (await library.RefreshAsync()).Single();
        await library.GetPreviewAsync(model);
        var reads = _printer.RangeReads;

        var (path, info) = await library.GetPreviewAsync(model);

        Assert.Equal(reads, _printer.RangeReads);
        Assert.NotNull(path);
        Assert.Equal(TimeSpan.FromSeconds(959), info.PrintTime);
        Assert.Equal(path, ModelLibrary.LoadCachedPreview(_root, model).PreviewPath);
    }

    [Fact]
    public async Task Archives_without_a_preview_or_not_zip_at_all_have_none()
    {
        AddModel("NoPreview.3mf", Archive(preview: false), DateTime.UtcNow);
        AddModel("Broken.3mf", Encoding.UTF8.GetBytes("not a zip file at all"), DateTime.UtcNow.AddMinutes(-1));
        var library = Library();

        foreach (var model in await library.RefreshAsync())
        {
            var (path, info) = await library.GetPreviewAsync(model);
            Assert.Null(path);
            Assert.False(info.HasPreview);
        }
    }

    [Fact]
    public void Ignores_malformed_slice_info()
    {
        Assert.Equal(ModelInfo.Empty, ModelArchive.ParseSliceInfo(Encoding.UTF8.GetBytes("<config><plate>")));
    }
}

public sealed class PrinterFileDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-fdl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Bracket v2 (final), A&B #3.gcode.3mf", "Bracket v2 (final), A&B #3.gcode.3mf")]
    [InlineData("挂钩 Größe.gcode.3mf", "挂钩 Größe.gcode.3mf")]
    [InlineData("Long name trunc....gcode.3mf", "Long name trunc....gcode.3mf")]
    [InlineData("a:b*c?.3mf", "a_b_c_.3mf")]
    [InlineData("../evil.3mf", ".._evil.3mf")]
    [InlineData("..", null)]
    [InlineData("  ", null)]
    public void Makes_names_safe_on_every_system(string remote, string? local) => Assert.Equal(local, PrinterFileDownloader.SafeFileName(remote));

    [Fact]
    public async Task Downloads_a_model_with_an_awkward_name()
    {
        var printer = new FakePrinter();
        var name = "Bracket v2 (final), A&B #3 挂钩.gcode.3mf";
        printer.AddFile("/" + name, 1234, DateTime.UtcNow);
        await using var session = new PrinterSession(new PrinterConnection("192.168.1.50", printer.AccessCode), printer.Create);

        var result = (await new PrinterFileDownloader(session).DownloadAsync(
            [new DownloadItem("/" + name, PrinterFileDownloader.SafeFileName(name)!, 1234)], _root))[0];

        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(printer.Files["/" + name].Data, await File.ReadAllBytesAsync(Path.Combine(_root, name)));
    }

    [Fact]
    public async Task Refuses_an_unsafe_target_name()
    {
        var printer = new FakePrinter();
        await using var session = new PrinterSession(new PrinterConnection("192.168.1.50", printer.AccessCode), printer.Create);

        var result = (await new PrinterFileDownloader(session).DownloadAsync([new DownloadItem("/x", "../x.3mf", 1)], _root))[0];

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Equal(0, printer.Downloads);
    }
}
