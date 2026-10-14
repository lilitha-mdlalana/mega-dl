using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaDl.Core.Crypto;
using MegaDl.Core.Links;

namespace MegaDl.Core.Api;

public sealed record RemoteFileInfo(string Name, long Size, string DownloadUrl, FileKey Key);

/// <summary>
/// Thin client for MEGA's JSON command API: POST a JSON array of commands to /cs,
/// get a JSON array of results back (or a bare negative int on error).
///
/// Java port note: MegaAPI.java. This first slice covers anonymous public-file access
/// (the "g" command). Login, folder listing, uploads and hashcash come in later phases
/// — see docs/PORT_PLAN.md.
/// </summary>
public sealed class MegaApiClient(HttpClient http, string appKey = MegaApiClient.DefaultAppKey)
{
    public const string ApiUrl = "https://g.api.mega.co.nz";
    public const string DefaultAppKey = "BdARkQSQ"; // public MEGAcmd app key, same as MegaBasterd
    private const int MaxAttempts = 6;

    private long _seq = Random.Shared.Next(0, int.MaxValue);

    public async Task<RemoteFileInfo> GetFileInfoAsync(MegaLink link, CancellationToken ct = default)
    {
        if (link.Kind != MegaLinkKind.File)
            throw new NotSupportedException("Folder links arrive in Phase 2 (folder listing + node keys).");

        var key = FileKey.FromBase64Url(link.Key);
        var command = new JsonObject { ["a"] = "g", ["g"] = 1, ["p"] = link.Id };
        JsonNode result = await RequestAsync(command, ct);

        long size = result["s"]!.GetValue<long>();
        string url = result["g"]?.GetValue<string>() is { Length: > 0 } g ? g : throw new MegaApiException(-101);
        string name = NodeAttributes.TryDecryptName(result["at"]!.GetValue<string>(), key.AesKey)
                      ?? throw new MegaApiException(-14);

        return new RemoteFileInfo(name, size, url, key);
    }

    /// <summary>Sends one command, retrying transient errors with exponential backoff.</summary>
    internal async Task<JsonNode> RequestAsync(JsonObject command, CancellationToken ct, string? extraQuery = null)
    {
        string? hashcashHeader = null;

        for (int attempt = 1; ; attempt++)
        {
            string url = $"{ApiUrl}/cs?id={Interlocked.Increment(ref _seq)}&ak={appKey}{extraQuery}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new JsonArray(command.DeepClone())),
            };
            if (hashcashHeader is not null)
                request.Headers.Add("X-Hashcash", hashcashHeader);

            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.PaymentRequired
                && response.Headers.TryGetValues("X-Hashcash", out var challenges))
            {
                // Solve the proof-of-work and retry the same command with the solution attached.
                hashcashHeader = HashcashSolver.BuildSolutionHeader(challenges.First());
                continue;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            JsonNode? root = JsonNode.Parse(body);

            // Error shape 1: bare int ("-3"). Error shape 2: array with an int inside ("[-9]").
            int? error = root switch
            {
                JsonValue v when v.TryGetValue(out int code) => code,
                JsonArray { Count: > 0 } a when a[0] is JsonValue v0 && v0.TryGetValue(out int code) && code < 0 => code,
                _ => null,
            };

            if (error is null)
                return root is JsonArray arr ? arr[0]! : throw new JsonException($"Unexpected API response: {body}");

            var ex = new MegaApiException(error.Value);
            if (!ex.IsTransient || attempt >= MaxAttempts) throw ex;
            await Task.Delay(Backoff(attempt), ct);
        }
    }

    // 1s, 2s, 4s, 8s (capped) — same shape as MiscTools.getWaitTimeExpBackOff.
    internal static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));
}
