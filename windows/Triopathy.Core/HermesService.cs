using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Triopathy.Core;

public sealed record Backend(Uri Endpoint, string Model, string Protocol);
public sealed record Availability(bool Available, string Detail);

public interface IConversationBackend
{
    Task<Availability> CheckAsync(Participant participant, CancellationToken cancellationToken);
    Task<string> RespondAsync(Participant participant, string prompt, CancellationToken cancellationToken);
}

public sealed class HermesService(AppSettings settings, HttpClient http)
{
    public Backend Resolve(Participant participant)
    {
        var custom = settings.Participants.First(p => p.Id == participant.Id);
        if (!custom.Enabled) throw new InvalidOperationException("Disabled");
        if (!string.IsNullOrWhiteSpace(custom.Endpoint))
            return MakeBackend(custom.Endpoint, custom.Model, custom.Protocol);
        var path = Path.Combine(settings.ProfilesDirectory, participant.Id, "config.yaml");
        if (!File.Exists(path) && participant.Id == "local") path = settings.LocalConfigFile;
        if (!File.Exists(path)) throw new InvalidOperationException("No profile or endpoint configured");
        return ParseConfiguration(File.ReadAllText(path), custom.Protocol);
    }

    public static Backend ParseConfiguration(string text, string protocol)
    {
        var yaml = new YamlStream(); yaml.Load(new StringReader(text));
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root) throw new InvalidDataException("Expected a YAML configuration mapping.");
        var model = Map(root, "model");
        var provider = Value(model, "provider");
        // Original named-provider format, plus the current Hermes base_url/default format.
        var providerConfig = Map(Map(root, "providers"), provider) ?? Map(Map(root, "custom_providers"), provider) ?? Map(root, provider);
        var api = Value(providerConfig, "api") ?? Value(providerConfig, "base_url") ?? Value(model, "base_url");
        var name = Value(providerConfig, "default_model") ?? Value(model, "default") ?? Value(model, "name") ?? Value(model, "model");
        if (string.IsNullOrWhiteSpace(api) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("The Hermes profile must specify a model and a local/custom endpoint.");
        return MakeBackend(api, name, protocol);
    }
    private static YamlMappingNode? Map(YamlMappingNode? root, string? key) => key != null && root?.Children.TryGetValue(new YamlScalarNode(key), out var value) == true ? value as YamlMappingNode : null;
    private static string? Value(YamlMappingNode? root, string key) => root?.Children.TryGetValue(new YamlScalarNode(key), out var value) == true ? (value as YamlScalarNode)?.Value : null;

    public static Backend MakeBackend(string endpoint, string model, string protocol)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidDataException("Choose a model for this participant.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Use an HTTP or HTTPS endpoint without credentials, query, or fragment.");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (protocol == "ollama")
        {
            if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
            if (!path.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase)) path += "/api/chat";
        }
        else if (protocol == "openai")
        {
            if (path.Length == 0) path = "/v1";
            if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) path += "/chat/completions";
        }
        else throw new InvalidDataException("Choose openai or ollama protocol.");
        return new(new UriBuilder(uri) { Path = path }.Uri, model.Trim(), protocol);
    }

    public async Task<Availability> CheckAsync(Participant participant, CancellationToken cancellationToken)
    {
        try
        {
            var backend = Resolve(participant);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var socket = new TcpClient();
            await socket.ConnectAsync(backend.Endpoint.Host, backend.Endpoint.Port, timeout.Token);
            return new(true, backend.Model);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "Connection timed out"); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, ex is SocketException ? "Model server is unreachable" : ex.Message); }
    }

    public async Task<string> RespondAsync(Participant participant, string prompt, CancellationToken cancellationToken)
    {
        var backend = Resolve(participant); // Reread live configuration for every turn.
        object payload = backend.Protocol == "ollama"
            ? new { model = backend.Model, messages = new[] { new { role = "user", content = prompt } }, stream = false, think = false, options = new { temperature = 0.75, num_predict = 512 } }
            : new { model = backend.Model, messages = new[] { new { role = "user", content = prompt } }, max_tokens = 512, temperature = 0.75, chat_template_kwargs = new { enable_thinking = false } };
        using var response = await http.PostAsJsonAsync(backend.Endpoint, payload, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Model server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseReply(document.RootElement, backend.Protocol);
    }
    public static string ParseReply(JsonElement root, string protocol)
    {
        JsonElement message;
        if (protocol == "ollama") message = root.GetProperty("message");
        else
        {
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) throw new InvalidDataException("The model returned no completion choices.");
            message = choices[0].GetProperty("message");
        }
        foreach (var key in new[] { "content", "reasoning_content", "reasoning" })
            if (message.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
        throw new InvalidDataException("The model returned no usable text.");
    }
}
