import AppKit
import CryptoKit
import Foundation
import Network
import Security

enum ChatGPTPlanError: LocalizedError {
    case notConnected
    case planUsageNotGranted
    case invalidCallback
    case tokenValidationFailed
    case unsupportedToken
    case requestFailed(String)
    case keychain(OSStatus)

    var errorDescription: String? {
        switch self {
        case .notConnected:
            return "Codex is not connected to a ChatGPT plan. Choose Continue with ChatGPT first."
        case .planUsageNotGranted:
            return "ChatGPT sign-in completed, but ChatGPT plan usage was not enabled for Triopathy."
        case .invalidCallback:
            return "The ChatGPT sign-in callback could not be verified. Please try again."
        case .tokenValidationFailed:
            return "Triopathy could not validate the ChatGPT sign-in token."
        case .unsupportedToken:
            return "ChatGPT returned an unsupported sign-in token."
        case let .requestFailed(detail):
            return detail
        case let .keychain(status):
            return "The macOS Keychain could not be updated (status \(status))."
        }
    }
}

/// A local, public-client implementation of the documented Sign in with ChatGPT flow.
/// Credentials remain in this Mac user's Keychain; neither an OpenAI API key nor an app
/// client secret is used for ChatGPT-plan requests.
struct ChatGPTPlanService {
    struct Model: Codable, Hashable, Identifiable {
        let slug: String
        let displayName: String

        var id: String { slug }

        enum CodingKeys: String, CodingKey {
            case slug
            case displayName = "display_name"
        }
    }

    private struct Profile: Codable {
        var email: String
        var subject: String
        var clientID: String
        var hostID: String
        var idToken: String
        var accessToken: String
        var refreshToken: String
        var expiresAt: Date
        var scopes: [String]
        var selectedModel: String
    }

    private struct TokenResponse: Decodable {
        let accessToken: String
        let refreshToken: String
        let idToken: String?
        let tokenType: String
        let expiresIn: Int
        let scope: String?

        enum CodingKeys: String, CodingKey {
            case accessToken = "access_token"
            case refreshToken = "refresh_token"
            case idToken = "id_token"
            case tokenType = "token_type"
            case expiresIn = "expires_in"
            case scope
        }
    }

    private struct OpenIDConfiguration: Decodable {
        let issuer: String
        let jwksURI: URL

        enum CodingKeys: String, CodingKey {
            case issuer
            case jwksURI = "jwks_uri"
        }
    }

    private struct JWKS: Decodable {
        let keys: [JWK]
    }

    private struct JWK: Decodable {
        let kid: String
        let kty: String
        let n: String
        let e: String
    }

    private struct JWTHeader: Decodable {
        let alg: String
        let kid: String
    }

    private struct JWTClaims: Decodable {
        let iss: String
        let aud: Audience
        let exp: TimeInterval
        let nonce: String
        let sub: String
        let email: String?
    }

    private enum Audience: Decodable {
        case one(String)
        case many([String])

        init(from decoder: Decoder) throws {
            let container = try decoder.singleValueContainer()
            if let value = try? container.decode(String.self) { self = .one(value) }
            else { self = .many(try container.decode([String].self)) }
        }

        func contains(_ clientID: String) -> Bool {
            switch self {
            case let .one(value): return value == clientID
            case let .many(values): return values.contains(clientID)
            }
        }
    }

    private struct StreamEvent: Decodable {
        struct EventResponse: Decodable {
            struct EventError: Decodable {
                let code: String?
                let message: String?
            }
            let error: EventError?
        }

        let type: String
        let delta: String?
        let response: EventResponse?
    }

    private struct ModelList: Decodable {
        struct Item: Decodable {
            let slug: String
            let displayName: String
            let visibility: String?

            enum CodingKeys: String, CodingKey {
                case slug
                case displayName = "display_name"
                case visibility
            }
        }
        let models: [Item]
    }

    private static let service = "com.ricercar.Triopathy"
    private static let profileAccount = "chatgpt-plan-profile"
    private static let hostIDKey = "ChatGPTPlanHostID"
    private static let authURL = URL(string: "https://auth.openai.com/api/accounts/authorize")!
    private static let tokenURL = URL(string: "https://auth.openai.com/api/accounts/oauth/token")!
    private static let configurationURL = URL(string: "https://auth.openai.com/.well-known/openid-configuration")!
    private static let requiredScope = "chatgpt.tokens.use.direct"

    static var isConnected: Bool {
        guard let profile = try? loadProfile() else { return false }
        return profile.scopes.contains(requiredScope)
    }

