using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
            throw new NotSupportedException("Use GetFolderAsync for folder (or file-in-folder) links.");

        var key = FileKey.FromBase64Url(link.Key);
        var command = new JsonObject { ["a"] = "g", ["g"] = 1, ["p"] = link.Id };
        JsonNode result = await RequestAsync(command, ct);

        long size = result["s"]!.GetValue<long>();
        string url = result["g"]?.GetValue<string>() is { Length: > 0 } g ? g : throw new MegaApiException(-101);
        string name = NodeAttributes.TryDecryptName(result["at"]!.GetValue<string>(), key.AesKey)
                      ?? throw new MegaApiException(-14);

        return new RemoteFileInfo(name, size, url, key);
    }

    /// <summary>
    /// Lists every node (file and folder) under a public folder link. Node keys arrive
    /// wrapped with the folder's share key (<see cref="NodeKey.Unwrap"/>) — no per-parent
    /// chaining, every node unwraps directly with the link's key. A node whose "k" field
    /// doesn't decrypt with this key (a foreign nested share) is silently skipped.
    /// </summary>
    public async Task<IReadOnlyList<FolderNode>> GetFolderAsync(MegaLink link, CancellationToken ct = default)
    {
        if (link.Kind is not (MegaLinkKind.Folder or MegaLinkKind.FileInFolder))
            throw new NotSupportedException("GetFolderAsync is for folder (or file-in-folder) links.");

        byte[] folderKey = Base64Url.Decode(link.Key);
        var command = new JsonObject { ["a"] = "f", ["c"] = 1, ["r"] = 1, ["ca"] = 1 };
        JsonNode result = await RequestAsync(command, ct, extraQuery: $"&n={link.Id}");

        var nodes = new List<FolderNode>();
        foreach (JsonNode? entry in result["f"]?.AsArray() ?? [])
        {
            if (entry is null) continue;

            string? fullKey = entry["k"]?.GetValue<string>();
            string? attr = entry["a"]?.GetValue<string>();
            if (string.IsNullOrEmpty(fullKey) || string.IsNullOrEmpty(attr)) continue;

            (byte[] nodeKey, string name)? resolved = ResolveNodeKey(fullKey, attr, folderKey);
            if (resolved is null) continue;

            string handle = entry["h"]!.GetValue<string>();
            string? parent = entry["p"]?.GetValue<string>();
            int type = entry["t"]!.GetValue<int>();
            long size = entry["s"]?.GetValue<long>() ?? 0;

            nodes.Add(new FolderNode(handle, parent, IsFolder: type != 0, resolved.Value.name, size, resolved.Value.nodeKey));
        }
        return nodes;
    }

    /// <summary>Resolves a download URL for one file node inside a folder (the "n" + "&amp;n=" shape).</summary>
    public async Task<RemoteFileInfo> GetFileInFolderAsync(string folderId, FolderNode file, CancellationToken ct = default)
    {
        if (file.IsFolder) throw new ArgumentException("Not a file node.", nameof(file));
        if (file.NodeKey.Length != 32) throw new MegaApiException(-14, "File node key must be 32 bytes.");

        var command = new JsonObject { ["a"] = "g", ["g"] = 1, ["n"] = file.Handle };
        JsonNode result = await RequestAsync(command, ct, extraQuery: $"&n={folderId}");

        long size = result["s"]?.GetValue<long>() ?? file.Size;
        string url = result["g"]?.GetValue<string>() is { Length: > 0 } g ? g : throw new MegaApiException(-101);

        return new RemoteFileInfo(file.Name, size, url, FileKey.FromBytes(file.NodeKey));
    }

    /// <summary>
    /// MEGA may send several ':'/'/'-separated key candidates per node (nested shares);
    /// try each until one both AES-ECB-unwraps and decrypts a readable "n" (name) attribute.
    /// </summary>
    private static (byte[] nodeKey, string name)? ResolveNodeKey(string fullKey, string attr, byte[] folderKey)
    {
        foreach (string segment in fullKey.Split('/'))
        {
            string[] parts = segment.Split(':');
            if (parts.Length < 2) continue;

            try
            {
                byte[] wrapped = Base64Url.Decode(parts[^1]);
                byte[] nodeKey = NodeKey.Unwrap(wrapped, folderKey);
                byte[] attrKey = nodeKey.Length == 32 ? FileKey.FromBytes(nodeKey).AesKey : nodeKey;

                if (NodeAttributes.TryDecryptName(attr, attrKey) is { } name)
                    return (nodeKey, name);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // Not the right segment/key for this node — try the next candidate.
            }
        }
        return null;
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
