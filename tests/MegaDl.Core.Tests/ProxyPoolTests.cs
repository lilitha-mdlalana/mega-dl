using System.Net;
using MegaDl.Core.Proxy;
using Xunit;

namespace MegaDl.Core.Tests;

public class ProxyPoolTests
{
    [Theory]
    [InlineData("1.2.3.4:8080", "1.2.3.4", 8080, ProxyKind.Http, null, null)]
    [InlineData("*1.2.3.4:1080", "1.2.3.4", 1080, ProxyKind.Socks5, null, null)]
    [InlineData("socks5://1.2.3.4:1080", "1.2.3.4", 1080, ProxyKind.Socks5, null, null)]
    public void Parses_every_supported_line_format(string line, string host, int port, ProxyKind kind, string? user, string? pass)
    {
        Assert.True(ProxyPool.TryParseLine(line, out ProxyEntry entry));
        Assert.Equal(host, entry.Host);
        Assert.Equal(port, entry.Port);
        Assert.Equal(kind, entry.Kind);
        Assert.Equal(user, entry.Username);
        Assert.Equal(pass, entry.Password);
    }

    [Fact]
    public void Parses_base64_credentials()
    {
        string userB64 = Convert.ToBase64String("alice"u8);
        string passB64 = Convert.ToBase64String("s3cret"u8);

        Assert.True(ProxyPool.TryParseLine($"1.2.3.4:8080@{userB64}:{passB64}", out ProxyEntry entry));
        Assert.Equal("1.2.3.4", entry.Host);
        Assert.Equal(8080, entry.Port);
        Assert.Equal(ProxyKind.Http, entry.Kind);
        Assert.Equal("alice", entry.Username);
        Assert.Equal("s3cret", entry.Password);
    }

    [Theory]
    [InlineData("not-a-proxy-line")]
    [InlineData("1.2.3.4")]              // no port
    [InlineData("1.2.3.4:not-a-port")]
    [InlineData(":8080")]                // no host
    public void Rejects_malformed_lines(string line) => Assert.False(ProxyPool.TryParseLine(line, out _));

    [Fact]
    public void LoadList_parses_proxies_and_refresh_urls_and_skips_blank_lines()
    {
        var pool = new ProxyPool();
        pool.LoadList("""
            1.2.3.4:8080

            *5.6.7.8:1080
            #https://example.com/more-proxies.txt
            """);

        Assert.Equal(2, pool.AvailableCount);
        Assert.NotNull(pool.Pick());
    }

    [Fact]
    public void Pick_excludes_banned_proxies()
    {
        var pool = new ProxyPool();
        pool.LoadList("1.2.3.4:8080\n5.6.7.8:8080");

        ProxyEntry first = pool.Pick()!;
        pool.Ban(first, TimeSpan.FromMinutes(5));

        ProxyEntry? second = pool.Pick();
        Assert.NotNull(second);
        Assert.NotEqual(first.Key, second!.Key);
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public void Ban_with_zero_duration_removes_the_proxy_permanently()
    {
        var pool = new ProxyPool();
        pool.LoadList("1.2.3.4:8080");

        ProxyEntry proxy = pool.Pick()!;
        pool.Ban(proxy, TimeSpan.Zero);

        Assert.Equal(0, pool.AvailableCount);
        Assert.Null(pool.Pick());
    }

    [Fact]
    public async Task Ban_expires_after_its_duration()
    {
        var pool = new ProxyPool();
        pool.LoadList("1.2.3.4:8080");

        ProxyEntry proxy = pool.Pick()!;
        pool.Ban(proxy, TimeSpan.FromMilliseconds(20));
        Assert.Equal(0, pool.AvailableCount); // banned, still in the list

        await Task.Delay(100);

        Assert.Equal(1, pool.AvailableCount);
        Assert.NotNull(pool.Pick());
    }

    [Fact]
    public async Task RefreshAsync_merges_new_proxies_from_a_hash_url_and_skips_duplicates()
    {
        var handler = new FakeHandler("1.2.3.4:8080\n9.9.9.9:9090\n");
        var pool = new ProxyPool(new HttpClient(handler));
        pool.LoadList("1.2.3.4:8080\n#https://example.com/list.txt");

        await pool.RefreshAsync();

        Assert.Equal(2, pool.AvailableCount); // 1.2.3.4 wasn't duplicated, 9.9.9.9 was added
    }

    [Fact]
    public async Task RefreshAsync_is_best_effort_and_does_not_throw_on_a_failing_url()
    {
        var pool = new ProxyPool(new HttpClient(new FakeHandler(null, HttpStatusCode.InternalServerError)));
        pool.LoadList("1.2.3.4:8080\n#https://example.com/list.txt");

        await pool.RefreshAsync(); // should not throw

        Assert.Equal(1, pool.AvailableCount);
    }

    private sealed class FakeHandler(string? body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null) response.Content = new StringContent(body);
            return Task.FromResult(response);
        }
    }
}