    static var accountLabel: String? {
        guard let profile = try? loadProfile(), profile.scopes.contains(requiredScope) else { return nil }
        return profile.email
    }

    static var selectedModel: String? {
        try? loadProfile().selectedModel
    }

    /// Opens the user's browser, waits for the loopback callback, validates the OIDC token,
    /// and stores the resulting ChatGPT-plan connection in Keychain.
    static func connect() async throws -> [Model] {
        let existing = try? loadProfile()
        let hostID = stableHostID()
        let callback = OAuthCallbackServer()
        let callbackURL = try await callback.start()
        let attempt = AuthorizationAttempt(
            clientID: existing?.clientID ?? "dynamic_agent_client",
            hostID: hostID,
            callbackURL: callbackURL,
            existingIDToken: existing?.idToken,
            loginHint: existing?.email
        )

        var components = URLComponents(url: authURL, resolvingAgainstBaseURL: false)!
        var query = [
            URLQueryItem(name: "client_id", value: attempt.clientID),
            URLQueryItem(name: "ext_agent_host_id", value: hostID),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "redirect_uri", value: callbackURL.absoluteString),
            URLQueryItem(name: "scope", value: "openid profile email offline_access resource.invoke \(requiredScope)"),
            URLQueryItem(name: "resource", value: "https://api.openai.com/v1"),
            URLQueryItem(name: "state", value: attempt.state),
            URLQueryItem(name: "nonce", value: attempt.nonce),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "code_challenge", value: attempt.codeChallenge),
        ]
        if existing == nil {
            query.append(URLQueryItem(name: "agent_name_hint", value: "Triopathy"))
        } else {
            query.append(URLQueryItem(name: "id_token_hint", value: existing?.idToken))
            query.append(URLQueryItem(name: "login_hint", value: existing?.email))
        }
        components.queryItems = query
        guard let authorizationURL = components.url else { throw ChatGPTPlanError.invalidCallback }
        NSWorkspace.shared.open(authorizationURL)

        let returnURL = try await callback.waitForCallback()
        let result = try callbackValues(from: returnURL)
        guard result.state == attempt.state, let code = result.code else { throw ChatGPTPlanError.invalidCallback }
        if let oauthError = result.error {
            throw ChatGPTPlanError.requestFailed("ChatGPT sign-in was not completed: \(oauthError).")
        }

        let issuedClientID: String
        if existing == nil {
            guard let returnedID = result.clientID, returnedID != "dynamic_agent_client" else {
                throw ChatGPTPlanError.invalidCallback
            }
            issuedClientID = returnedID
        } else {
            guard result.clientID == nil || result.clientID == existing?.clientID else {
                throw ChatGPTPlanError.invalidCallback
            }
            issuedClientID = existing!.clientID
        }

        let tokens = try await exchange(code: code, clientID: issuedClientID, verifier: attempt.verifier, callbackURL: callbackURL)
        guard let idToken = tokens.idToken else { throw ChatGPTPlanError.tokenValidationFailed }
        let claims = try await validate(idToken: idToken, expectedClientID: issuedClientID, expectedNonce: attempt.nonce)
        let scopes = (tokens.scope ?? "").split(separator: " ").map(String.init)
        guard scopes.contains(requiredScope) else { throw ChatGPTPlanError.planUsageNotGranted }
        if let existing, existing.subject != claims.sub { throw ChatGPTPlanError.tokenValidationFailed }

        var profile = Profile(
            email: claims.email ?? "Connected ChatGPT account",
            subject: claims.sub,
            clientID: issuedClientID,
            hostID: hostID,
            idToken: idToken,
            accessToken: tokens.accessToken,
            refreshToken: tokens.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokens.expiresIn)),
            scopes: scopes,
            selectedModel: ""
        )
        try saveProfile(profile)
        let models = try await listModels(using: profile)
        guard let first = models.first else {
            throw ChatGPTPlanError.requestFailed("ChatGPT did not offer an available model for this connection.")
        }
        profile.selectedModel = first.slug
        try saveProfile(profile)
        return models
    }

    static func availableModels() async throws -> [Model] {
        try await listModels(using: freshProfile())
    }

    static func selectModel(_ model: String) throws {
        var profile = try loadProfile()
        profile.selectedModel = model
        try saveProfile(profile)
    }

    static func disconnect() throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: profileAccount,
        ]
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw ChatGPTPlanError.keychain(status) }
    }

    func respond(prompt: String) async throws -> String {
        let profile = try await Self.freshProfile()
        guard !profile.selectedModel.isEmpty else { throw ChatGPTPlanError.notConnected }
        let payload = PlanResponseRequest(
            model: profile.selectedModel,
            instructions: "You are Codex, one thoughtful participant in a multi-host conversation. Respond only to the conversation prompt. Do not claim access to tools, files, host machines, accounts, or this app. Be concise: one to three paragraphs. Build on a distinct point or ask a useful question.",
            input: [.init(role: "user", content: prompt)],
            store: false,
            stream: true
        )
        var request = URLRequest(url: URL(string: "https://api.openai.com/v1/responses")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 180
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(profile.accessToken)", forHTTPHeaderField: "Authorization")
        request.httpBody = try JSONEncoder().encode(payload)

        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        guard let http = response as? HTTPURLResponse else { throw ChatGPTPlanError.requestFailed("ChatGPT returned an invalid response.") }
        guard (200...299).contains(http.statusCode) else {
            var body = ""
            for try await line in bytes.lines {
                body += line
                if body.utf8.count >= 16_384 { break }
            }
            throw ChatGPTPlanError.requestFailed(planRequestError(status: http.statusCode, body: body, requestID: http.value(forHTTPHeaderField: "x-request-id")))
        }

        var text = ""
        var completed = false
        for try await line in bytes.lines {
            guard line.hasPrefix("data: ") else { continue }
            let payload = String(line.dropFirst(6))
            guard payload != "[DONE]", let data = payload.data(using: .utf8), let event = try? JSONDecoder().decode(StreamEvent.self, from: data) else { continue }
            switch event.type {
            case "response.output_text.delta":
                text += event.delta ?? ""
            case "response.failed":
                let detail = event.response?.error?.code ?? event.response?.error?.message ?? "unknown error"
                throw ChatGPTPlanError.requestFailed("ChatGPT plan request failed: \(detail).")
            case "response.completed":
                completed = true
            default:
                break
            }
        }
        guard completed else { throw ChatGPTPlanError.requestFailed("ChatGPT ended the response before completion.") }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw ChatGPTPlanError.requestFailed("ChatGPT returned a completed response without text.") }
        return trimmed
    }

    private func planRequestError(status: Int, body: String, requestID: String?) -> String {
        let detail = Self.responseErrorDetail(from: body)
        let requestReference = requestID.map { " Reference: \($0)." } ?? ""
        return "ChatGPT plan request failed (HTTP \(status)): \(detail)\(requestReference)"
    }

    private static func responseErrorDetail(from body: String) -> String {
        struct ErrorEnvelope: Decodable {
            struct APIError: Decodable {
                let message: String?
                let code: String?
                let param: String?
            }
            let error: APIError?
            let detail: String?
        }

        guard let data = body.data(using: .utf8), let error = try? JSONDecoder().decode(ErrorEnvelope.self, from: data) else {
            return body.isEmpty ? "No diagnostic detail was returned." : body
        }
        if let apiError = error.error {
            let parts = [apiError.code, apiError.param, apiError.message].compactMap { $0 }.filter { !$0.isEmpty }
            if !parts.isEmpty { return parts.joined(separator: " — ") }
        }
        return error.detail ?? "No diagnostic detail was returned."
    }

    private static func freshProfile() async throws -> Profile {
        let profile = try loadProfile()
        guard profile.scopes.contains(requiredScope) else { throw ChatGPTPlanError.planUsageNotGranted }
        if profile.expiresAt > Date().addingTimeInterval(90) { return profile }

        let tokens = try await refresh(profile)
        var refreshed = profile
        refreshed.accessToken = tokens.accessToken
        refreshed.refreshToken = tokens.refreshToken
        refreshed.expiresAt = Date().addingTimeInterval(TimeInterval(tokens.expiresIn))
        if let scope = tokens.scope { refreshed.scopes = scope.split(separator: " ").map(String.init) }
        guard refreshed.scopes.contains(requiredScope) else { throw ChatGPTPlanError.planUsageNotGranted }
        try saveProfile(refreshed)
        return refreshed
    }

    private static func listModels(using profile: Profile) async throws -> [Model] {
        var request = URLRequest(url: URL(string: "https://api.openai.com/v1/models")!)
        request.setValue("Bearer \(profile.accessToken)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw ChatGPTPlanError.requestFailed("Triopathy could not retrieve the available ChatGPT plan models.")
        }
        let catalog = try JSONDecoder().decode(ModelList.self, from: data)
        return catalog.models
            .filter { $0.visibility == "list" }
            .map { Model(slug: $0.slug, displayName: $0.displayName) }
    }

    private static func exchange(code: String, clientID: String, verifier: String, callbackURL: URL) async throws -> TokenResponse {
        try await tokenRequest([
            "grant_type": "authorization_code",
            "client_id": clientID,
            "code": code,
            "code_verifier": verifier,
            "redirect_uri": callbackURL.absoluteString,
            "resource": "https://api.openai.com/v1",
        ])
    }

    private static func refresh(_ profile: Profile) async throws -> TokenResponse {
        try await tokenRequest([
            "grant_type": "refresh_token",
            "client_id": profile.clientID,
            "refresh_token": profile.refreshToken,
            "resource": "https://api.openai.com/v1",
        ])
    }

    private static func tokenRequest(_ values: [String: String]) async throws -> TokenResponse {
        var request = URLRequest(url: tokenURL)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = values
            .map { "\($0.key.urlQueryEscaped)=\($0.value.urlQueryEscaped)" }
            .sorted()
            .joined(separator: "&")
            .data(using: .utf8)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let detail = String(data: data, encoding: .utf8) ?? "unknown error"
            throw ChatGPTPlanError.requestFailed("ChatGPT sign-in token exchange failed: \(detail)")
        }
        return try JSONDecoder().decode(TokenResponse.self, from: data)
    }

    private static func validate(idToken: String, expectedClientID: String, expectedNonce: String) async throws -> JWTClaims {
        let parts = idToken.split(separator: ".").map(String.init)
        guard parts.count == 3,
              let headerData = Data(base64URLEncoded: parts[0]),
              let claimsData = Data(base64URLEncoded: parts[1]),
              let signature = Data(base64URLEncoded: parts[2]) else { throw ChatGPTPlanError.unsupportedToken }
        let header = try JSONDecoder().decode(JWTHeader.self, from: headerData)
        guard header.alg == "RS256" else { throw ChatGPTPlanError.unsupportedToken }
        let configuration = try await fetch(OpenIDConfiguration.self, from: configurationURL)
        let jwks = try await fetch(JWKS.self, from: configuration.jwksURI)
        guard let jwk = jwks.keys.first(where: { $0.kid == header.kid && $0.kty == "RSA" }),
              let modulus = Data(base64URLEncoded: jwk.n),
              let exponent = Data(base64URLEncoded: jwk.e),
              let signingInput = "\(parts[0]).\(parts[1])".data(using: .utf8) else { throw ChatGPTPlanError.tokenValidationFailed }
        var error: Unmanaged<CFError>?
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
            kSecAttrKeyClass as String: kSecAttrKeyClassPublic,
            kSecAttrKeySizeInBits as String: modulus.count * 8,
        ]
        guard let key = SecKeyCreateWithData(rsaPublicKey(modulus: modulus, exponent: exponent) as CFData, attributes as CFDictionary, &error),
              SecKeyVerifySignature(key, .rsaSignatureMessagePKCS1v15SHA256, signingInput as CFData, signature as CFData, &error) else {
            throw ChatGPTPlanError.tokenValidationFailed
        }
        let claims = try JSONDecoder().decode(JWTClaims.self, from: claimsData)
        guard claims.iss == configuration.issuer,
              claims.aud.contains(expectedClientID),
              claims.nonce == expectedNonce,
              Date(timeIntervalSince1970: claims.exp) > Date() else { throw ChatGPTPlanError.tokenValidationFailed }
        return claims
    }

    private static func fetch<T: Decodable>(_ type: T.Type, from url: URL) async throws -> T {
        let (data, response) = try await URLSession.shared.data(from: url)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw ChatGPTPlanError.requestFailed("Triopathy could not validate the ChatGPT sign-in response.")
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private static func stableHostID() -> String {
        if let value = UserDefaults.standard.string(forKey: hostIDKey), value.hasPrefix("urn:uuid:") { return value }
        let value = "urn:uuid:\(UUID().uuidString.lowercased())"
        UserDefaults.standard.set(value, forKey: hostIDKey)
        return value
    }

    private static func loadProfile() throws -> Profile {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: profileAccount,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else { throw ChatGPTPlanError.notConnected }
        return try JSONDecoder().decode(Profile.self, from: data)
    }

    private static func saveProfile(_ profile: Profile) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: profileAccount,
        ]
        let data = try JSONEncoder().encode(profile)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            item[kSecValueData as String] = data
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            let addStatus = SecItemAdd(item as CFDictionary, nil)
            guard addStatus == errSecSuccess else { throw ChatGPTPlanError.keychain(addStatus) }
        } else if status != errSecSuccess {
            throw ChatGPTPlanError.keychain(status)
        }
    }

    private static func rsaPublicKey(modulus: Data, exponent: Data) -> Data {
        let body = asn1Integer(modulus) + asn1Integer(exponent)
        return Data([0x30]) + asn1Length(body.count) + body
    }

    private static func asn1Integer(_ data: Data) -> Data {
        var value = data.drop { $0 == 0 }
        if value.isEmpty { value = Data([0]) }
        var result = Data(value)
        if result.first! & 0x80 != 0 { result.insert(0, at: 0) }
        return Data([0x02]) + asn1Length(result.count) + result
    }

    private static func asn1Length(_ length: Int) -> Data {
        if length < 128 { return Data([UInt8(length)]) }
        var value = length
        var bytes: [UInt8] = []
        while value > 0 { bytes.insert(UInt8(value & 0xff), at: 0); value >>= 8 }
        return Data([0x80 | UInt8(bytes.count)]) + Data(bytes)
    }
}

