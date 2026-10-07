namespace Triopathy.Core;

public sealed class BackendRouter(AppSettings settings, HermesService hermes, OpenAIService openAI, ChatGPTPlanService plan) : IConversationBackend
{
    public Task<Availability> CheckAsync(Participant participant, CancellationToken token)
    {
        if (participant.Id != "codex") return hermes.CheckAsync(participant, token);
        try
        {
            return Task.FromResult(!settings.CodexEnabled ? new Availability(false, "Disabled") : plan.IsConnected
                ? new Availability(!string.IsNullOrWhiteSpace(plan.SelectedModel), "ChatGPT plan · " + plan.SelectedModel)
                : new Availability(openAI.HasApiKey, openAI.HasApiKey ? "API key · " + settings.ApiModel : "Not connected"));
        }
        catch (Exception) { return Task.FromResult(new Availability(false, "Credentials could not be read. Reconnect in Connections.")); }
    }
    public Task<string> RespondAsync(Participant participant, string prompt, CancellationToken token) => participant.Id == "codex"
        ? plan.IsConnected ? plan.RespondAsync(prompt, token) : openAI.RespondAsync(prompt, token)
        : hermes.RespondAsync(participant, prompt, token);
}
