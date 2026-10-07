# Triopathy

Triopathy is a native conversation room for **macOS and Windows**. Give it a question or a context document, and several models take turns responding to the same conversation. Each participant receives the seed and recent contributions so it can build on the other perspectives.

The room includes **Hermes Local, WhiteLotus, BlackLotus, GreenLotus, Cheyenne, and Hal**, plus an optional **Codex** participant connected through ChatGPT or an OpenAI API key. Model servers can run on the same computer or elsewhere on your network.

| Platform | Application | Source |
| --- | --- | --- |
| macOS | Native SwiftUI app | `Triopathy/`, `Triopathy.xcodeproj` |
| Windows 10/11 x64 | Native C# / WPF app | [`windows/`](windows/) |

## Features

- Conversations with a chosen number of rounds; each available participant speaks once per round.
- Direct requests to configured OpenAI-compatible and Ollama model servers.
- UTF-8 text and JSON context loading, plus JSON and readable text transcript export.
- Offline participant skipping, cancellation, and continued discussion after a failed reply.
- Optional ChatGPT-plan sign-in or an API-key fallback, with protected credential storage.

Triopathy does not launch Hermes agents, invoke tools, or carry over Hermes session memory. Inference servers must already be running. The app reads configuration and sends conversation requests without changing server settings or model installations.

## Windows quick start

The Windows port is implemented and has been tested with replies from all six LAN model participants. See the [Windows guide](windows/README.md) for detailed configuration and diagnostics.

Install the **.NET 10 SDK**, then clone and build:

```powershell
git clone https://github.com/mgadziko/Triopathy.git
cd Triopathy
powershell -NoProfile -ExecutionPolicy Bypass -File windows/build.ps1 -Publish
.\windows\publish\Triopathy.Windows.exe
```

The script runs the behavior tests, builds the app, and publishes a self-contained x64 executable in `windows/publish`. The published app includes its .NET runtime. It is unsigned and currently has no installer or automatic updates. This repository contains source; the build command creates the executable locally.

1. Open **Connections** and confirm each participant's endpoint, model, protocol, and **Join** setting.
2. Save connections and click **Refresh hosts**.
3. Enter a seed, optionally load a context document, and choose 1–12 rounds.
4. Click **Begin conversation**. Use **Stop conversation** to cancel an active request.
5. Export the transcript as JSON or TXT from the **File** menu.

### Included LAN configuration

The owner's network configuration is bundled in [`network-defaults.json`](windows/Triopathy.Windows/network-defaults.json). Missing connections fall back to these entries; explicit saved endpoints and existing named Hermes profiles take priority. Disabled participants remain disabled. Use **Connections** to change the addresses, or edit the defaults before building for another network.

| Participant | Server | Model | Protocol |
| --- | --- | --- | --- |
| Hermes Local — MacBook Pro | `192.168.4.164:11434` | `qwen3-coder:q8-64k` | OpenAI-compatible |
| WhiteLotus | `192.168.4.165:11435` | `qwen3.8-27b-q4km` | OpenAI-compatible |
| BlackLotus | `192.168.4.150:11435` | `qwen3.8-27b-q4km` | OpenAI-compatible |
| GreenLotus | `192.168.4.57:11435` | `greenlotus-qwen` | OpenAI-compatible |
| Cheyenne | `192.168.4.101:11434` | `qwen3-coder:latest` | OpenAI-compatible |
| Hal | `192.168.4.78:11434` | `qwen-coder:latest` | Ollama native |

OpenAI-compatible endpoints use `http://<server>/v1/chat/completions`; Hal uses `http://<server>/api/chat`. Windows requests up to 512 response tokens at temperature 0.75, with thinking disabled in the configured request format. Context capacity is configured on the model server.

**Hermes Local is a participant name.** Its model can run on another computer. Use that computer's LAN address; `127.0.0.1` always refers to the computer running Triopathy.

Refresh and new conversations reload saved connections. Host badges test server reachability; a reachable server can still reject a request if the configured model is missing. Hover over an offline badge for its reason. `No profile or endpoint configured` means the app needs a connection address or a valid Hermes profile. An HTTP 404 can indicate that the requested model is not installed on that server.

## macOS quick start

Open `Triopathy.xcodeproj` in Xcode, or build from Terminal:

```sh
xcodebuild -project Triopathy.xcodeproj -scheme Triopathy -configuration Debug -destination 'platform=macOS,arch=arm64' CODE_SIGNING_ALLOWED=NO build
```

The macOS app reads the named Hermes profiles' live provider, endpoint, and model selection before each turn. Enter a seed, choose the rounds, and click **Begin Conversation**. **File > Load Context Seed…** imports a UTF-8 `.txt` or `.json` document. **Save as JSON…** and **Save as TXT…** export the transcript.

## ChatGPT / Codex

On Windows, open **Connections > Continue with ChatGPT**. On macOS, choose **Configure Codex… > Continue with ChatGPT**. Complete sign-in and authorization in your browser, then select an available model. Triopathy creates its own connection; it does not copy credentials from the Codex desktop app or another computer.

A connected ChatGPT plan takes priority over the optional API key. Plan failures do not silently switch to paid API usage. API usage is billed separately. Available models and plan access depend on the authorized account. Automated tests simulate sign-in; live plan authorization requires the user to complete the browser flow.

macOS protects credentials in the user's Keychain. Windows encrypts them with DPAPI for the current user. Credentials are not stored in this repository or included in builds. Windows saves nonsecret settings and connection diagnostics in `%LOCALAPPDATA%\Triopathy`.

## Verification

Run the Windows behavior tests and build independently:

```powershell
dotnet run --project windows/Triopathy.Tests -c Release
dotnet build windows/Triopathy.Windows -c Release
```

The suite contains 28 behavior tests covering configuration, request formats, conversation ordering, cancellation, transcript export, protected credentials, and the ChatGPT authorization flow. Additional WPF smoke checks cover bindings, layout, saved-connection reloads, and bundled network defaults.

After publishing, sample mode and an offscreen UI check are available:

```powershell
.\windows\publish\Triopathy.Windows.exe --demo
.\windows\publish\Triopathy.Windows.exe --smoke-test C:\Temp\Triopathy-smoke
```

These modes send no inference requests and do not change saved settings. For real model checks, see the [Windows guide's live-test instructions](windows/README.md#build-and-verify).