private struct AuthorizationAttempt {
    let clientID: String
    let hostID: String
    let callbackURL: URL
    let verifier: String
    let state: String
    let nonce: String
    let codeChallenge: String

    init(clientID: String, hostID: String, callbackURL: URL, existingIDToken: String?, loginHint: String?) {
        self.clientID = clientID
        self.hostID = hostID
        self.callbackURL = callbackURL
        verifier = randomURLSafeString(byteCount: 48)
        state = randomURLSafeString(byteCount: 32)
        nonce = randomURLSafeString(byteCount: 32)
        codeChallenge = Data(SHA256.hash(data: Data(verifier.utf8))).base64URLEncodedString()
    }
}

private struct PlanResponseRequest: Encodable {
    struct InputItem: Encodable {
        let role: String
        let content: String
    }

    let model: String
    let instructions: String
    let input: [InputItem]
    let store: Bool
    let stream: Bool
}

private final class OAuthCallbackServer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "com.ricercar.Triopathy.oauth-callback")
    private let lock = NSLock()
    private var listener: NWListener?
    private var callbackContinuation: CheckedContinuation<URL, Error>?
    private var receivedURL: URL?

    func start() async throws -> URL {
        let listener = try NWListener(using: .tcp, on: .any)
        self.listener = listener
        listener.newConnectionHandler = { [weak self] connection in self?.receive(connection) }
        return try await withCheckedThrowingContinuation { continuation in
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard let port = listener.port, let url = URL(string: "http://127.0.0.1:\(port.rawValue)/auth/callback") else {
                        continuation.resume(throwing: ChatGPTPlanError.invalidCallback)
                        return
                    }
                    continuation.resume(returning: url)
                case let .failed(error):
                    continuation.resume(throwing: error)
                default:
                    break
                }
            }
            listener.start(queue: queue)
        }
    }

    func waitForCallback() async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            if let receivedURL {
                self.receivedURL = nil
                lock.unlock()
                continuation.resume(returning: receivedURL)
            } else {
                callbackContinuation = continuation
                lock.unlock()
            }
        }
    }

    private func receive(_ connection: NWConnection) {
        connection.start(queue: queue)
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, _, _ in
            defer {
                let page = "<html><body><h2>Triopathy connected</h2><p>You may return to the app.</p></body></html>"
                let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(page.utf8.count)\r\nConnection: close\r\n\r\n\(page)"
                connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in connection.cancel() })
            }
            guard let data, let request = String(data: data, encoding: .utf8), let firstLine = request.split(separator: "\n").first else { return }
            let components = firstLine.split(separator: " ")
            guard components.count >= 2, let url = URL(string: "http://127.0.0.1\(components[1])") else { return }
            guard let self else { return }
            self.lock.lock()
            if let continuation = self.callbackContinuation {
                self.callbackContinuation = nil
                self.lock.unlock()
                continuation.resume(returning: url)
            } else {
                self.receivedURL = url
                self.lock.unlock()
            }
            self.listener?.cancel()
        }
    }
}

private func callbackValues(from url: URL) throws -> (code: String?, state: String?, clientID: String?, error: String?) {
    let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
    func value(_ name: String) -> String? { items.first(where: { $0.name == name })?.value }
    return (value("code"), value("state"), value("client_id"), value("error"))
}

private func randomURLSafeString(byteCount: Int) -> String {
    var bytes = [UInt8](repeating: 0, count: byteCount)
    _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
    return Data(bytes).base64URLEncodedString()
}

private extension Data {
    init?(base64URLEncoded string: String) {
        var value = string.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        value += String(repeating: "=", count: (4 - value.count % 4) % 4)
        self.init(base64Encoded: value)
    }

    func base64URLEncodedString() -> String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
}

private extension String {
    var urlQueryEscaped: String {
        addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? self
    }
}
