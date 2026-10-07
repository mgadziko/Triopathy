import Foundation
import Network

enum HermesProfile: String, CaseIterable, Identifiable {
    case local = "local"
    case whiteLotus = "whitelotus"
    case blackLotus = "blacklotus"
    case greenLotus = "greenlotus"
    case cheyenne = "cheyenne"
    case hal = "hal"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .local: return "Organon"
        case .whiteLotus: return "WhiteLotus"
        case .blackLotus: return "BlackLotus"
        case .greenLotus: return "GreenLotus"
        case .cheyenne: return "Cheyenne"
        case .hal: return "Hal"
        }
    }

    /// Triopathy uses one consistent turn budget across every local participant.
    /// This is high enough for a complete short contribution without changing
    /// a server's model, context window, or concurrency configuration.
    var triopathyOutputTokenLimit: Int {
        512
    }
}

enum HermesServiceError: LocalizedError {
    case profileConfigurationUnavailable(HermesProfile)
    case noResponse(profile: HermesProfile, detail: String)
    case terminated

    var errorDescription: String? {
        switch self {
        case let .profileConfigurationUnavailable(profile):
            return "Triopathy could not read the live Hermes configuration for \(profile.displayName)."
        case let .noResponse(profile, detail):
            return detail.isEmpty ? "\(profile.displayName) did not return a response." : "\(profile.displayName): \(detail)"
        case .terminated:
            return "The turn was stopped."
        }
    }
}

private struct ProfileBackend {
    enum ProtocolStyle {
        case openAIChat
        case ollamaNativeChat
    }

    let apiURL: URL
    let model: String
    let protocolStyle: ProtocolStyle
}

private struct ConversationRequest: Encodable {
    struct Message: Encodable {
        let role: String
        let content: String
    }

    let model: String
    let messages: [Message]
    let maxTokens: Int
    let temperature: Double
    let chatTemplateKwargs: [String: Bool]

    enum CodingKeys: String, CodingKey {
        case model, messages, temperature
        case maxTokens = "max_tokens"
        case chatTemplateKwargs = "chat_template_kwargs"
    }
}

private struct ConversationResponse: Decodable {
    struct Choice: Decodable {
        struct Message: Decodable {
            let content: String?
            let reasoningContent: String?
            let reasoning: String?

            enum CodingKeys: String, CodingKey {
                case content
                case reasoningContent = "reasoning_content"
                case reasoning
            }
        }

        let message: Message
        let finishReason: String?

        enum CodingKeys: String, CodingKey {
            case message
            case finishReason = "finish_reason"
        }
    }

    let choices: [Choice]
}

private struct NativeOllamaConversationRequest: Encodable {
    struct Message: Encodable {
        let role: String
        let content: String
    }

    struct Options: Encodable {
        let temperature: Double
        let numPredict: Int

        enum CodingKeys: String, CodingKey {
            case temperature
            case numPredict = "num_predict"
        }
    }

    let model: String
    let messages: [Message]
    let stream: Bool
    let think: Bool
    let options: Options
}

private struct NativeOllamaConversationResponse: Decodable {
    struct Message: Decodable {
        let content: String?
    }

    let message: Message
    let doneReason: String?

    enum CodingKeys: String, CodingKey {
        case message
        case doneReason = "done_reason"
    }
}

private final class RequestBox {
    var connection: NWConnection?
    var task: URLSessionDataTask?
    var completed = false
}

final class HermesService {
    private let profilesDirectory: URL
    private var activeConnection: NWConnection?
    private var activeTask: URLSessionDataTask?

    init(
        profilesDirectory: URL = URL(fileURLWithPath: NSString(string: "~/.hermes/profiles").expandingTildeInPath)
    ) {
        self.profilesDirectory = profilesDirectory
    }

    func stop() {
        activeTask?.cancel()
        activeTask = nil
        activeConnection?.cancel()
        activeConnection = nil
    }

