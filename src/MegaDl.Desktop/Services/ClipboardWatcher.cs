using Avalonia.Controls;
using Avalonia.Threading;
using MegaDl.Core.Links;

namespace MegaDl.Desktop.Services;

/// <summary>
/// Polls the clipboard once a second for MEGA links and raises <see cref="LinksFound"/>
/// when it sees new ones — it never queues anything itself. There's no cross-platform
/// "clipboard changed" event in Avalonia, so polling plus a last-seen-text comparison is
/// the only portable option (same approach as MegaBasterd's ClipboardSpy).
/// </summary>
public sealed class ClipboardWatcher(TopLevel topLevel) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private string? _lastText;
    private Task? _loop;

    /// <summary>Checked once per tick; toggling it on/off takes effect on the next tick, no restart needed.</summary>
    public bool Enabled { get; set; }

    public event Action<IReadOnlyList<MegaLink>>? LinksFound;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (!Enabled) continue;

            string? text = await Dispatcher.UIThread.InvokeAsync(async () => await (topLevel.Clipboard?.GetTextAsync() ?? Task.FromResult<string?>(null)));
            if (text is null || text == _lastText) continue;
            _lastText = text;

            IReadOnlyList<MegaLink> links = MegaLink.FindAll(text);
            if (links.Count > 0) LinksFound?.Invoke(links);
        }
    }

    public void Dispose() => _cts.Cancel();
}
