using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Triopathy.Core;

public sealed record WebSource(string Title, string Url, string Text);
public sealed record WebResearchResult(IReadOnlyList<WebSource> Sources, IReadOnlyList<string> Notices)
{
    public string Summary => "Web sources retrieved at " + DateTimeOffset.UtcNow.ToString("u") + "\n\n" +
        string.Join("\n\n", Sources.Select((s, i) => $"[{i + 1}] {s.Title}\n{s.Url}"));
    public string Reference => "Web research retrieved at " + DateTimeOffset.UtcNow.ToString("u") + "\n" +
        string.Join("\n\n", Sources.Select((s, i) => $"[{i + 1}] {s.Title}\nURL: {s.Url}\n{s.Text}"));
}
public interface IWebResearch
{
    Task<WebResearchResult> GatherAsync(string query, string urls, CancellationToken token);
}

public sealed class WebResearch(HttpClient http) : IWebResearch
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static string Replace(string input, string pattern, string replacement) => Regex.Replace(input, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
    public static string PlainText(string html)
    {
        html = Replace(html, @"<(script|style|noscript|svg)\b[^>]*>.*?</\1\s*>", " ");
        html = Replace(html, @"<!--.*?-->", " ");
        html = Replace(html, @"<[^>]+>", " ");
        return Replace(WebUtility.HtmlDecode(html), @"\s+", " ").Trim();
    }
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
                !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
                !(b[0] == 198 && (b[1] == 18 || b[1] == 19));
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20;
    }
    public static Uri PublicUri(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && !IsPublicAddress(address)))
            throw new InvalidDataException("Use a public HTTP or HTTPS page without login credentials or a custom port.");
        return uri;
    }
    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, token) => {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a))) throw new HttpRequestException("This page resolves to a private or local address.");
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                    catch (OperationCanceledException) { socket.Dispose(); throw; }
                    catch (Exception ex) { socket.Dispose(); last = ex; }
                }
                throw new HttpRequestException("Could not connect to this page.", last);
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
    private async Task<(Uri Uri, string Body)> ReadAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 4; redirects++)
        {
            uri = PublicUri(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Triopathy/0.2 (+https://github.com/mgadziko/Triopathy)");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); continue; }
            response.EnsureSuccessStatusCode();
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type is not ("text/html" or "application/xhtml+xml" or "text/plain")) throw new InvalidDataException("Only HTML and plain-text pages can be read.");
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token); bounded.CancelAfter(TimeSpan.FromSeconds(15));
            await using var source = await response.Content.ReadAsStreamAsync(bounded.Token);
            using var buffer = new MemoryStream(); var chunk = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(chunk, bounded.Token)) > 0)
            {
                if (buffer.Length + count > 1_000_000) throw new InvalidDataException("Page exceeds the 1 MB reading limit.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            using var reader = new StreamReader(buffer);
            return (uri, await reader.ReadToEndAsync(bounded.Token));
        }
        throw new HttpRequestException("Too many page redirects.");
    }
    public static IReadOnlyList<(string Title, Uri Url)> SearchLinks(string html)
    {
        var links = new List<(string, Uri)>();
        foreach (Match match in Regex.Matches(html, "<a\\b(?=[^>]*class=\"[^\"]*result__a[^\"]*\")(?=[^>]*href=\"(?<url>[^\"]+)\")[^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout))
        {
            var href = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (href.StartsWith("//")) href = "https:" + href;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            {
                var redirect = uri.Query.TrimStart('?').Split('&').FirstOrDefault(p => p.StartsWith("uddg="));
                if (redirect == null || !Uri.TryCreate(WebUtility.UrlDecode(redirect[5..]), UriKind.Absolute, out uri)) continue;
            }
            try { links.Add((PlainText(match.Groups["title"].Value), PublicUri(uri.AbsoluteUri))); } catch (InvalidDataException) { }
        }
        return links;
    }
    public async Task<WebResearchResult> GatherAsync(string query, string urls, CancellationToken token)
    {
        var notices = new List<string>(); var candidates = new List<(string Title, Uri Url)>();
        foreach (var text in urls.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Take(3))
        {
            try { candidates.Add((text, PublicUri(text))); }
            catch (InvalidDataException ex) { notices.Add(ex.Message); }
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            try
            {
                var search = await ReadAsync(new Uri("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query[..Math.Min(500, query.Length)])), token);
                var links = SearchLinks(search.Body);
                if (links.Count == 0) notices.Add("Search returned no usable results or was blocked. You can supply page URLs directly.");
                candidates.AddRange(links.Take(5));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { notices.Add("Web search failed. You can supply page URLs directly."); }
        }
        var sources = new List<WebSource>();
        foreach (var item in candidates.DistinctBy(p => p.Url.AbsoluteUri))
        {
            if (sources.Count == 3) break;
            token.ThrowIfCancellationRequested();
            try
            {
                var page = await ReadAsync(item.Url, token);
                var title = Regex.Match(page.Body, @"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
                var text = PlainText(page.Body);
                if (text.Length < 40) throw new InvalidDataException("No readable page text.");
                sources.Add(new(title.Success ? PlainText(title.Groups[1].Value) : item.Title, page.Uri.AbsoluteUri, text[..Math.Min(6000, text.Length)]));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { notices.Add("Could not read " + item.Url.Host + "."); }
        }
        return new(sources, notices);
    }
}
