import SwiftUI

struct CodexSetupView: View {
    @ObservedObject var viewModel: TriopathyViewModel
    @Environment(\.dismiss) private var dismiss
    @State private var apiKey = ""
    @State private var model = OpenAIService.configuredModel
    @State private var errorMessage: String?
    @State private var planModels: [ChatGPTPlanService.Model] = []
    @State private var planModel = ChatGPTPlanService.selectedModel ?? ""
    @State private var isConnectingPlan = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Connect Codex")
                .font(.title2.weight(.semibold))
            Text("Use your ChatGPT plan directly, or retain an API-key fallback. Triopathy keeps either credential only in this Mac user’s Keychain.")
                .font(.callout)
                .foregroundStyle(.secondary)

            GroupBox("ChatGPT plan") {
                VStack(alignment: .leading, spacing: 10) {
                    if let account = ChatGPTPlanService.accountLabel {
                        Text("Connected as \(account)")
                            .font(.callout.weight(.medium))
                        if !planModels.isEmpty {
                            Picker("Model", selection: $planModel) {
                                ForEach(planModels) { option in
                                    Text(option.displayName).tag(option.slug)
                                }
                            }
                            .onChange(of: planModel) { _, value in
                                try? ChatGPTPlanService.selectModel(value)
                            }
                        } else if let selected = ChatGPTPlanService.selectedModel {
                            Text("Using \(selected)").font(.caption).foregroundStyle(.secondary)
                        }
                        HStack {
                            Button("Refresh Models") { loadPlanModels() }
                            Button("Disconnect", role: .destructive) {
                                do {
                                    try ChatGPTPlanService.disconnect()
                                    planModels = []
                                    planModel = ""
                                    viewModel.refreshAvailability()
                                } catch { errorMessage = error.localizedDescription }
                            }
                        }
                    } else {
                        Text("Connect an eligible ChatGPT Plus or Pro account. Triopathy will open your browser and request permission to use your plan; no API key is required.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Button(isConnectingPlan ? "Connecting…" : "Continue with ChatGPT") {
                            connectPlan()
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(isConnectingPlan)
                    }
                }
                .padding(.top, 2)
            }

            Divider()

            GroupBox("API key fallback") {
                VStack(alignment: .leading, spacing: 8) {
                    SecureField(OpenAIService.hasAPIKey ? "API key saved — paste a replacement to change it" : "OpenAI API key", text: $apiKey)
                        .textFieldStyle(.roundedBorder)
                    TextField("Model", text: $model)
                        .textFieldStyle(.roundedBorder)
                    Text("Used only when no ChatGPT plan connection is active. API billing applies to this fallback.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .padding(.top, 2)
            }
            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }
            HStack {
                if OpenAIService.hasAPIKey {
                    Button("Remove Saved Key", role: .destructive) {
                        do {
                            try OpenAIService.removeAPIKey()
                            viewModel.refreshAvailability()
                            dismiss()
                        } catch { errorMessage = error.localizedDescription }
                    }
                }
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") {
                    do {
                        if !apiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                            try OpenAIService.save(apiKey: apiKey, model: model)
                        }
                        viewModel.refreshAvailability()
                        dismiss()
                    } catch { errorMessage = error.localizedDescription }
                }
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(24)
        .frame(width: 570)
        .task { loadPlanModels() }
    }

    private func connectPlan() {
        isConnectingPlan = true
        errorMessage = nil
        Task {
            do {
                let models = try await ChatGPTPlanService.connect()
                await MainActor.run {
                    planModels = models
                    planModel = ChatGPTPlanService.selectedModel ?? models.first?.slug ?? ""
                    isConnectingPlan = false
                    viewModel.refreshAvailability()
                }
            } catch {
                await MainActor.run {
                    isConnectingPlan = false
                    errorMessage = error.localizedDescription
                }
            }
        }
    }

    private func loadPlanModels() {
        guard ChatGPTPlanService.isConnected else { return }
        Task {
            do {
                let models = try await ChatGPTPlanService.availableModels()
                await MainActor.run {
                    planModels = models
                    if planModel.isEmpty { planModel = ChatGPTPlanService.selectedModel ?? models.first?.slug ?? "" }
                }
            } catch {
                await MainActor.run { errorMessage = error.localizedDescription }
            }
        }
    }
}
