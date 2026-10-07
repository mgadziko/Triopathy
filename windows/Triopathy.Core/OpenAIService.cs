using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Triopathy.Core;

public sealed class OpenAIService(HttpClient http, ISecretStore secrets, AppSettings settings)
{
    public const string Instructions = "You are Codex, one thoughtful participant in a multi-host conversation. Speak only as Codex; never script another participant's replies or imitate their labels. Respond only to the conversation prompt. Do not claim access to tools, files, host machines, accounts, or this app. Be concise: one to three paragraphs. Build on a distinct point or ask a useful question.";
    public bool HasApiKey => !string.IsNullOrWhiteSpace(secrets.Read("api-key"));
    public Task<string> RespondAsync(string prompt, CancellationToken token) => RequestAsync(secrets.Read("api-key") ?? throw new InvalidOperationException("Add an API key in Connections."), settings.ApiModel, prompt, false, token);
    public async Task<string> RequestAsync(string credential, string model, string prompt, bool plan, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Select a Codex model in Connections.");
        object payload = plan
            ? new { model, instructions = Instructions, input = new[] { new { role = "user", content = prompt } }, store = false, stream = true, tools = Array.Empty<object>() }
            : new { model, instructions = Instructions, input = prompt, max_output_tokens = 500, store = false, tools = Array.Empty<object>() };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await EnsureSuccessAsync(response, token);
        if (plan)
        {
            using var stream = await response.Content.ReadAsStreamAsync(token);
            return await ReadStreamAsync(stream, token);
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (document.RootElement.TryGetProperty("status", out var status) && status.GetString() is "failed" or "incomplete")
            throw new InvalidDataException("OpenAI did not complete this response.");
        var text = new StringBuilder();
        foreach (var output in document.RootElement.GetProperty("output").EnumerateArray())
            if (output.TryGetProperty("content", out var content)) foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("type", out var type) && type.GetString() == "output_text" && item.TryGetProperty("text", out var value)) text.Append(value.GetString());
        return RequireText(text.ToString());
    }
    public static async Task<string> ReadStreamAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var text = new StringBuilder(); var completed = false; var data = new StringBuilder();
        void Event()
        {
            if (data.Length == 0) return;
            var payload = data.ToString(); data.Clear();
            if (payload == "[DONE]") return;
            using var document = JsonDocument.Parse(payload); var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "response.output_text.delta") text.Append(root.GetProperty("delta").GetString());
            if (type == "response.completed") completed = true;
            if (type is "response.failed" or "response.incomplete" or "error") throw new InvalidDataException("ChatGPT did not complete the response. Check your connection, selected model, and plan limits.");
        }
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.Length == 0) { Event(); continue; }
            if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart(' ')); }
        }
        Event();
        if (!completed) throw new InvalidDataException("ChatGPT ended the response before completion.");
        return RequireText(text.ToString());
    }
    private static string RequireText(string text) => !string.IsNullOrWhiteSpace(text) ? text.Trim() : throw new InvalidDataException("OpenAI returned no usable text.");
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var code = "";
        // Report a structured code, never echo raw responses that might contain credentials.
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var candidate = value.GetString() ?? "";
                if (candidate.Length <= 100 && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) code = " " + candidate;
            }
        }
        catch (JsonException) { }
        throw new HttpRequestException($"OpenAI returned HTTP {(int)response.StatusCode}.{code} Check sign-in, model access, and usage limits.");
    }
}
