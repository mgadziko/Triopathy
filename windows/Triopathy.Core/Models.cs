using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Triopathy.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record Participant(string Id, string Name, string Color)
{
    public static readonly Participant[] All = [
        new("local", "Hermes Local", "#467E72"), new("whitelotus", "WhiteLotus", "#9A782F"),
        new("blacklotus", "BlackLotus", "#70627F"), new("greenlotus", "GreenLotus", "#50783F"),
        new("cheyenne", "Cheyenne", "#AD6846"), new("hal", "Hal", "#4B78A0"), new("codex", "Codex", "#318F92")];
    public static readonly Participant System = new("system", "Triopathy", "#74797C");
    public static Participant Find(string id) => All.FirstOrDefault(p => p.Id == id) ?? System;
    public string ExportSpeaker => Id switch { "whitelotus" => "whiteLotus", "blacklotus" => "blackLotus", "greenlotus" => "greenLotus", _ => Id };
}

public sealed record ConversationMessage(Guid Id, string Speaker, string Text, DateTimeOffset Date)
{
    public static ConversationMessage Create(Participant speaker, string text) => new(Guid.NewGuid(), speaker.ExportSpeaker, text, DateTimeOffset.UtcNow);
    [JsonIgnore] public Participant Participant => Participant.Find(Speaker.Replace("Lotus", "lotus"));
}

public sealed record LoadedContext(string Filename, string Text, bool Truncated);
public sealed record TranscriptExport(int SchemaVersion, DateTimeOffset ExportedAt, string ConversationSeed, string? LoadedContextFilename, IReadOnlyList<ConversationMessage> Messages);

public static class Transcripts
{
    public static LoadedContext Load(string path)
    {
        // Reject invalid UTF-8 rather than silently corrupting reference material.
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(text);
            text = JsonSerializer.Serialize(document.RootElement, Json.Options);
        }
        var truncated = text.Length > 120_000;
        if (truncated) { var length = 120_000; if (char.IsHighSurrogate(text[length - 1])) length--; text = text[..length]; }
        return new(Path.GetFileName(path), text, truncated);
    }
    public static string AsJson(string seed, LoadedContext? context, IReadOnlyList<ConversationMessage> messages) =>
        JsonSerializer.Serialize(new TranscriptExport(1, DateTimeOffset.UtcNow, seed, context?.Filename, messages), Json.Options);
    public static string AsText(IEnumerable<ConversationMessage> messages) => string.Join("\n\n", messages.Select(m => $"[{m.Participant.Name.ToUpperInvariant()}]\n{m.Text}"));
    public static void AtomicWrite(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class ParticipantConfig
{
    [JsonIgnore] public string Name => Participant.Find(Id).Name;
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Protocol { get; set; } = "openai";
}

public sealed class AppSettings
{
    [JsonIgnore] public string? StorageDirectory { get; set; }
    public string ProfilesDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes", "profiles");
    public string LocalConfigFile { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "config.yaml");
    public List<ParticipantConfig> Participants { get; set; } = Participant.All.Where(p => p.Id != "codex").Select(p => new ParticipantConfig { Id = p.Id, Protocol = p.Id == "hal" ? "ollama" : "openai" }).ToList();
    public bool CodexEnabled { get; set; } = true;
    public string ApiModel { get; set; } = "gpt-5.4";
    public double FontSize { get; set; } = 16;
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid().ToString().ToLowerInvariant();
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Triopathy");
    public static AppSettings Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? DataDirectory, "settings.json");
        if (!File.Exists(path)) return new() { StorageDirectory = directory };
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException("The settings file is empty.");
        settings.FontSize = Math.Clamp(settings.FontSize, 12, 28);
        settings.Participants ??= [];
        foreach (var participant in Participant.All.Where(p => p.Id != "codex"))
            if (!settings.Participants.Any(p => p.Id == participant.Id)) settings.Participants.Add(new() { Id = participant.Id, Protocol = participant.Id == "hal" ? "ollama" : "openai" });
        settings.StorageDirectory = directory;
        return settings;
    }
    public void Save(string? directory = null)
    {
        directory ??= StorageDirectory ?? DataDirectory;
        Directory.CreateDirectory(directory);
        Transcripts.AtomicWrite(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(this, Json.Options));
    }
}
