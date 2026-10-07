using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Layerlapse.App.ViewModels;
using Layerlapse.App.Views;
using Layerlapse.Core.Credentials;
using Layerlapse.Core.Discovery;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;
using Layerlapse.Core.Tests;

namespace Layerlapse.App.Tests;

/// <summary>Headless Avalonia with real Skia rendering, so tests can save screenshots of views.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the main window in each connection state to PNG for visual review.
/// Opt-in: set LAYERLAPSE_RENDER_DIR to a folder. The wrong-code render also needs LAYERLAPSE_IP and
/// logs in once to the real printer with the fake code 00000000.
/// </summary>
[Trait("Category", "Render")]
public sealed class RenderConnectionStatesTests : IDisposable
{
    private static readonly string? OutputFolder = Environment.GetEnvironmentVariable("LAYERLAPSE_RENDER_DIR");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "layerlapse-render-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class RenderFactAttribute : FactAttribute
    {
        public RenderFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(OutputFolder))
            {
                Skip = "Set LAYERLAPSE_RENDER_DIR to render connection states to PNG.";
            }
        }
    }

    [RenderFact]
    public async Task Renders_every_state()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        var printer = new FakePrinter { Serial = "ZZZ000000000001" };
        var fakeDiscovery = new FakeDiscovery();
        fakeDiscovery.Printers.Add(FakeDiscovery.Announced(serial: "00M999999999999", host: "192.168.1.60", name: "Garage"));
        fakeDiscovery.Printers.Add(FakeDiscovery.Announced(serial: "01P000000000002", host: "192.168.1.61", modelCode: "C12", name: null));
        var credentials = new InMemoryCredentialStore();
        var profiles = new JsonPrinterProfileStore(Path.Combine(_folder, "printers.json"));
        ConnectionViewModel Launch() => new(new PrinterSetupService(credentials, profiles, printer.Create, fakeDiscovery), fakeDiscovery);

        await session.Dispatch(async () =>
        {
            var setupVm = Launch();
            await setupVm.InitializeAsync();
            await setupVm.SearchCommand.ExecuteAsync(null);
            Render(setupVm, "several-found", light: false);
            setupVm.SelectedDiscovered = null;
            setupVm.Host = "192.168.1.50";
            setupVm.AccessCode = printer.AccessCode;
            await setupVm.SaveCommand.ExecuteAsync(null);
            Render(setupVm, "connected-model-unknown", light: false);
            setupVm.ChosenModel = "P1S";
            await setupVm.SaveModelCommand.ExecuteAsync(null);
            Render(setupVm, "connected-dark", light: false);
            Render(setupVm, "connected-light", light: true);

            printer.Reachable = false;
            var unreachable = Launch();
            await unreachable.InitializeAsync();
            Render(unreachable, "unreachable", light: false);

            printer.Reachable = true;
            printer.Fingerprint = new string('B', 64);
            var changed = Launch();
            await changed.InitializeAsync();
            Render(changed, "certificate-changed", light: false);
            return 0;
        }, CancellationToken.None);
    }

    [RenderFact]
    public async Task Renders_wrong_code_from_the_real_printer()
    {
        var host = Environment.GetEnvironmentVariable("LAYERLAPSE_IP");
        if (string.IsNullOrWhiteSpace(host))
        {
            return; // Needs the printer's address; the fake-printer render above still runs.
        }

        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        var profiles = new JsonPrinterProfileStore(Path.Combine(_folder, "printers.json"));
        var service = new PrinterSetupService(new InMemoryCredentialStore(), profiles, c => new BambuFtpsClient(c));

        await session.Dispatch(async () =>
        {
            var vm = new ConnectionViewModel(service);
            await vm.InitializeAsync();
            vm.Host = host!;
            vm.AccessCode = "00000000";
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Contains("rejected the access code", vm.Error);
            vm.Host = "192.168.1.50"; // keep the real address out of the screenshot
            Render(vm, "wrong-code-real-printer", light: false);
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>Acceptance check: the real printer appears in the real view with no IP typed.</summary>
    [RenderFact]
    public async Task Renders_real_discovery()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        var discovery = new PrinterDiscovery();
        var profiles = new JsonPrinterProfileStore(Path.Combine(_folder, "printers.json"));
        var service = new PrinterSetupService(new InMemoryCredentialStore(), profiles, c => new BambuFtpsClient(c), discovery);

        await session.Dispatch(async () =>
        {
            var vm = new ConnectionViewModel(service, discovery);
            await vm.InitializeAsync();
            await vm.SearchCommand.ExecuteAsync(null);
            Assert.NotEmpty(vm.Discovered);
            Assert.NotNull(vm.SelectedDiscovered);
            Assert.False(string.IsNullOrEmpty(vm.Host));
            Render(vm, "real-discovery", light: false, redact: true);
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>Acceptance view for milestone 4: the real printer's timelapses with thumbnails.</summary>
    [RenderFact]
    public async Task Renders_real_timelapse_grid()
    {
        var host = Environment.GetEnvironmentVariable("LAYERLAPSE_IP");
        var code = Environment.GetEnvironmentVariable("LAYERLAPSE_CODE");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            var setup = new PrinterSetupService(
                new InMemoryCredentialStore(), new JsonPrinterProfileStore(Path.Combine(_folder, "p.json")), c => new BambuFtpsClient(c));
            var main = new MainViewModel(
                new ConnectionViewModel(setup), setup,
                id => new Layerlapse.Core.Timelapses.TimelapseCache(Path.Combine(_folder, "cache", Layerlapse.Core.Timelapses.TimelapseCache.SafeFolderName(id))),
                new NoPlayer());
            await main.InitializeAsync();
            main.Connection.Host = host;
            main.Connection.AccessCode = code;
            await main.Connection.SaveCommand.ExecuteAsync(null);

            for (var i = 0; i < 300 && (main.Timelapses is not { Items.Count: > 0 } grid || grid.Items.Take(12).Any(t => t.Thumbnail is null)); i++)
            {
                await Task.Delay(100);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(AppPage.Timelapses, main.CurrentPage);
            Assert.NotEmpty(main.Timelapses!.Items);
            var window = new MainWindow { DataContext = main, Width = 1200, Height = 760 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Save(window, "timelapse-grid");
            await main.Timelapses.DisposeAsync();
            return 0;
        }, CancellationToken.None);
    }

    private sealed class NoPlayer : Layerlapse.Core.Timelapses.IVideoPlayer
    {
        public void Play(string localPath)
        {
        }
    }

    private static void Save(Avalonia.Controls.Window window, string name)
    {
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing rendered.");
        Directory.CreateDirectory(OutputFolder!);
        frame.Save(Path.Combine(OutputFolder!, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
    }

    private static void Render(ConnectionViewModel connection, string name, bool light, bool redact = false)
    {
        if (redact)
        {
            // Keep the real address and serial out of saved screenshots.
            var real = connection.Discovered.Select(d => d.Printer).ToList();
            var selected = connection.SelectedDiscovered is not null;
            connection.Discovered.Clear();
            foreach (var p in real)
            {
                connection.Discovered.Add(new DiscoveredPrinterItem(p with { Host = "192.168.x.x", Serial = p.Serial[..3] + "…" }));
            }

            connection.Host = "192.168.x.x";
            if (selected)
            {
                connection.SelectedDiscovered = connection.Discovered[0];
                connection.Host = "192.168.x.x";
            }
        }

        Application.Current!.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        var window = new MainWindow { DataContext = new MainViewModel(connection) { IsLightTheme = light } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing rendered.");
        Directory.CreateDirectory(OutputFolder!);
        frame.Save(Path.Combine(OutputFolder!, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
    }
}
