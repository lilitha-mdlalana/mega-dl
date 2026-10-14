using System.Diagnostics;
using MegaDl.Core;
using MegaDl.Core.Api;
using MegaDl.Core.Links;
using MegaDl.Core.Transfers;

// Usage:
//   dotnet run --project src/MegaDl.Console -- parse <link>
//   dotnet run --project src/MegaDl.Console -- info  <link>
//   dotnet run --project src/MegaDl.Console -- get   <link> [output-dir]

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
RemoteFileInfo file = await api.GetFileInfoAsync(link, cts.Token);
Console.WriteLine($"Name: {file.Name}\nSize: {Format(file.Size)}");

if (command == "info") return 0;

string dir = args.Length > 2 ? args[2] : Directory.GetCurrentDirectory();
string path = Path.Combine(dir, file.Name);
var downloader = new FileDownloader(http);

// The download runs on the thread pool; this loop just *samples* progress twice a
// second. Same throttling pattern the desktop UI uses.
var sw = Stopwatch.StartNew();
Task download = downloader.DownloadAsync(file, path, cts.Token);
while (!download.IsCompleted)
{
    await Task.WhenAny(download, Task.Delay(500));
    long done = downloader.BytesDone;
    double speed = done / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.Write($"\r{done * 100.0 / Math.Max(file.Size, 1),6:0.0}%  {Format(done)} / {Format(file.Size)}  {Format((long)speed)}/s   ");
}
await download; // rethrows if it failed
Console.WriteLine($"\nSaved to {path}\nVerifying integrity (MEGA meta MAC)...");

bool ok = await FileDownloader.VerifyAsync(path, file.Key, cts.Token);
Console.WriteLine(ok ? "OK: file is intact." : "MISMATCH: file is corrupt or the key is wrong.");
return ok ? 0 : 2;

static string Format(long bytes) => ByteSize.Format(bytes);
