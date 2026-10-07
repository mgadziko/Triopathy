using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Triopathy.Core;

namespace Triopathy.Windows;

public sealed class RelayCommand(Action action, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => action();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(property); return true; }
}
public sealed class MessageItem(ConversationMessage message) : Bindable
{
    public Guid Id => message.Id;
    public string Name => message.Participant.Name;
    public string Color => message.Participant.Color;
    public string Timestamp => message.Date.ToLocalTime().ToString("HH:mm");
    private string text = message.Text;
    public string Text { get => text.Length == 0 ? "Thinking…" : text; set { text = value; Notify(); } }
}
public sealed class ParticipantItem(Participant participant) : Bindable
{
    public string Id => participant.Id;
    public string Name => participant.Name;
    public string Color => participant.Color;
    private bool available;
    private string detail = "Checking…";
    private int count;
    public bool Available { get => available; set { Set(ref available, value); Notify(nameof(Indicator)); Notify(nameof(Label)); } }
    public string Indicator => Available ? Color : "#AEB5B7";
    public string Detail { get => detail; set => Set(ref detail, value); }
    public int Count { get => count; set { Set(ref count, value); Notify(nameof(Label)); } }
    public string Label => Available ? $"{Count} turn{(Count == 1 ? "" : "s")}" : "Offline";
}

public sealed class Services : IDisposable
{
    public AppSettings Settings { get; }
    public ISecretStore Secrets { get; }
    private readonly HttpClient localHttp = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };
    private readonly HttpClient cloudHttp = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly HttpClient webHttp = WebResearch.CreateClient();
    public IWebResearch Web { get; }
    public OpenAIService OpenAI { get; }
    public ChatGPTPlanService Plan { get; }
    public IConversationBackend Backend { get; }
    public static void ApplyNetworkDefaults(AppSettings settings)
    {
        using var stream = typeof(Services).Assembly.GetManifestResourceStream("Triopathy.NetworkDefaults");
        if (stream == null) return;
        var defaults = System.Text.Json.JsonSerializer.Deserialize<List<ParticipantConfig>>(stream, Json.Options) ?? [];
        foreach (var configured in settings.Participants)
        {
            if (!string.IsNullOrWhiteSpace(configured.Endpoint) || File.Exists(Path.Combine(settings.ProfilesDirectory, configured.Id, "config.yaml"))) continue;
            var initial = defaults.FirstOrDefault(p => p.Id == configured.Id);
            if (initial == null) continue;
            configured.Endpoint = initial.Endpoint; configured.Model = initial.Model; configured.Protocol = initial.Protocol;
        }
    }
    public void ReloadConnections()
    {
        var saved = AppSettings.Load(Settings.StorageDirectory);
        ApplyNetworkDefaults(saved);
        Settings.Participants = saved.Participants;
        Settings.ProfilesDirectory = saved.ProfilesDirectory;
        Settings.LocalConfigFile = saved.LocalConfigFile;
        Settings.CodexEnabled = saved.CodexEnabled;
        Settings.ApiModel = saved.ApiModel;
    }
    public Services(bool demo = false)
    {
        Settings = demo ? new() : AppSettings.Load(); Secrets = demo ? new MemorySecrets() : new WindowsSecretStore();
        if (!demo) ApplyNetworkDefaults(Settings);
        OpenAI = new(cloudHttp, Secrets, Settings); Plan = new(cloudHttp, Secrets, Settings, OpenAI);
        Backend = demo ? new DemoBackend() : new BackendRouter(Settings, new(Settings, localHttp), OpenAI, Plan);
        Web = new WebResearch(webHttp);
    }
    public void Dispose() { localHttp.Dispose(); cloudHttp.Dispose(); webHttp.Dispose(); }
}
internal sealed class MemorySecrets : ISecretStore
{
    private readonly Dictionary<string, string> values = [];
    public string? Read(string name) => values.GetValueOrDefault(name);
    public void Write(string name, string value) => values[name] = value;
    public void Delete(string name) => values.Remove(name);
}
internal sealed class DemoBackend : IConversationBackend
{
    public Task<Availability> CheckAsync(Participant p, CancellationToken token) => Task.FromResult(new Availability(p.Id is "local" or "whitelotus" or "hal", "Sample participant · no network requests"));
    public Task<string> RespondAsync(Participant p, string prompt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(p.Id switch {
            "local" => "A good conversation leaves room for several perspectives. We can start with a shared question, then give each voice time to develop a distinct idea.",
            "whitelotus" => "I would add that listening matters as much as speaking. Each contribution should build on something already said, while still leaving an opening for the next participant.",
            _ => "Then perhaps the useful test is whether we finish with a clearer question than we began with. A short round gives us enough structure to discover that together." });
    }
}

