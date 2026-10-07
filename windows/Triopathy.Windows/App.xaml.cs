using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Triopathy.Core;

namespace Triopathy.Windows;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = Array.IndexOf(e.Args, "--smoke-test");
        var live = Array.IndexOf(e.Args, "--live-test");
        try
        {
            var demo = smoke >= 0 || e.Args.Contains("--demo");
            var services = new Services(demo); var vm = new MainViewModel(services, demo, smoke >= 0 || live >= 0); var window = new MainWindow(vm);
            MainWindow = window;
            if (live >= 0)
            {
                if (live + 1 >= e.Args.Length) throw new ArgumentException("--live-test requires an output folder.");
                var selection = Array.IndexOf(e.Args, "--participant");
                if (selection >= 0)
                {
                    if (selection + 1 >= e.Args.Length || !Participant.All.Any(p => p.Id == e.Args[selection + 1])) throw new ArgumentException("Choose a known participant ID.");
                    var id = e.Args[selection + 1];
                    foreach (var participant in services.Settings.Participants) participant.Enabled = participant.Id == id;
                    services.Settings.CodexEnabled = id == "codex";
                }
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await LiveAsync(vm, e.Args[live + 1], e.Args.Contains("--web-test"));
                vm.Dispose(); Shutdown(0); return;
            }
            if (smoke >= 0)
            {
                if (smoke + 1 >= e.Args.Length) throw new ArgumentException("--smoke-test requires an output folder.");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await SmokeAsync(window, e.Args[smoke + 1]);
                vm.Dispose(); Shutdown(0); return;
            }
            window.Show();
        }
        catch (Exception ex)
        {
            var diagnostic = smoke >= 0 ? smoke : live;
            if (diagnostic >= 0 && diagnostic + 1 < e.Args.Length) { Directory.CreateDirectory(e.Args[diagnostic + 1]); File.WriteAllText(Path.Combine(e.Args[diagnostic + 1], "diagnostic-error.txt"), ex.ToString()); }
            else MessageBox.Show("Triopathy could not start: " + ex.Message, "Triopathy", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private static async Task LiveAsync(MainViewModel vm, string directory, bool webTest = false)
    {
        Directory.CreateDirectory(directory);
        vm.Engine.StatusChanged += status => Transcripts.AtomicWrite(Path.Combine(directory, "live-status.txt"), status);
        vm.Seed = "What is one practical benefit of hearing several perspectives before making a decision? Reply in one concise sentence and build on the earlier contributions if there are any.";
        if (webTest)
        {
            vm.WebEnabled = true; vm.WebQuery = "Ollama official API documentation"; vm.WebUrls = "https://docs.ollama.com/api/introduction";
            vm.Seed = "According to the supplied web sources, what is Ollama's default local API base URL? Cite a source URL and keep your response concise.";
        }
        vm.Rounds = 1;
        await vm.StartAsync();
        if (vm.CanExport) { vm.Export(Path.Combine(directory, "live-transcript.json"), true); vm.Export(Path.Combine(directory, "live-transcript.txt"), false); }
        var results = Participant.All.Select(p => new {
            participant = p.Name,
            reachable = vm.Participants.First(item => item.Id == p.Id).Available,
            detail = vm.Participants.First(item => item.Id == p.Id).Detail,
            successfulReplies = vm.Engine.Messages.Count(m => m.Participant.Id == p.Id && !m.Text.StartsWith("(No reply:") && m.Text != "(Stopped)" && m.Text.Length > 0)
        }).ToArray();
        Transcripts.AtomicWrite(Path.Combine(directory, "live-report.json"), JsonSerializer.Serialize(new { finished = true, checkedAt = DateTimeOffset.UtcNow, results }, Json.Options));
    }
    private async Task SmokeAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        using (var reloadServices = new Services(true))
        {
            var emptySettings = new AppSettings { ProfilesDirectory = Path.Combine(directory, "missing-profiles") };
            emptySettings.Participants.First(p => p.Id == "hal").Enabled = false;
            Services.ApplyNetworkDefaults(emptySettings);
            if (emptySettings.Participants.Any(p => string.IsNullOrWhiteSpace(p.Endpoint)) || emptySettings.Participants.First(p => p.Id == "hal").Enabled)
                throw new InvalidOperationException("Bundled connections did not fill missing configuration or preserved disabled state incorrectly.");
            emptySettings.Participants.First(p => p.Id == "local").Endpoint = "http://custom-host:11434/v1/chat/completions";
            Services.ApplyNetworkDefaults(emptySettings);
            if (emptySettings.Participants.First(p => p.Id == "local").Endpoint != "http://custom-host:11434/v1/chat/completions")
                throw new InvalidOperationException("Bundled defaults overwrote an explicit endpoint.");
            reloadServices.Settings.StorageDirectory = Path.Combine(directory, "reload-regression");
            reloadServices.Settings.Save();
            var externallySaved = AppSettings.Load(reloadServices.Settings.StorageDirectory);
            externallySaved.Participants.First(p => p.Id == "whitelotus").Endpoint = "http://192.168.4.165:11435/v1/chat/completions";
            externallySaved.Participants.First(p => p.Id == "whitelotus").Model = "fixture";
            externallySaved.Save();
            reloadServices.ReloadConnections();
            if (reloadServices.Settings.Participants.First(p => p.Id == "whitelotus").Endpoint != "http://192.168.4.165:11435/v1/chat/completions")
                throw new InvalidOperationException("Connection settings remained stale after reload.");
        }
        var trace = new BindingTrace(); PresentationTraceSources.DataBindingSource.Listeners.Add(trace); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var vm = window.ViewModel;
        await vm.RefreshAsync();
        if (vm.Participants.Count(p => p.Available) != 3) throw new InvalidOperationException("Availability did not update.");
        Capture(window, Path.Combine(directory, "empty-room.png"), 1160, 820);
        if (vm.StartCommand.CanExecute(null)) throw new InvalidOperationException("Empty seed enabled Start.");
        var input = Path.Combine(directory, "sample-context.json"); File.WriteAllText(input, "{\"history\":\"[HAL] Earlier sample contribution.\"}"); vm.LoadContext(input);
        vm.Seed = "What makes a good conversation?"; vm.Rounds = 1;
        await vm.StartAsync();
        if (vm.IsRunning || !vm.CanExport || vm.Participants.Where(p => p.Available).Any(p => p.Count != 1)) throw new InvalidOperationException("Conversation UI state did not complete.");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        window.ScrollToOpening();
        Capture(window, Path.Combine(directory, "sample-conversation.png"), 1160, 820);
        window.WebAccessExpander.IsExpanded = true;
        vm.SetWebSources([new("Example source link", "https://example.com", "Sample source")]);
        vm.WebEnabled = true; vm.WebQuery = "Sample search query";
        Capture(window, Path.Combine(directory, "web-access.png"), 1160, 920);
        window.WebAccessExpander.IsExpanded = false; vm.WebEnabled = false; vm.SetWebSources([]);
        vm.Seed = "Changed after the run";
        vm.Export(Path.Combine(directory, "sample-transcript.json"), true); vm.Export(Path.Combine(directory, "sample-transcript.txt"), false);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "sample-transcript.json"))))
            if (doc.RootElement.GetProperty("conversationSeed").GetString() != "What makes a good conversation?") throw new InvalidOperationException("Export seed changed after editing.");
        vm.FontSize = 100; if (vm.FontSize != 28) throw new InvalidOperationException("Font bounds failed."); vm.FontSize = 16;
        var connections = new ConnectionsWindow(vm.Services, true); Capture(connections, Path.Combine(directory, "connections.png"), 980, 820);
        connections.ScrollToCredentials(); Capture(connections, Path.Combine(directory, "credentials.png"), 980, 820);
        vm.Clear(); if (vm.HasMessages || vm.CanExport) throw new InvalidOperationException("Clear state failed.");
        vm.RemoveContextCommand.Execute(null); if (vm.HasContext) throw new InvalidOperationException("Remove context failed.");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (trace.Errors.Count > 0) throw new InvalidOperationException("WPF binding errors: " + string.Join("\n", trace.Errors));
        File.WriteAllText(Path.Combine(directory, "smoke-report.json"), JsonSerializer.Serialize(new { passed = true, checks = new[] { "availability", "empty-seed guard", "context import", "conversation completion", "participant counts", "JSON/TXT export", "export snapshot", "font bounds", "connections layout", "clear", "remove context", "WPF bindings" } }, Json.Options));
        PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
    }
    private static void Capture(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var context = background.RenderOpen()) context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class BindingTrace : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
