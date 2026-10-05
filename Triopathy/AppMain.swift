import SwiftUI

@main
struct TriopathyApp: App {
    @StateObject private var viewModel = TriopathyViewModel()
    @AppStorage("discussionFontSize") private var discussionFontSize = 16.0

    var body: some Scene {
        WindowGroup {
            ContentView(viewModel: viewModel, discussionFontSize: discussionFontSize)
                .frame(minWidth: 980, minHeight: 720)
                .sheet(isPresented: $viewModel.showCodexSetup) {
                    CodexSetupView(viewModel: viewModel)
                }
        }
        .windowResizability(.contentSize)
        .commands {
            CommandGroup(after: .newItem) {
                Button("Load Context Seed…") { viewModel.loadContextSeed() }
                    .keyboardShortcut("o", modifiers: [.command, .shift])
                    .disabled(viewModel.isRunning)

                Button("Remove Loaded Context") { viewModel.clearLoadedContext() }
                    .disabled(viewModel.isRunning || viewModel.loadedSeedContext == nil)

                Divider()

                Button("Save as JSON…") { viewModel.saveTranscriptAsJSON() }
                    .disabled(viewModel.isRunning || viewModel.messages.isEmpty)

                Button("Save as TXT…") { viewModel.saveTranscriptAsText() }
                    .disabled(viewModel.isRunning || viewModel.messages.isEmpty)

                Divider()

                Button("Clear Conversation") { viewModel.clearConversation() }
                    .keyboardShortcut(.delete, modifiers: [.command])
                    .disabled(viewModel.isRunning || viewModel.messages.isEmpty)
            }
            CommandMenu("View") {
                Button("Larger Discussion Text") {
                    discussionFontSize = min(discussionFontSize + 1, 28)
                }
                .keyboardShortcut("+", modifiers: .command)
                .disabled(discussionFontSize >= 28)

                Button("Smaller Discussion Text") {
                    discussionFontSize = max(discussionFontSize - 1, 12)
                }
                .keyboardShortcut("-", modifiers: .command)
                .disabled(discussionFontSize <= 12)

                Button("Reset Discussion Text Size") {
                    discussionFontSize = 16
                }
                .keyboardShortcut("0", modifiers: .command)
                .disabled(discussionFontSize == 16)
            }
        }
    }
}