public sealed class MainViewModel : Bindable, IDisposable
{
    public Services Services { get; }
    public ConversationEngine Engine { get; }
    public ObservableCollection<MessageItem> Messages { get; } = [];
    public ObservableCollection<WebSource> WebSources { get; } = [];
    public bool HasWebSources => WebSources.Count > 0;
    public void SetWebSources(IReadOnlyList<WebSource> sources)
    { WebSources.Clear(); foreach (var source in sources) WebSources.Add(source); Notify(nameof(HasWebSources)); }
    public ObservableCollection<ParticipantItem> Participants { get; } = new(Participant.All.Select(p => new ParticipantItem(p)));
    public int[] RoundChoices { get; } = Enumerable.Range(1, 12).ToArray();
    private string seed = "", status = "Ready";
    private bool running, checking;
    private int rounds = 2;
    private double fontSize;
    private LoadedContext? context;
    private CancellationTokenSource? active;
    private readonly CancellationTokenSource lifetime = new();
    private string exportSeed = "";
    private LoadedContext? exportContext;
    private bool disposed;
    public bool Demo { get; }
    public bool PersistSettings { get; }
    private bool webEnabled;
    private string webQuery = "", webUrls = "";
    public bool WebEnabled { get => webEnabled; set => Set(ref webEnabled, value); }
    public string WebQuery { get => webQuery; set => Set(ref webQuery, value); }
    public string WebUrls { get => webUrls; set => Set(ref webUrls, value); }
    public string Title => Demo ? "Triopathy · Sample conversation" : "Triopathy";
    public string Subtitle => Demo ? "Sample mode — no model requests or settings changes" : "A conversation among your local models and optional ChatGPT plan";
    public string Seed { get => seed; set { Set(ref seed, value); CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => status; set => Set(ref status, value); }
    public int Rounds { get => rounds; set => Set(ref rounds, value); }
    public double FontSize { get => fontSize; set { Set(ref fontSize, Math.Clamp(value, 12, 28)); Services.Settings.FontSize = fontSize; } }
    public bool IsRunning { get => running; private set { Set(ref running, value); Notify(nameof(CanEdit)); Notify(nameof(CanExport)); Notify(nameof(StartLabel)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsChecking { get => checking; private set { Set(ref checking, value); Notify(nameof(CanEdit)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanEdit => !IsRunning && !IsChecking;
    public bool CanExport => !IsRunning && Messages.Count > 0;
    public bool HasMessages => Messages.Count > 0;
    public bool HasContext => context != null;
    public string ContextLabel => context == null ? "" : $"{context.Filename} · {context.Text.Length:N0} characters{(context.Truncated ? " · truncated" : "")}";
    public string StartLabel => IsRunning ? "Stop conversation" : "Begin conversation";
    public ICommand StartCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RemoveContextCommand { get; }
    public event Action? ScrollRequested;
    public MainViewModel(Services services, bool demo, bool diagnostics = false)
    {
        Services = services; Demo = demo; PersistSettings = !demo && !diagnostics; fontSize = services.Settings.FontSize; Engine = new(services.Backend, demo ? null : services.Web);
        webEnabled = services.Settings.WebEnabled; webQuery = services.Settings.WebQuery; webUrls = services.Settings.WebUrls;
        StartCommand = new RelayCommand(() => { if (IsRunning) Stop(); else _ = StartAsync(); }, () => !IsChecking && (IsRunning || !string.IsNullOrWhiteSpace(Seed) || HasContext));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => CanEdit);
        ClearCommand = new RelayCommand(Clear, () => CanEdit && HasMessages);
        RemoveContextCommand = new RelayCommand(() => { context = null; Notify(nameof(HasContext)); Notify(nameof(ContextLabel)); Status = "Removed loaded context"; CommandManager.InvalidateRequerySuggested(); }, () => CanEdit && HasContext);
        Engine.StatusChanged += value => Status = value;
        Engine.WebSourcesChanged += SetWebSources;
        Engine.AvailabilityChanged += states => {
            foreach (var p in Participants) { p.Available = states[p.Id].Available; p.Detail = states[p.Id].Detail; }
            if (PersistSettings)
            {
                try
                {
                    var directory = Services.Settings.StorageDirectory ?? AppSettings.DataDirectory;
                    Directory.CreateDirectory(directory);
                    Transcripts.AtomicWrite(Path.Combine(directory, "connections-status.json"), System.Text.Json.JsonSerializer.Serialize(new {
                        checkedAt = DateTimeOffset.UtcNow, executable = Environment.ProcessPath,
                        settingsFile = Path.Combine(directory, "settings.json"),
                        settingsExist = File.Exists(Path.Combine(directory, "settings.json")),
                        settingsBytes = File.Exists(Path.Combine(directory, "settings.json")) ? new FileInfo(Path.Combine(directory, "settings.json")).Length : 0,
                        parsedFromDisk = AppSettings.Load(directory).Participants.Select(p => new { p.Id, p.Endpoint }),
                        participants = Participant.All.Select(p => new { p.Name, states[p.Id].Available, states[p.Id].Detail,
                            endpoint = Services.Settings.Participants.FirstOrDefault(c => c.Id == p.Id)?.Endpoint })
                    }, Json.Options));
                }
                catch { /* Diagnostics must not interrupt a conversation. */ }
            }
        };
        Engine.MessageAdded += message => {
            if (Engine.Messages.Count == 1) { Messages.Clear(); foreach (var p in Participants) p.Count = 0; }
            Messages.Add(new(message)); Notify(nameof(HasMessages)); Notify(nameof(CanExport)); ScrollRequested?.Invoke(); };
        Engine.MessageUpdated += message => {
            Messages.First(m => m.Id == message.Id).Text = message.Text;
            var p = Participants.FirstOrDefault(p => p.Id == message.Participant.Id);
            if (p != null) p.Count = Engine.Messages.Count(m => m.Participant.Id == p.Id);
            ScrollRequested?.Invoke(); };
    }
    public async Task RefreshAsync()
    {
        if (!CanEdit) return; IsChecking = true; Status = "Checking participants…";
        try { if (PersistSettings) Services.ReloadConnections(); await Engine.CheckAsync(lifetime.Token); Status = "Ready"; }
        catch (OperationCanceledException) { } catch (Exception ex) { Status = ex.Message; } finally { IsChecking = false; }
    }
    public async Task StartAsync()
    {
        if (!CanEdit) return; IsRunning = true; active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var actualSeed = string.IsNullOrWhiteSpace(Seed) ? "Continue the loaded context." : Seed.Trim(); var actualContext = context;
        try {
            if (PersistSettings) {
                Services.ReloadConnections();
                Services.Settings.WebEnabled = WebEnabled; Services.Settings.WebQuery = WebQuery; Services.Settings.WebUrls = WebUrls;
                Services.Settings.Save();
            }
            await Engine.RunAsync(Seed, context, Rounds, active.Token, WebEnabled, WebQuery, WebUrls);
            if (Engine.LastRunStarted) { exportSeed = actualSeed; exportContext = actualContext; }
        }
        catch (Exception ex) { Status = ex.Message; } finally { active.Dispose(); active = null; IsRunning = false; }
    }
    public void Stop() { active?.Cancel(); Status = "Stopping…"; }
    public void Clear()
    {
        if (!CanEdit) return; Engine.Messages.Clear(); Messages.Clear(); WebSources.Clear(); Notify(nameof(HasWebSources)); foreach (var p in Participants) p.Count = 0;
        Notify(nameof(HasMessages)); Notify(nameof(CanExport)); Status = "Cleared";
    }
    public void LoadContext(string path)
    {
        if (!CanEdit) return; context = Transcripts.Load(path); Notify(nameof(HasContext)); Notify(nameof(ContextLabel)); Status = "Loaded " + ContextLabel; CommandManager.InvalidateRequerySuggested();
    }
    public void Export(string path, bool json)
    {
        if (!CanExport) throw new InvalidOperationException("There is no transcript to save.");
        Transcripts.AtomicWrite(path, json ? Transcripts.AsJson(exportSeed, exportContext, Engine.Messages) : Transcripts.AsText(Engine.Messages)); Status = "Transcript saved";
    }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); active?.Cancel(); lifetime.Dispose(); Services.Dispose(); }
}
