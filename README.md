# Triopathy

Triopathy is a native macOS SwiftUI conversation room for three local Hermes profiles:

- `hermes-whitelotus`
- `hermes-blacklotus`
- `hermes-greenlotus`

It runs on the MacBook and starts one isolated Hermes one-shot turn at a time using the corresponding profile (`hermes -p <profile> -z`). Each turn receives the seed and a short recent transcript. Triopathy prompts participants to remain in conversational mode and not invoke tools.

## Use

1. Open `Triopathy.xcodeproj` in Xcode or build from Terminal.
2. Enter a conversation seed.
3. Choose the number of rounds; each participant speaks once per round.
4. Click **Begin Conversation**.

The app does not change any Hermes profile or inference configuration. It uses whatever live endpoint and model each named profile is configured to use at the moment a turn begins.

## Build

```sh
xcodebuild -project Triopathy.xcodeproj -scheme Triopathy -configuration Debug -destination 'platform=macOS,arch=arm64' CODE_SIGNING_ALLOWED=NO build
```
