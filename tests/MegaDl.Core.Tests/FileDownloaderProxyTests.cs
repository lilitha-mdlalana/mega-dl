using System.Net;
using MegaDl.Core.Api;
using MegaDl.Core.Proxy;
using MegaDl.Core.Transfers;
using Xunit;

namespace MegaDl.Core.Tests;

/// <summary>End-to-end check that FetchChunkAsync actually falls back to a proxy on 509, not
/// just that ProxyPool's bookkeeping works in isolation (see ProxyPoolTests).</summary>
public class FileDownloaderProxyTests
{
    [Fact]
    public async Task FetchChunkAsync_switches_to_a_proxy_after_509_and_succeeds()
    {
        byte[] chunkBytes = [1, 2, 3, 4, 5];
        var directHttp = new HttpClient(new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)509)));
        var pool = new ProxyPool(clientFactory: (_, _) =>
            new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(chunkBytes) })));
        pool.LoadList("1.2.3.4:8080");

        var downloader = new FileDownloader(directHttp, proxyPool: pool,
            proxySettings: new SmartProxySettings(Enabled: true));

        byte[] result = await downloader.FetchChunkAsync("http://fake/file", new Chunk(1, 0, chunkBytes.Length), chunkBytes.Length, default);

        Assert.Equal(chunkBytes, result);
    }

    [Fact]
    public async Task FetchChunkAsync_throws_quota_exceeded_when_proxy_disabled()
    {
        var directHttp = new HttpClient(new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)509)));
        var downloader = new FileDownloader(directHttp); // no proxy pool at all — Phase 1 behaviour

        await Assert.ThrowsAsync<TransferQuotaExceededException>(() =>
            downloader.FetchChunkAsync("http://fake/file", new Chunk(1, 0, 5), 5, default));
    }

    [Fact]
    public async Task Force_mode_goes_straight_to_a_proxy_without_trying_direct_first()
    {
        bool directWasCalled = false;
        byte[] chunkBytes = [9, 9, 9];
        var directHttp = new HttpClient(new FakeHandler(_ => { directWasCalled = true; return new HttpResponseMessage(HttpStatusCode.OK); }));
        var pool = new ProxyPool(clientFactory: (_, _) =>
            new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(chunkBytes) })));
        pool.LoadList("1.2.3.4:8080");

        var downloader = new FileDownloader(directHttp, proxyPool: pool,
            proxySettings: new SmartProxySettings(Enabled: true, Force: true));

        byte[] result = await downloader.FetchChunkAsync("http://fake/file", new Chunk(1, 0, chunkBytes.Length), chunkBytes.Length, default);

        Assert.Equal(chunkBytes, result);
        Assert.False(directWasCalled);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
