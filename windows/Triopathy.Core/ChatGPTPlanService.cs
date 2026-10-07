using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Triopathy.Core;

public sealed record PlanModel(string Slug, string DisplayName);
public sealed class PlanProfile
{
    public string Email { get; set; } = "";
    public string Subject { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string HostId { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string[] Scopes { get; set; } = [];
    public string SelectedModel { get; set; } = "";
}

public sealed class ChatGPTPlanService(HttpClient http, ISecretStore secrets, AppSettings settings, OpenAIService openAI)
{
    private const string Scope = "chatgpt.tokens.use.direct";
    private readonly SemaphoreSlim refreshLock = new(1);
    private PlanProfile? Load() => secrets.Read("chatgpt-plan") is { } value ? JsonSerializer.Deserialize<PlanProfile>(value, Json.Options) : null;
    private void Save(PlanProfile profile) => secrets.Write("chatgpt-plan", JsonSerializer.Serialize(profile, Json.Options));
    public bool IsConnected => Load()?.Scopes.Contains(Scope) == true;
    public string AccountLabel => Load()?.Email ?? "Not connected";
    public string SelectedModel => Load()?.SelectedModel ?? "";
    public void Disconnect() => secrets.Delete("chatgpt-plan");
    public void SelectModel(string model) { var profile = Load() ?? throw new InvalidOperationException("Connect ChatGPT first."); profile.SelectedModel = model; Save(profile); }
    public async Task<IReadOnlyList<PlanModel>> ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        var existing = Load();
        settings.Save(); // Persist host identity before starting registration.
        var state = Random(); var nonce = Random(); var verifier = Random(48);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var listener = new OAuthLoopback();
        var callback = listener.Callback;
        var parameters = new Dictionary<string, string> {
            ["client_id"] = existing?.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = settings.HostId,
            ["response_type"] = "code", ["redirect_uri"] = callback.AbsoluteUri, ["scope"] = "openid profile email offline_access resource.invoke " + Scope,
            ["resource"] = "https://api.openai.com/v1", ["state"] = state, ["nonce"] = nonce,
            ["code_challenge_method"] = "S256", ["code_challenge"] = Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) };
        if (existing == null) parameters["agent_name_hint"] = "Triopathy";
        else { parameters["id_token_hint"] = existing.IdToken; parameters["login_hint"] = existing.Email; }
        openBrowser(new Uri("https://auth.openai.com/api/accounts/authorize?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)))));
        var values = await listener.WaitAsync(state, token);
        var clientId = ValidateCallback(values, state, existing?.ClientId);
        var tokens = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = values["code"], ["code_verifier"] = verifier, ["redirect_uri"] = callback.AbsoluteUri, ["resource"] = "https://api.openai.com/v1" }, token);
        var idToken = tokens.GetProperty("id_token").GetString() ?? throw new InvalidDataException("Sign-in returned no ID token.");
        var claims = await ValidateIdTokenAsync(idToken, clientId, nonce, token);
        if (existing != null && existing.Subject != claims.Subject) throw new InvalidDataException("The returned account does not match the connected account. Disconnect before adding a different account.");
        var scopes = tokens.GetProperty("scope").GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (!scopes.Contains(Scope)) throw new InvalidOperationException("ChatGPT plan usage was not granted. Continue with ChatGPT and authorize plan usage.");
        var profile = new PlanProfile { Email = claims.Email, Subject = claims.Subject, ClientId = clientId, HostId = settings.HostId, IdToken = idToken, Scopes = scopes, SelectedModel = existing?.SelectedModel ?? "" };
        ApplyTokens(profile, tokens); Save(profile);
        var models = await AvailableModelsAsync(token);
        if (models.Count > 0 && !models.Any(m => m.Slug == profile.SelectedModel)) SelectModel(models[0].Slug);
        return models;
    }
    public static string ValidateCallback(IReadOnlyDictionary<string, string> values, string state, string? existingClientId)
    {
        if (!values.TryGetValue("state", out var received) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(received))) throw new InvalidDataException("Invalid sign-in state.");
        if (values.ContainsKey("error")) throw new InvalidOperationException("ChatGPT sign-in was declined or could not be completed.");
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("Sign-in returned no authorization code.");
        values.TryGetValue("client_id", out var issued);
        if (existingClientId != null) return issued == null || issued == existingClientId ? existingClientId : throw new InvalidDataException("Sign-in returned a different client registration.");
        return !string.IsNullOrWhiteSpace(issued) && issued != "dynamic_agent_client" ? issued : throw new InvalidDataException("Sign-in registration is incomplete.");
    }
    public async Task<IReadOnlyList<PlanModel>> AvailableModelsAsync(CancellationToken token)
    {
        var profile = await FreshAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.AccessToken);
        using var response = await http.SendAsync(request, token); await OpenAIService.EnsureSuccessAsync(response, token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("models").EnumerateArray()
            .Where(m => m.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list")
            .Select(m => new PlanModel(m.GetProperty("slug").GetString()!, m.GetProperty("display_name").GetString()!)).ToArray();
    }
    public async Task<string> RespondAsync(string prompt, CancellationToken token)
    { var profile = await FreshAsync(token); return await openAI.RequestAsync(profile.AccessToken, profile.SelectedModel, prompt, true, token); }
    private async Task<PlanProfile> FreshAsync(CancellationToken token)
    {
        await refreshLock.WaitAsync(token);
        try
        {
            var profile = Load() ?? throw new InvalidOperationException("Continue with ChatGPT in Connections first.");
            if (!profile.Scopes.Contains(Scope)) throw new InvalidOperationException("ChatGPT plan usage is not enabled.");
            if (profile.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(90)) return profile;
            var result = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = profile.ClientId, ["refresh_token"] = profile.RefreshToken, ["resource"] = "https://api.openai.com/v1" }, token);
            ApplyTokens(profile, result);
            if (result.TryGetProperty("scope", out var scope)) profile.Scopes = (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!profile.Scopes.Contains(Scope)) throw new InvalidOperationException("ChatGPT plan usage is no longer enabled. Reconnect in Connections.");
            Save(profile); return profile;
        }
        finally { refreshLock.Release(); }
    }
    private static void ApplyTokens(PlanProfile profile, JsonElement result)
    {
        if (!string.Equals(result.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported token type.");
        profile.AccessToken = result.GetProperty("access_token").GetString() ?? throw new InvalidDataException("Missing access token.");
        if (result.TryGetProperty("refresh_token", out var refresh)) profile.RefreshToken = refresh.GetString() ?? profile.RefreshToken;
        profile.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(result.GetProperty("expires_in").GetInt32());
    }
    private async Task<JsonElement> TokenAsync(Dictionary<string, string> values, CancellationToken token)
    {
        using var response = await http.PostAsync("https://auth.openai.com/api/accounts/oauth/token", new FormUrlEncodedContent(values), token);
        await OpenAIService.EnsureSuccessAsync(response, token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); return document.RootElement.Clone();
    }
    public async Task<(string Subject, string Email)> ValidateIdTokenAsync(string token, string clientId, string nonce, CancellationToken cancellationToken)
    {
        var parts = token.Split('.'); if (parts.Length != 3) throw new InvalidDataException("Invalid ID token.");
        using var header = JsonDocument.Parse(Decode(parts[0]));
        if (header.RootElement.GetProperty("alg").GetString() != "RS256") throw new InvalidDataException("Unsupported ID-token signature.");
        using var configuration = JsonDocument.Parse(await http.GetStringAsync("https://auth.openai.com/.well-known/openid-configuration", cancellationToken));
        var config = configuration.RootElement;
        var jwksUri = new Uri(config.GetProperty("jwks_uri").GetString()!);
        if (jwksUri.Scheme != "https" || jwksUri.Host != "auth.openai.com") throw new InvalidDataException("Untrusted signing-key endpoint.");
        using var jwks = JsonDocument.Parse(await http.GetStringAsync(jwksUri, cancellationToken));
        var kid = header.RootElement.GetProperty("kid").GetString();
        var key = jwks.RootElement.GetProperty("keys").EnumerateArray().FirstOrDefault(k => k.GetProperty("kid").GetString() == kid && k.GetProperty("kty").GetString() == "RSA");
        if (key.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("ID-token signing key not found.");
        using var rsa = RSA.Create(); rsa.ImportParameters(new() { Modulus = Decode(key.GetProperty("n").GetString()!), Exponent = Decode(key.GetProperty("e").GetString()!) });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidDataException("ID-token signature validation failed.");
        using var claims = JsonDocument.Parse(Decode(parts[1])); var claim = claims.RootElement;
        var audience = claim.GetProperty("aud");
        var audienceMatches = audience.ValueKind == JsonValueKind.String ? audience.GetString() == clientId : audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().Any(a => a.GetString() == clientId);
        if (claim.GetProperty("iss").GetString() != "https://auth.openai.com" || config.GetProperty("issuer").GetString() != "https://auth.openai.com" || !audienceMatches || claim.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || claim.GetProperty("nonce").GetString() != nonce || string.IsNullOrWhiteSpace(claim.GetProperty("sub").GetString()))
            throw new InvalidDataException("ID-token identity validation failed.");
        return (claim.GetProperty("sub").GetString()!, claim.TryGetProperty("email", out var email) ? email.GetString() ?? "Connected ChatGPT account" : "Connected ChatGPT account");
    }
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string text) { var base64 = text.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')); }
    private static string Random(int bytes = 32) => Encode(RandomNumberGenerator.GetBytes(bytes));
}

public sealed class OAuthLoopback : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public Uri Callback { get; }
    public OAuthLoopback() { listener.Start(); Callback = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback"); }
    public async Task<Dictionary<string, string>> WaitAsync(string expectedState, CancellationToken token)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = new StringBuilder();
                // Read just the request line, capped to prevent a stalled/oversized callback.
                var character = new char[1];
                while (line.Length < 16_384)
                { if (await reader.ReadAsync(character.AsMemory(), readTimeout.Token) == 0) break; if (character[0] == '\n') break; line.Append(character[0]); }
                var parts = line.ToString().TrimEnd('\r').Split(' ');
                var validPath = parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith("/auth/callback?", StringComparison.Ordinal);
                var values = validPath ? ParseQuery(parts[1][(parts[1].IndexOf('?') + 1)..]) : new Dictionary<string, string>();
                var valid = validPath && values.TryGetValue("state", out var state) && state == expectedState;
                var body = valid ? "Sign-in response received. Return to Triopathy to finish connecting." : "Invalid callback. Return to the sign-in browser tab.";
                var payload = Encoding.UTF8.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(payload, readTimeout.Token);
                if (valid) return values;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UriFormatException) { }
        }
    }
    public static Dictionary<string, string> ParseQuery(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' ')); var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, value)) throw new InvalidDataException("Duplicate sign-in callback parameter.");
        }
        return result;
    }
    public void Dispose() => listener.Stop();
}
