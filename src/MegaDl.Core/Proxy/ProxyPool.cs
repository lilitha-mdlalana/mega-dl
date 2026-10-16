using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace MegaDl.Core.Proxy;

/// <summary>
/// The proxy list, parsed, with ban/expiry and random pick. One instance is shared by
/// every download in the app (see <see cref="MegaDl.Core.Transfers.TransferManager"/>),
/// since a proxy banned by one download's chunk failure should stay banned for everyone.
///
/// List format (one entry per line), same as MegaBasterd's so existing lists paste straight in:
///
///   IP:PORT                      HTTP proxy
///   *IP:PORT or socks5://IP:PORT SOCKS5 proxy
///   IP:PORT@b64user:b64pass      either of the above, with base64-encoded credentials
///   #https://example.com/list    fetch more proxies from this URL (<see cref="RefreshAsync"/>)
///
/// Java port note: SmartMegaProxyManager's list parsing and ban bookkeeping.
/// </summary>
public sealed class ProxyPool(HttpClient? refreshHttp = null, Func<ProxyEntry, TimeSpan, HttpClient>? clientFactory = null)
{
    private readonly HttpClient _refreshHttp = refreshHttp ?? new HttpClient();
    private readonly Func<ProxyEntry, TimeSpan, HttpClient> _clientFactory = clientFactory ?? DefaultClientFactory;
    private readonly List<ProxyEntry> _proxies = [];
    private readonly Dictionary<string, DateTime> _bannedUntil = [];
    private readonly List<string> _refreshUrls = [];
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    /// <summary>Replaces the whole list. Existing bans are kept (a reload shouldn't un-ban anything).</summary>
    public void LoadList(string? text)
    {
        lock (_lock)
        {
            _proxies.Clear();
            _refreshUrls.Clear();
            foreach (ProxyEntry entry in ParseLines(text, _refreshUrls))
                if (!_proxies.Any(p => p.Key == entry.Key))
                    _proxies.Add(entry);
        }
    }

    /// <summary>Fetches every "#url" line's list and merges in whatever new proxies it finds.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        List<string> urls;
        lock (_lock) urls = [.. _refreshUrls];

        foreach (string url in urls)
        {
            string text;
            try { text = await _refreshHttp.GetStringAsync(url, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { continue; } // best-effort

            lock (_lock)
            {
                foreach (ProxyEntry entry in ParseLines(text, refreshUrls: null))
                    if (!_proxies.Any(p => p.Key == entry.Key))
                        _proxies.Add(entry);
            }
        }
    }

    /// <summary>A random proxy that isn't banned and isn't <paramref name="excludeKey"/>, or null if none qualify.</summary>
    public ProxyEntry? Pick(string? excludeKey = null)
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            var available = _proxies.Where(p =>
                p.Key != excludeKey && (!_bannedUntil.TryGetValue(p.Key, out DateTime until) || until <= now)).ToList();
            return available.Count == 0 ? null : available[Random.Shared.Next(available.Count)];
        }
    }

    /// <summary><paramref name="duration"/> of <see cref="TimeSpan.Zero"/> (or less) removes it permanently.</summary>
    public void Ban(ProxyEntry proxy, TimeSpan duration)
    {
        lock (_lock)
        {
            if (duration <= TimeSpan.Zero)
            {
                _proxies.RemoveAll(p => p.Key == proxy.Key);
                _bannedUntil.Remove(proxy.Key);
            }
            else
            {
                _bannedUntil[proxy.Key] = DateTime.UtcNow + duration;
            }
        }
    }

    public int AvailableCount
    {
        get
        {
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                return _proxies.Count(p => !_bannedUntil.TryGetValue(p.Key, out DateTime until) || until <= now);
            }
        }
    }

    /// <summary>A cached <see cref="HttpClient"/> routed through <paramref name="proxy"/> — one per proxy, reused across chunks/downloads.</summary>
    public HttpClient GetClient(ProxyEntry proxy, TimeSpan timeout) =>
        _clients.GetOrAdd(proxy.Key, _ => _clientFactory(proxy, timeout));

    private static HttpClient DefaultClientFactory(ProxyEntry proxy, TimeSpan timeout)
    {
        string scheme = proxy.Kind == ProxyKind.Socks5 ? "socks5" : "http";
        var webProxy = new WebProxy(new Uri($"{scheme}://{proxy.Host}:{proxy.Port}"));
        if (proxy.Username is not null)
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

        var handler = new SocketsHttpHandler { Proxy = webProxy, UseProxy = true };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static IEnumerable<ProxyEntry> ParseLines(string? text, List<string>? refreshUrls)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                string url = line[1..].Trim();
                if (url.Length > 0) refreshUrls?.Add(url);
                continue;
            }

            if (TryParseLine(line, out ProxyEntry entry)) yield return entry;
        }
    }

    /// <summary>Parses one non-comment line. Exposed for testing each format directly.</summary>
    public static bool TryParseLine(string line, out ProxyEntry entry)
    {
        entry = null!;
        string body = line;
        string? username = null, password = null;

        int at = body.LastIndexOf('@');
        if (at >= 0)
        {
            string[] creds = body[(at + 1)..].Split(':');
            if (creds.Length != 2) return false;
            username = DecodeBase64(creds[0]);
            password = DecodeBase64(creds[1]);
            body = body[..at];
        }

        ProxyKind kind = ProxyKind.Http;
        if (body.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
        {
            kind = ProxyKind.Socks5;
            body = body["socks5://".Length..];
        }
        else if (body.StartsWith('*'))
        {
            kind = ProxyKind.Socks5;
            body = body[1..];
        }

        int colon = body.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(body[(colon + 1)..], out int port)) return false;
        string host = body[..colon];
        if (host.Length == 0) return false;

        entry = new ProxyEntry(host, port, kind, username, password);
        return true;
    }

    private static string DecodeBase64(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return value; } // not actually base64 — use it verbatim rather than fail the whole line
    }
}
