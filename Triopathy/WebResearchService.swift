import Foundation
import Darwin

struct WebSource: Identifiable, Hashable {
    let title: String
    let url: URL
    let text: String

    var id: String { url.absoluteString }
}

struct WebResearchResult {
    let sources: [WebSource]
    let notices: [String]

    var summary: String {
        let entries = sources.enumerated().map { index, source in
            "[\(index + 1)] \(source.title)\n\(source.url.absoluteString)"
        }.joined(separator: "\n\n")
        return "Web sources retrieved at \(Date().formatted(date: .abbreviated, time: .standard))\n\n\(entries)"
    }

    var reference: String {
        sources.enumerated().map { index, source in
            "[\(index + 1)] \(source.title)\nURL: \(source.url.absoluteString)\n\(source.text)"
        }.joined(separator: "\n\n")
    }
}

private final class NoRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }
}

final class WebResearchService {
    private static let maxPageBytes = 1_000_000
    private let redirects = NoRedirects()
    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 15
        configuration.timeoutIntervalForResource = 15
        return URLSession(configuration: configuration, delegate: redirects, delegateQueue: nil)
    }()

    func gather(query: String, urls: String) async -> WebResearchResult {
        var notices: [String] = []
        var candidates: [(String, URL)] = []

        for item in urls.split(whereSeparator: { $0.isWhitespace }).prefix(3) {
            do {
                let url = try publicURL(String(item))
                candidates.append((url.absoluteString, url))
            } catch {
                notices.append(error.localizedDescription)
            }
        }

        if !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            do {
                var components = URLComponents(string: "https://html.duckduckgo.com/html/")!
                components.queryItems = [URLQueryItem(name: "q", value: String(query.prefix(500)))]
                let search = try await fetch(try publicURL(components.url!.absoluteString))
                let links = searchLinks(in: search.text)
                if links.isEmpty {
                    notices.append("Search returned no usable results. You can supply public page URLs directly.")
                }
                candidates.append(contentsOf: links.prefix(5))
            } catch is CancellationError {
                return WebResearchResult(sources: [], notices: notices)
            } catch {
                notices.append("Web search failed. You can supply public page URLs directly.")
            }
        }

        var sources: [WebSource] = []
        var seen = Set<String>()
        for (fallbackTitle, url) in candidates where sources.count < 3 {
            guard seen.insert(url.absoluteString).inserted else { continue }
            do {
                let page = try await fetch(url)
                let text = plainText(page.text)
                guard text.count >= 40 else { throw WebResearchError.unreadablePage }
                sources.append(WebSource(title: pageTitle(in: page.text) ?? fallbackTitle, url: url, text: String(text.prefix(6_000))))
            } catch is CancellationError {
                return WebResearchResult(sources: sources, notices: notices)
            } catch {
                notices.append("Could not read \(url.host ?? "that page").")
            }
        }
        return WebResearchResult(sources: sources, notices: notices)
    }

    private func fetch(_ url: URL) async throws -> (text: String, response: HTTPURLResponse) {
        try Task.checkCancellation()
        _ = try publicURL(url.absoluteString)
        var request = URLRequest(url: url)
        request.setValue("Triopathy/0.2 (+https://github.com/mgadziko/Triopathy)", forHTTPHeaderField: "User-Agent")
        request.setValue("text/html, text/plain", forHTTPHeaderField: "Accept")
        let (bytes, response) = try await session.bytes(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw WebResearchError.unreadablePage
        }
        let contentType = (http.value(forHTTPHeaderField: "Content-Type") ?? "").lowercased()
        guard contentType.isEmpty || contentType.contains("text/html") || contentType.contains("application/xhtml+xml") || contentType.contains("text/plain") else {
            throw WebResearchError.unsupportedContent
        }
        var data = Data()
        for try await byte in bytes {
            data.append(byte)
            if data.count > Self.maxPageBytes { throw WebResearchError.pageTooLarge }
        }
        guard let text = String(data: data, encoding: .utf8) else { throw WebResearchError.unreadablePage }
        return (text, http)
    }

    private func publicURL(_ text: String) throws -> URL {
        guard let url = URL(string: text), let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme),
              let host = url.host?.lowercased(), !host.isEmpty,
              url.user == nil, url.password == nil,
              (url.port == nil || url.port == 80 || url.port == 443),
              host != "localhost", !host.hasSuffix(".local") else {
            throw WebResearchError.invalidURL
        }
        try validatePublicAddresses(for: host)
        return url
    }

    private func validatePublicAddresses(for host: String) throws {
        var hints = addrinfo(ai_flags: AI_ADDRCONFIG, ai_family: AF_UNSPEC, ai_socktype: SOCK_STREAM, ai_protocol: 0, ai_addrlen: 0, ai_canonname: nil, ai_addr: nil, ai_next: nil)
        var results: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &results) == 0, let first = results else {
            throw WebResearchError.invalidURL
        }
        defer { freeaddrinfo(first) }
        var cursor: UnsafeMutablePointer<addrinfo>? = first
        var foundAddress = false
        while let entry = cursor {
            let address = entry.pointee
            if address.ai_family == AF_INET, let raw = address.ai_addr {
                foundAddress = true
                let ipv4 = raw.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr }
                guard isPublic(ipv4) else { throw WebResearchError.invalidURL }
            } else if address.ai_family == AF_INET6, let raw = address.ai_addr {
                foundAddress = true
                let ipv6 = raw.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { $0.pointee.sin6_addr }
                guard isPublic(ipv6) else { throw WebResearchError.invalidURL }
            }
            cursor = address.ai_next
        }
        guard foundAddress else { throw WebResearchError.invalidURL }
    }

    private func isPublic(_ address: in_addr) -> Bool {
        var address = address
        let bytes = withUnsafeBytes(of: &address) { Array($0) }
        guard bytes.count == 4 else { return false }
        let (a, b) = (bytes[0], bytes[1])
        return a != 0 && a != 10 && a != 127 && a < 224 &&
            !(a == 169 && b == 254) && !(a == 172 && (16...31).contains(b)) &&
            !(a == 192 && b == 168) && !(a == 100 && (64...127).contains(b)) &&
            !(a == 198 && (b == 18 || b == 19))
    }

    private func isPublic(_ address: in6_addr) -> Bool {
        var address = address
        let bytes = withUnsafeBytes(of: &address) { Array($0) }
        guard bytes.count == 16 else { return false }
        let isLoopback = bytes.dropLast().allSatisfy { $0 == 0 } && bytes.last == 1
        let isLinkLocal = bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80
        let isUniqueLocal = (bytes[0] & 0xfe) == 0xfc
        return !isLoopback && !isLinkLocal && !isUniqueLocal && (bytes[0] & 0xe0) == 0x20
    }

    private func searchLinks(in html: String) -> [(String, URL)] {
        let pattern = #"<a\b(?=[^>]*class=\"[^\"]*result__a[^\"]*\")(?=[^>]*href=\"(?<url>[^\"]+)\")[^>]*>(?<title>.*?)</a>"#
        guard let expression = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators]) else { return [] }
        let range = NSRange(html.startIndex..., in: html)
        return expression.matches(in: html, range: range).compactMap { match in
            guard let urlRange = Range(match.range(withName: "url"), in: html), let titleRange = Range(match.range(withName: "title"), in: html) else { return nil }
            var href = htmlEntityDecode(String(html[urlRange]))
            if href.hasPrefix("//") { href = "https:\(href)" }
            guard var url = URL(string: href) else { return nil }
            if url.host?.hasSuffix("duckduckgo.com") == true,
               let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
               let redirect = components.queryItems?.first(where: { $0.name == "uddg" })?.value,
               let destination = URL(string: redirect) {
                url = destination
            }
            guard let safeURL = try? publicURL(url.absoluteString) else { return nil }
            return (plainText(String(html[titleRange])), safeURL)
        }
    }

    private func pageTitle(in html: String) -> String? {
        let pattern = #"<title\b[^>]*>(.*?)</title>"#
        guard let expression = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators]) else { return nil }
        let range = NSRange(html.startIndex..., in: html)
        guard let match = expression.firstMatch(in: html, range: range), let titleRange = Range(match.range(at: 1), in: html) else { return nil }
        let title = plainText(String(html[titleRange]))
        return title.isEmpty ? nil : title
    }

    private func plainText(_ html: String) -> String {
        var text = html
        for pattern in [#"<(script|style|noscript|svg)\b[^>]*>.*?</\1\s*>"#, #"<!--.*?-->"#, #"<[^>]+>"#] {
            text = (try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators]))?.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: " ") ?? text
        }
        return htmlEntityDecode(text).replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func htmlEntityDecode(_ text: String) -> String {
        NSAttributedString(html: Data(text.utf8), options: [.documentType: NSAttributedString.DocumentType.html], documentAttributes: nil)?.string ?? text
    }
}

private enum WebResearchError: LocalizedError {
    case invalidURL
    case unsupportedContent
    case pageTooLarge
    case unreadablePage

    var errorDescription: String? {
        switch self {
        case .invalidURL: return "Use a public HTTP or HTTPS page without login credentials or a custom port."
        case .unsupportedContent: return "Only HTML and plain-text pages can be read."
        case .pageTooLarge: return "Page exceeds the 1 MB reading limit."
        case .unreadablePage: return "No readable page text was available."
        }
    }
}
