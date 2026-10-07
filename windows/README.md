# Triopathy for Windows

Native Windows port of the macOS conversation room. Requires Windows 10/11 x64. The published self-contained executable includes .NET; developing the source requires the .NET 10 SDK.

## Run

Launch `Triopathy.Windows.exe`. Open **Connections** to configure participants, then enter a seed, choose 1–12 rounds, and select **Begin conversation**. **Stop conversation** cancels the active network request. Unavailable participants are skipped; a failed reply does not stop the remaining participants.

`Triopathy.Windows.exe --demo` provides a clearly marked sample mode with three canned participants. It sends no model requests and saves no settings or credentials.

## Hermes connections

Named participants use `<profiles folder>/<profile>/config.yaml` for `local`, `whitelotus`, `blacklotus`, `greenlotus`, `cheyenne`, and `hal`. Choose a Windows path or a WSL UNC path such as `\\wsl.localhost\Ubuntu\home\your-user\.hermes\profiles`. Files are read only and reread before every turn.

Hermes Local also supports a standalone configuration file. Its default is `%LOCALAPPDATA%\hermes\config.yaml`, matching this machine's existing Hermes installation. Neither a profile nor an inference server is created or modified automatically.

The parser accepts named-provider `api`/`default_model` configuration under `providers` or `custom_providers`, and current Hermes `model.base_url`/`model.default` configuration. An explicit endpoint and model in Connections overrides the file. `openai` protocol appends `/chat/completions` to a `/v1` base; `ollama` uses `/api/chat`. Both HTTP and HTTPS work. Uncheck **Join** to omit a participant. Reachability checks only open a TCP connection; they do not load a model or submit a prompt.

The app does not run Hermes itself, invoke tools, or carry over Hermes agent memory. A model inference server must already be running. Models are chosen from your configuration, never silently substituted.

This build includes the owner's LAN participant configuration in `Triopathy.Windows/network-defaults.json`, including Hermes Local on the MacBook at `192.168.4.164`. Missing connections fall back to these entries; explicit saved endpoints and existing named profiles take priority, and disabled participants stay disabled. Edit that file before building for another network. Refreshing hosts and starting a conversation reload saved connections. The app writes a nonsecret `connections-status.json` diagnostic alongside settings with the active addresses and availability reasons.

## ChatGPT / Codex

Select **Continue with ChatGPT** in Connections. The system browser asks you to sign in and authorize plan usage for Triopathy. Sign-in uses the documented dynamic open-source client registration, loopback callback, PKCE, OAuth state, and cryptographically verified OIDC identity. After connecting, choose a model and save connections. This is a separate Triopathy registration; the app does not read or modify the Codex desktop app's authentication.

Account-specific model choices come from the public model endpoint. Plan requests use the public Responses API with `store=false`, `stream=true`, and no tools. A completed event is required before accepting a reply. Refresh tokens are renewed automatically; access and usage remain subject to the selected account's permissions and plan limits. The included tests simulate authorization; live browser approval must be completed by the user.

An API key is an optional fallback. The original API default, `gpt-5.4`, is preserved and can be changed in Connections. API usage is billed separately. A connected ChatGPT plan takes priority; plan failures do not silently switch to paid API usage. **Disconnect** and **Remove saved API key** take effect immediately; **Close** does not undo them.

Credentials are encrypted with Windows DPAPI for the current user in `%LOCALAPPDATA%\Triopathy`. Nonsecret settings are in `settings.json`. The app keeps one active ChatGPT account; disconnect before adding a different account. Credentials are never stored in the repository or included in releases.

Official references:
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference

## Context and transcripts

Use **File → Load context seed** (Ctrl+Shift+O) for UTF-8 text or valid JSON. JSON is formatted for readability. Context is capped at 120,000 UTF-16 code units without splitting surrogate pairs; truncation is shown. Participants receive the seed, reference context, and the last nine successful contributions. Quoted material is described as reference history rather than executable instructions.

Use **Save as JSON** or **Save as TXT** to export. JSON preserves the macOS schema version, participant speaker identifiers, UUIDs, and ISO dates. Export uses the seed and context filename from the actual run, even after editing the seed field. Context contents are not embedded in the export, matching the original app. Larger/smaller discussion text uses Ctrl+plus/minus; Ctrl+0 resets it.

## Build and verify

From the repository root:

```powershell
dotnet run --project windows/Triopathy.Tests -c Release
dotnet build windows/Triopathy.Windows -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File windows/build.ps1 -Publish
```

The build script runs the behavior checks, builds, and optionally publishes a self-contained single executable to `windows/publish`. Set `-OutputDirectory` to another directory. Package versions are pinned in the project files and lock files. The classic `.sln` can be used by editors that support .NET 10; the installed Visual Studio 2022 may need an update, while command-line builds work independently.

The tests cover both profile formats, live configuration changes, wire formats, real chunked HTTP framing, TCP-only availability, round ordering, failed/offline participants, cancellation, history limits, import/export, DPAPI, SSE completion/failure, OAuth callbacks, signed token validation, refresh rotation, and a simulated complete browser registration. They require no real credentials or remote model requests.

For an offscreen WPF smoke test:

```powershell
Triopathy.Windows.exe --smoke-test C:\path\to\test-output
```

This renders the real windows, checks bindings and state transitions, exports sample transcripts, and exits with a result code and `smoke-report.json`. It uses in-memory credentials and sample responses; it does not alter user settings. The release is unsigned and does not include an installer or automatic updates.

`--live-test C:\path\to\test-output` runs one short real conversation using saved connections and writes a transcript and reachability report. It sends actual inference requests. Add `--participant hal` (or another participant ID) to check just one participant without changing the saved connection settings. Model behavior can vary; the prompt explicitly asks each participant to provide only its own contribution.
