import Foundation
import Security

enum OpenAIServiceError: LocalizedError {
    case notConfigured
    case keychain(OSStatus)
    case invalidResponse
    case requestFailed(String)

    var errorDescription: String? {
        switch self {
        case .notConfigured:
            return "Codex is not connected. Choose Configure Codex and add an OpenAI API key."
        case let .keychain(status):
            return "The macOS Keychain could not be updated (status \(status))."
        case .invalidResponse:
            return "OpenAI returned a response without usable text."
        case let .requestFailed(detail):
            return detail
        }
    }
}

struct OpenAIService {
    private static let service = "com.ricercar.Triopathy"
    private static let account = "openai-api-key"
    private static let modelKey = "OpenAIModel"
    static let defaultModel = "gpt-5.4"

    static var hasAPIKey: Bool {
        (try? apiKey()) != nil
    }

    static var configuredModel: String {
        let model = UserDefaults.standard.string(forKey: modelKey)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return model.isEmpty ? defaultModel : model
    }

    static func save(apiKey: String, model: String) throws {
        let trimmedKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedKey.isEmpty else { throw OpenAIServiceError.notConfigured }
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        let data = Data(trimmedKey.utf8)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            item[kSecValueData as String] = data
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            let addStatus = SecItemAdd(item as CFDictionary, nil)
            guard addStatus == errSecSuccess else { throw OpenAIServiceError.keychain(addStatus) }
        } else if status != errSecSuccess {
            throw OpenAIServiceError.keychain(status)
        }
        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        UserDefaults.standard.set(trimmedModel.isEmpty ? defaultModel : trimmedModel, forKey: modelKey)
    }

    static func removeAPIKey() throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw OpenAIServiceError.keychain(status) }
    }

    func respond(prompt: String) async throws -> String {
        let key = try Self.apiKey()
        let payload = ResponseRequest(
            model: Self.configuredModel,
            instructions: "You are Codex, one thoughtful participant in a multi-host conversation. Respond only to the conversation prompt. Do not claim access to tools, files, host machines, accounts, or this app. Be concise: one to three paragraphs. Build on a distinct point or ask a useful question.",
            input: prompt,
            maxOutputTokens: 500,
            store: false
        )
        var request = URLRequest(url: URL(string: "https://api.openai.com/v1/responses")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 120
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        request.httpBody = try JSONEncoder().encode(payload)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw OpenAIServiceError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            let detail = String(data: data, encoding: .utf8) ?? "OpenAI HTTP \(http.statusCode)"
            throw OpenAIServiceError.requestFailed(detail)
        }
        let decoded = try JSONDecoder().decode(Response.self, from: data)
        let text = decoded.output
            .compactMap(\.content)
            .flatMap { $0 }
            .compactMap(\.text)
            .joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { throw OpenAIServiceError.invalidResponse }
        return text
    }

    private static func apiKey() throws -> String {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data, let key = String(data: data, encoding: .utf8), !key.isEmpty else {
            throw OpenAIServiceError.notConfigured
        }
        return key
    }
}

private struct ResponseRequest: Encodable {
    let model: String
    let instructions: String
    let input: String
    let maxOutputTokens: Int
    let store: Bool

    enum CodingKeys: String, CodingKey {
        case model, instructions, input, store
        case maxOutputTokens = "max_output_tokens"
    }
}

private struct Response: Decodable {
    struct Output: Decodable {
        struct Content: Decodable {
            let type: String
            let text: String?
        }
        let content: [Content]?
    }
    let output: [Output]
}
