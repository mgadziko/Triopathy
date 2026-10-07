# Triopathy

Triopathy is a native macOS SwiftUI conversation room for local Hermes profiles and an optional ChatGPT-plan participant.

- `hermes-local`
- `hermes-whitelotus`
- `hermes-blacklotus`
- `hermes-greenlotus`

It runs on the MacBook and reads each corresponding Hermes profile's live provider, endpoint, and model selection. It then sends a conversation-only request directly to that configured local model backend. Each turn receives the seed and a short recent transcript; tool schemas, agent workflows, and Hermes session memory are deliberately excluded so the three models can respond reliably as conversational participants.

## ChatGPT plan participant

Choose **Configure Codex…** and then **Continue with ChatGPT** to connect an eligible ChatGPT Plus or Pro account. The app follows OpenAI's local, open-source Sign in with ChatGPT flow: a browser authorizes Triopathy, the app validates the returned OpenID Connect token, and the protected connection is stored in this Mac user's Keychain. Codex then uses the selected account's ChatGPT plan rather than an API key.

The existing API-key form remains an optional fallback only. A connected ChatGPT plan takes priority. No API key, ChatGPT token, or transcript credential is written into this repository.

## Use

1. Open `Triopathy.xcodeproj` in Xcode or build from Terminal.
2. Enter a conversation seed. Optionally choose **File > Load Context Seed…** to import a UTF-8 `.txt` or `.json` document. JSON is formatted for readability; the loaded document is treated as reference history, and every participant is told to recognize any contributions labeled with its own name.
3. Choose the number of rounds; each participant speaks once per round.
4. Click **Begin Conversation**.
5. Use **File > Save as JSON…** for structured transcript data or **File > Save as TXT…** for a readable transcript.

The app does not change any Hermes profile or inference configuration. It uses whatever live endpoint and model each named profile is configured to use at the moment a turn begins.

## Windows version

A native C# / WPF Windows port is in `windows/`. It includes the conversation room, Hermes profile and endpoint configuration, ChatGPT-plan sign-in, API-key fallback, encrypted Windows credential storage, context loading, transcript export, and cancellation. See [Windows setup and build instructions](windows/README.md).

## macOS build

```sh
xcodebuild -project Triopathy.xcodeproj -scheme Triopathy -configuration Debug -destination 'platform=macOS,arch=arm64' CODE_SIGNING_ALLOWED=NO build
```
