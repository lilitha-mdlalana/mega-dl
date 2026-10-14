using System.Diagnostics;
using MegaDl.Core;
using MegaDl.Core.Api;
using MegaDl.Core.Links;
using MegaDl.Core.Transfers;

// Usage:
//   dotnet run --project src/MegaDl.Console -- parse  <link>
//   dotnet run --project src/MegaDl.Console -- info    <link>
//   dotnet run --project src/MegaDl.Console -- get     <link> [output-dir]
//
// <link> can be a file link, a folder link (downloads every file in it, preserving
// subfolder structure under <output-dir>/<folder name>/...), or a folder link with a
// specific file selected (mega.nz/folder/ID#KEY/file/FILEID — downloads just that one).

if (args.Length < 2)
{
    Console.WriteLine("usage: megadl parse|info|get <mega-link> [output-dir]");
    return 1;
}

string command = args[0];
var link = MegaLink.Parse(args[1]);

if (command == "parse")
{
    Console.WriteLine($"Kind: {link.Kind}\nId:   {link.Id}\nKey:  {link.Key}\nNorm: {link}");
    return 0;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaDl/0.1");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var api = new MegaApiClient(http);
string dir = args.Length > 2 ? args[2] : Directory.GetCurrentDirectory();

try
{
    if (link.Kind == MegaLinkKind.File)
    {
        RemoteFileInfo file = await api.GetFileInfoAsync(link, cts.Token);
        Console.WriteLine($"Name: {file.Name}\nSize: {Format(file.Size)}");
        if (command == "info") return 0;

        return await DownloadOneAsync(file, Path.Combine(dir, file.Name));
    }

    // Folder (every file in it) or file-in-folder (just that one) — both need the listing
    // first, since a file inside a folder is downloaded by handle, not by its own link key.
    IReadOnlyList<FolderNode> nodes = await api.GetFolderAsync(link, cts.Token);
    var byHandle = nodes.ToDictionary(n => n.Handle);

    IEnumerable<FolderNode> targets = link.Kind == MegaLinkKind.FileInFolder
        ? nodes.Where(n => n.Handle == link.ChildId)
        : nodes.Where(n => !n.IsFolder);
    var files = targets.ToList();

    Console.WriteLine($"{nodes.Count} node(s) in folder, {files.Count} file(s) selected" +
                       $" ({Format(files.Sum(f => f.Size))} total).");
    if (command == "info")
    {
        foreach (var f in files)
            Console.WriteLine($"  [{f.Handle}] {BuildRelativePath(byHandle, f)}  ({Format(f.Size)})");
        return 0;
    }

    int exitCode = 0;
    foreach (FolderNode node in files)
    {
        RemoteFileInfo file = await api.GetFileInFolderAsync(link.Id, node, cts.Token);
        string path = Path.Combine(dir, BuildRelativePath(byHandle, node));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Console.WriteLine($"\n{BuildRelativePath(byHandle, node)}:");
        int code = await DownloadOneAsync(file, path);
        if (code != 0) exitCode = code;
    }
    return exitCode;
}
catch (Exception ex) when (Flatten(ex) is TransferQuotaExceededException)
{
    Console.WriteLine("\nQuota exceeded: this IP has used up MEGA's free transfer quota (HTTP 509). " +
                       "Wait, use a different network, or (Phase 4) enable the smart proxy.");
    return 3;
}
catch (Exception ex) when (Flatten(ex) is MegaApiException)
{
    Console.WriteLine($"\nMEGA API error: {Flatten(ex).Message}");
    return 4;
}

async Task<int> DownloadOneAsync(RemoteFileInfo file, string path)
{
    var downloader = new FileDownloader(http);

    // The download runs on the thread pool; this loop just *samples* progress twice a
    // second. Same throttling pattern the desktop UI uses. The MAC is folded in as
    // chunks land, so there's no separate verify pass after this completes.
    var sw = Stopwatch.StartNew();
    Task<bool> download = downloader.DownloadAsync(file, path, cts.Token);
    while (!download.IsCompleted)
    {
        await Task.WhenAny(download, Task.Delay(500));
        long done = downloader.BytesDone;
        double speed = done / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.Write($"\r{done * 100.0 / Math.Max(file.Size, 1),6:0.0}%  {Format(done)} / {Format(file.Size)}  {Format((long)speed)}/s   ");
    }
    bool ok = await download; // rethrows if it failed

    Console.WriteLine(ok
        ? $"\nSaved to {path}\nOK: integrity check (MEGA meta MAC) passed."
        : $"\nMISMATCH: downloaded data doesn't match MEGA's meta MAC. Left as {path}.part for inspection.");
    return ok ? 0 : 2;
}

// <download folder>/<folder name>/<subfolder path>/<file name> — walks the parent chain
// the "f" listing gives us back up to the folder's root.
static string BuildRelativePath(IReadOnlyDictionary<string, FolderNode> byHandle, FolderNode node)
{
    var parts = new List<string> { node.Name };
    string? parentHandle = node.ParentHandle;
    while (parentHandle is not null && byHandle.TryGetValue(parentHandle, out FolderNode? parent))
    {
        parts.Insert(0, parent.Name);
        parentHandle = parent.ParentHandle;
    }
    return Path.Combine(parts.ToArray());
}

// Parallel.ForEachAsync wraps a single worker failure as-is but wraps 2+ concurrent
// failures in an AggregateException; this unwraps either case to the first real cause.
static Exception Flatten(Exception ex) => ex is AggregateException { InnerExceptions: [var first, ..] } ? first : ex;

static string Format(long bytes) => ByteSize.Format(bytes);
