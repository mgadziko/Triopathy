import Foundation

enum HermesProfile: String, CaseIterable, Identifiable {
    case whiteLotus = "whitelotus"
    case blackLotus = "blacklotus"
    case greenLotus = "greenlotus"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .whiteLotus: return "WhiteLotus"
        case .blackLotus: return "BlackLotus"
        case .greenLotus: return "GreenLotus"
        }
    }
}

enum HermesServiceError: LocalizedError {
    case missingExecutable
    case noResponse(profile: HermesProfile, stderr: String)
    case terminated

    var errorDescription: String? {
        switch self {
        case .missingExecutable:
            return "The local Hermes executable was not found at ~/.local/bin/hermes."
        case let .noResponse(profile, stderr):
            let detail = stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            return detail.isEmpty ? "\(profile.displayName) did not return a response." : "\(profile.displayName): \(detail)"
        case .terminated:
            return "The Hermes turn was stopped."
        }
    }
}

final class HermesService {
    private let executableURL: URL
    private let workspaceURL: URL
    private var activeProcess: Process?

    init(
        executableURL: URL = URL(fileURLWithPath: NSString(string: "~/.local/bin/hermes").expandingTildeInPath),
        workspaceURL: URL = URL(fileURLWithPath: NSString(string: "~/Documents/GitHub/Triopathy").expandingTildeInPath)
    ) {
        self.executableURL = executableURL
        self.workspaceURL = workspaceURL
    }

    func stop() {
        activeProcess?.terminate()
        activeProcess = nil
    }

    func respond(profile: HermesProfile, prompt: String) async throws -> String {
        guard FileManager.default.isExecutableFile(atPath: executableURL.path) else {
            throw HermesServiceError.missingExecutable
        }

        return try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            let outputPipe = Pipe()
            let errorPipe = Pipe()
            process.executableURL = executableURL
            process.arguments = ["-p", profile.rawValue, "--in", workspaceURL.path, "-z", prompt]
            process.standardOutput = outputPipe
            process.standardError = errorPipe
            process.terminationHandler = { [weak self] finishedProcess in
                let output = String(data: outputPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
                let error = String(data: errorPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
                DispatchQueue.main.async { self?.activeProcess = nil }

                guard finishedProcess.terminationReason != .uncaughtSignal else {
                    continuation.resume(throwing: HermesServiceError.terminated)
                    return
                }

                let response = output.trimmingCharacters(in: .whitespacesAndNewlines)
                guard finishedProcess.terminationStatus == 0, !response.isEmpty else {
                    continuation.resume(throwing: HermesServiceError.noResponse(profile: profile, stderr: error))
                    return
                }
                continuation.resume(returning: response)
            }

            do {
                self.activeProcess = process
                try process.run()
            } catch {
                self.activeProcess = nil
                continuation.resume(throwing: error)
            }
        }
    }
}
