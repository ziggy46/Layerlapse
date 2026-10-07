using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Layerlapse.App.ViewModels;
using Layerlapse.App.Views;
using Layerlapse.Core.Credentials;
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
        var printer = new FakePrinter();
        var credentials = new InMemoryCredentialStore();
        var profiles = new JsonPrinterProfileStore(Path.Combine(_folder, "printers.json"));
        ConnectionViewModel Launch() => new(new PrinterSetupService(credentials, profiles, printer.Create));

        await session.Dispatch(async () =>
        {
            var setupVm = Launch();
            await setupVm.InitializeAsync();
            setupVm.Host = "192.168.1.50";
            setupVm.AccessCode = printer.AccessCode;
            await setupVm.SaveCommand.ExecuteAsync(null);
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

    private static void Render(ConnectionViewModel connection, string name, bool light)
    {
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
