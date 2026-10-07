import SwiftUI

struct ContentView: View {
    @ObservedObject var viewModel: TriopathyViewModel
    let discussionFontSize: Double

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            transcript
            Divider()
            controls
        }
        .padding()
    }

    private var header: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 5) {
                Text("Triopathy")
                    .font(.largeTitle.weight(.bold))
                Text("A conversation among your local Hermes profiles and optional ChatGPT plan")
                    .foregroundStyle(.secondary)
                HStack(spacing: 8) {
                    ForEach(viewModel.participants.indices, id: \.self) { participantIndex in
                        let participant = viewModel.participants[participantIndex]
                        participantBadge(participant, index: participantIndex)
                    }
                }
            }
            Spacer()
            Text(viewModel.statusText)
                .font(.caption)
                .padding(.horizontal, 10)
                .padding(.vertical, 7)
                .background(.secondary.opacity(0.12))
                .clipShape(Capsule())
        }
        .padding(.bottom, 14)
    }

    private func participantBadge(_ participant: Participant, index: Int) -> some View {
        HStack(spacing: 5) {
            Circle().fill(viewModel.isAvailable(participant.name) ? color(for: participant.name) : .gray).frame(width: 8, height: 8)
            Toggle(isOn: $viewModel.participants[index].isEnabled) {
                Text(viewModel.speakerName(participant.name))
            }
            Text(participant.isEnabled ? (viewModel.isAvailable(participant.name) ? "\(viewModel.counts[participant.name, default: 0])" : "Offline") : "Off")
                .foregroundStyle(.secondary)
        }
        .font(.caption)
        .padding(.horizontal, 9)
        .padding(.vertical, 5)
        .background((viewModel.isAvailable(participant.name) ? color(for: participant.name) : .gray).opacity(0.13))
        .clipShape(Capsule())
        .opacity(participant.isEnabled && viewModel.isAvailable(participant.name) ? 1 : 0.55)
    }

    private var transcript: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 12) {
                    if viewModel.messages.isEmpty {
                        ContentUnavailableView("Begin with a seed", systemImage: "bubble.left.and.bubble.right", description: Text("Triopathy will let every reachable participant take a turn."))
                            .padding(.top, 100)
                    }
                    ForEach(viewModel.messages) { message in
                        messageBubble(message).id(message.id)
                    }
                }
                .padding(14)
            }
            .background(Color(nsColor: .textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 14))
            .onChange(of: viewModel.transcriptRevision) { _, _ in
                if let last = viewModel.messages.last {
                    withAnimation(.easeOut(duration: 0.15)) { proxy.scrollTo(last.id, anchor: .bottom) }
                }
            }
        }
    }

    private func messageBubble(_ message: ConversationMessage) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(viewModel.speakerName(message.speaker))
                .font(.caption.weight(.semibold))
                .foregroundStyle(color(for: message.speaker))
            Text(message.text.isEmpty ? "Thinking…" : message.text)
                .font(.system(size: discussionFontSize))
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(13)
        .background(color(for: message.speaker).opacity(message.speaker == .system ? 0.10 : 0.16))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    private var controls: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Conversation seed")
                .font(.headline)
            if let loadedContext = viewModel.loadedSeedContext {
                HStack(spacing: 8) {
                    Label("Loaded context: \(loadedContext.filename)", systemImage: "doc.text")
                    Text("\(loadedContext.text.count.formatted()) characters")
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Remove") { viewModel.clearLoadedContext() }
                        .disabled(viewModel.isRunning)
                }
                .font(.caption)
            }
            TextEditor(text: $viewModel.seed)
                .font(.body)
                .frame(height: 82)
                .padding(7)
                .background(Color(nsColor: .textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 9))
                .disabled(viewModel.isRunning)

            DisclosureGroup("Web access") {
                VStack(alignment: .leading, spacing: 8) {
                    Toggle("Share web research with every participant", isOn: $viewModel.webEnabled)
                    TextField("Search query (uses the seed when blank)", text: $viewModel.webQuery)
                    TextField("Public page URLs, separated by spaces", text: $viewModel.webURLs)
                    Text("Web access is off by default. Triopathy retrieves up to three public pages and gives every participant the same source packet; participants do not browse independently.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if !viewModel.webSources.isEmpty {
                        ForEach(viewModel.webSources) { source in
                            Link("[\(viewModel.webSources.firstIndex(of: source).map { $0 + 1 } ?? 0)] \(source.title)", destination: source.url)
                                .font(.caption)
                        }
                    }
                }
                .padding(.top, 4)
            }
            .disabled(viewModel.isRunning)

            HStack(spacing: 12) {
                Text("Rounds").foregroundStyle(.secondary)
                Stepper(value: $viewModel.rounds, in: 1...12) {
                    Text("\(viewModel.rounds) per participant").monospacedDigit()
                }
                .frame(width: 190)
                .disabled(viewModel.isRunning)
                Text("Local participants use their live models; Codex can use your connected ChatGPT plan.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Refresh Hosts") { viewModel.refreshAvailability() }
                    .disabled(viewModel.isRunning)
                Button("Configure Codex…") { viewModel.showCodexSetup = true }
                    .disabled(viewModel.isRunning)
                Button("Clear") { viewModel.clearConversation() }
                    .disabled(viewModel.messages.isEmpty || viewModel.isRunning)
                if viewModel.isRunning {
                    Button("Stop") { viewModel.stopConversation() }
                        .buttonStyle(.borderedProminent)
                        .tint(.red)
                } else {
                    Button("Begin Conversation") { viewModel.startConversation() }
                        .buttonStyle(.borderedProminent)
                }
            }
        }
        .padding(.top, 14)
    }

    private func color(for speaker: ConversationMessage.Speaker) -> Color {
        switch speaker {
        case .local: return .mint
        case .whiteLotus: return .purple
        case .blackLotus: return .blue
        case .greenLotus: return .green
        case .cheyenne: return .cyan
        case .hal: return .indigo
        case .codex: return .teal
        case .system: return .orange
        }
    }
}

#Preview {
    ContentView(viewModel: TriopathyViewModel(), discussionFontSize: 16)
}
