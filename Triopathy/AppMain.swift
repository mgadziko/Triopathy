import SwiftUI

@main
struct TriopathyApp: App {
    @StateObject private var viewModel = TriopathyViewModel()

    var body: some Scene {
        WindowGroup {
            ContentView(viewModel: viewModel)
                .frame(minWidth: 980, minHeight: 720)
        }
        .windowResizability(.contentSize)
        .commands {
            CommandGroup(after: .newItem) {
                Button("Clear Conversation") { viewModel.clearConversation() }
                    .keyboardShortcut(.delete, modifiers: [.command])
                    .disabled(viewModel.isRunning || viewModel.messages.isEmpty)
            }
        }
    }
}