    /// Tests only whether the profile's configured model service accepts a LAN connection.
    /// It never submits a prompt or loads the model.
    func isAvailable(profile: HermesProfile) async -> Bool {
        guard let backend = try? backend(for: profile),
              let host = backend.apiURL.host,
              let port = NWEndpoint.Port(rawValue: UInt16(backend.apiURL.port ?? 80)) else {
            return false
        }

        return await withCheckedContinuation { continuation in
            let connection = NWConnection(host: NWEndpoint.Host(host), port: port, using: .tcp)
            let box = RequestBox()
            box.connection = connection
            func finish(_ available: Bool) {
                guard !box.completed else { return }
                box.completed = true
                connection.cancel()
                continuation.resume(returning: available)
            }
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready: finish(true)
                case .failed, .cancelled: finish(false)
                default: break
                }
            }
            connection.start(queue: .global(qos: .utility))
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 2) {
                finish(false)
            }
        }
    }

    func respond(profile: HermesProfile, prompt: String) async throws -> String {
        let backend = try backend(for: profile)
        let data: Data
        switch backend.protocolStyle {
        case .openAIChat:
            let requestBody = ConversationRequest(
                model: backend.model,
                messages: [.init(role: "user", content: prompt)],
                maxTokens: profile.triopathyOutputTokenLimit,
                temperature: 0.75,
                chatTemplateKwargs: ["enable_thinking": false]
            )
            data = try await directRequest(to: backend.apiURL, body: JSONEncoder().encode(requestBody), profile: profile)
        case .ollamaNativeChat:
            let requestBody = NativeOllamaConversationRequest(
                model: backend.model,
                messages: [.init(role: "user", content: prompt)],
                stream: false,
                think: false,
                options: .init(temperature: 0.75, numPredict: profile.triopathyOutputTokenLimit)
            )
            data = try await directRequest(to: backend.apiURL, body: JSONEncoder().encode(requestBody), profile: profile)
            let decoded = try JSONDecoder().decode(NativeOllamaConversationResponse.self, from: data)
            let response = decoded.message.content?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            if !response.isEmpty { return response }
            let finishDescription = decoded.doneReason.map { " (finish reason: \($0))" } ?? ""
            throw HermesServiceError.noResponse(profile: profile, detail: "The model returned no final text\(finishDescription).")
        }

        let decoded = try JSONDecoder().decode(ConversationResponse.self, from: data)
        guard let choice = decoded.choices.first else {
            throw HermesServiceError.noResponse(profile: profile, detail: "The model returned no completion choices.")
        }
        let finalText = choice.message.content?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !finalText.isEmpty {
            return finalText
        }

        // Some Qwen/Ollama-compatible backends can put a complete answer in the
        // reasoning channel even when their final-content channel is empty.
        let reasoningText = (choice.message.reasoningContent ?? choice.message.reasoning)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !reasoningText.isEmpty {
            return reasoningText
        }

        let finishDescription = choice.finishReason.map { " (finish reason: \($0))" } ?? ""
        throw HermesServiceError.noResponse(profile: profile, detail: "The model returned neither final text nor reasoning text\(finishDescription).")
    }

    private func sessionRequest(to url: URL, body: Data, profile: HermesProfile) async throws -> Data {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 120
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = body

        let box = RequestBox()
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 120
            let session = URLSession(configuration: configuration)
            let task = session.dataTask(with: request) { [weak self] data, response, error in
                DispatchQueue.main.async {
                    if self?.activeTask === box.task {
                        self?.activeTask = nil
                    }
                }
                if let urlError = error as? URLError, urlError.code == .cancelled {
                    continuation.resume(throwing: HermesServiceError.terminated)
                    return
                }
                if let error {
                    let diagnostic = error as NSError
                    let detail = "Network request failed: \(diagnostic.domain) (\(diagnostic.code)) — \(diagnostic.localizedDescription)"
                    continuation.resume(throwing: HermesServiceError.noResponse(profile: profile, detail: detail))
                    return
                }
                guard let httpResponse = response as? HTTPURLResponse, let data else {
                    continuation.resume(throwing: HermesServiceError.noResponse(profile: profile, detail: "No data returned."))
                    return
                }
                guard (200...299).contains(httpResponse.statusCode) else {
                    let detail = String(data: data, encoding: .utf8) ?? "HTTP \(httpResponse.statusCode)"
                    continuation.resume(throwing: HermesServiceError.noResponse(profile: profile, detail: detail))
                    return
                }
                continuation.resume(returning: data)
            }
            box.task = task
            activeTask = task
            task.resume()
        }
    }

    private func directRequest(to url: URL, body: Data, profile: HermesProfile) async throws -> Data {
        guard let host = url.host,
              let port = NWEndpoint.Port(rawValue: UInt16(url.port ?? 80)) else {
            throw HermesServiceError.profileConfigurationUnavailable(profile)
        }
        let path = url.path.isEmpty ? "/" : url.path
        let request = "POST \(path) HTTP/1.1\r\nHost: \(host)\r\nContent-Type: application/json\r\nContent-Length: \(body.count)\r\nConnection: close\r\n\r\n"
        let payload = Data(request.utf8) + body
        let box = RequestBox()

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
            let connection = NWConnection(host: NWEndpoint.Host(host), port: port, using: .tcp)
            box.connection = connection
            activeConnection = connection
            var received = Data()

            func finish(_ result: Result<Data, Error>) {
                guard !box.completed else { return }
                box.completed = true
                connection.cancel()
                if self.activeConnection === connection {
                    self.activeConnection = nil
                }
                continuation.resume(with: result)
            }

            func receiveMore() {
                connection.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { data, _, complete, error in
                    if let data { received.append(data) }
                    if let error {
                        finish(.failure(HermesServiceError.noResponse(profile: profile, detail: error.localizedDescription)))
                        return
                    }

                    if let separator = received.range(of: Data("\r\n\r\n".utf8)) {
                        let header = String(decoding: received[..<separator.lowerBound], as: UTF8.self)
                        guard header.hasPrefix("HTTP/1.1 2") || header.hasPrefix("HTTP/1.0 2") else {
                            finish(.failure(HermesServiceError.noResponse(profile: profile, detail: header.components(separatedBy: "\r\n").first ?? "HTTP request failed.")))
                            return
                        }
                        let contentLength = header
                            .components(separatedBy: "\r\n")
                            .first { $0.lowercased().hasPrefix("content-length:") }
                            .flatMap { Int($0.split(separator: ":", maxSplits: 1).last?.trimmingCharacters(in: .whitespaces) ?? "") }
                        let isChunked = header
                            .components(separatedBy: "\r\n")
                            .contains { $0.lowercased().contains("transfer-encoding: chunked") }
                        if let contentLength {
                            let bodyEnd = separator.upperBound + contentLength
                            if received.count >= bodyEnd {
                                finish(.success(Data(received[separator.upperBound..<bodyEnd])))
                                return
                            }
                        } else if isChunked,
                                  let decoded = Self.decodeChunkedBody(Data(received[separator.upperBound...])) {
                            finish(.success(decoded))
                            return
                        }
                    }

                    if complete {
                        guard let separator = received.range(of: Data("\r\n\r\n".utf8)) else {
                            finish(.failure(HermesServiceError.noResponse(profile: profile, detail: "Incomplete HTTP response.")))
                            return
                        }
                        let header = String(decoding: received[..<separator.lowerBound], as: UTF8.self)
                        let isChunked = header
                            .components(separatedBy: "\r\n")
                            .contains { $0.lowercased().contains("transfer-encoding: chunked") }
                        let body = Data(received[separator.upperBound...])
                        if isChunked, let decoded = Self.decodeChunkedBody(body) {
                            finish(.success(decoded))
                        } else if !isChunked {
                            // HTTP/1.0 and some close-delimited HTTP/1.1 responses
                            // legitimately omit Content-Length.
                            finish(.success(body))
                        } else {
                            finish(.failure(HermesServiceError.noResponse(profile: profile, detail: "Incomplete chunked HTTP response.")))
                        }
                    } else {
                        receiveMore()
                    }
                }
            }

            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    connection.send(content: payload, completion: .contentProcessed { error in
                        if let error {
                            finish(.failure(HermesServiceError.noResponse(profile: profile, detail: error.localizedDescription)))
                        } else {
                            receiveMore()
                        }
                    })
                case let .failed(error):
                    finish(.failure(HermesServiceError.noResponse(profile: profile, detail: error.localizedDescription)))
                case .cancelled:
                    finish(.failure(HermesServiceError.terminated))
                default:
                    break
                }
            }
            connection.start(queue: .global(qos: .userInitiated))
        }
    }

    /// Decodes a complete HTTP/1.1 chunked body. Nil means more network data is
    /// required; this lets the direct LAN transport support Ollama's streamed
    /// response framing without falling back to URLSession.
    private static func decodeChunkedBody(_ data: Data) -> Data? {
        let bytes = Array(data)
        var cursor = 0
        var decoded = Data()

        func nextCRLF(from index: Int) -> Int? {
            guard index < bytes.count else { return nil }
            for position in index..<(bytes.count - 1) where bytes[position] == 13 && bytes[position + 1] == 10 {
                return position
            }
            return nil
        }

        while true {
            guard let lineEnd = nextCRLF(from: cursor),
                  let line = String(bytes: bytes[cursor..<lineEnd], encoding: .ascii),
                  let length = Int(line.split(separator: ";", maxSplits: 1).first ?? "", radix: 16) else {
                return nil
            }
            cursor = lineEnd + 2
            if length == 0 {
                return decoded
            }
            let chunkEnd = cursor + length
            guard chunkEnd + 2 <= bytes.count,
                  bytes[chunkEnd] == 13,
                  bytes[chunkEnd + 1] == 10 else {
                return nil
            }
            decoded.append(contentsOf: bytes[cursor..<chunkEnd])
            cursor = chunkEnd + 2
        }
    }

    private func backend(for profile: HermesProfile) throws -> ProfileBackend {
        let configurationURL = profilesDirectory.appendingPathComponent(profile.rawValue).appendingPathComponent("config.yaml")
        guard let configuration = try? String(contentsOf: configurationURL, encoding: .utf8) else {
            throw HermesServiceError.profileConfigurationUnavailable(profile)
        }

        let lines = configuration.components(separatedBy: .newlines)
        var provider: String?
        var inModelSection = false
        for line in lines {
            if line == "model:" {
                inModelSection = true
                continue
            }
            if inModelSection && !line.hasPrefix(" ") {
                break
            }
            if inModelSection, line.trimmingCharacters(in: .whitespaces).hasPrefix("provider:") {
                provider = line.components(separatedBy: ":").dropFirst().joined(separator: ":").trimmingCharacters(in: .whitespaces)
                break
            }
        }

        guard let provider, !provider.isEmpty else {
            throw HermesServiceError.profileConfigurationUnavailable(profile)
        }

        let providerHeader = "  \(provider):"
        var inProviderSection = false
        var api: String?
        var model: String?
        for line in lines {
            if line == providerHeader {
                inProviderSection = true
                continue
            }
            if inProviderSection && line.hasPrefix("  ") && !line.hasPrefix("    ") {
                break
            }
            guard inProviderSection else { continue }
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("api:") {
                api = trimmed.components(separatedBy: ":").dropFirst().joined(separator: ":").trimmingCharacters(in: .whitespaces)
            } else if trimmed.hasPrefix("default_model:") {
                model = trimmed.components(separatedBy: ":").dropFirst().joined(separator: ":").trimmingCharacters(in: .whitespaces)
            }
        }

        guard let api, let model, let baseURL = URL(string: api) else {
            throw HermesServiceError.profileConfigurationUnavailable(profile)
        }
        if profile == .hal {
            var url = baseURL.deletingLastPathComponent()
            url.appendPathComponent("api/chat")
            return ProfileBackend(apiURL: url, model: model, protocolStyle: .ollamaNativeChat)
        }
        var url = baseURL
        url.appendPathComponent("chat/completions")
        return ProfileBackend(apiURL: url, model: model, protocolStyle: .openAIChat)
    }
}
