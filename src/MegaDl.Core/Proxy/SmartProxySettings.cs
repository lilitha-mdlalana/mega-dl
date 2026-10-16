namespace MegaDl.Core.Proxy;

/// <summary>
/// A snapshot of the smart-proxy knobs for one download. Taken once when the download
/// starts (see <see cref="MegaDl.Core.Transfers.TransferManager"/>) — a setting change in
/// the UI applies to the next download, not an in-flight one.
/// </summary>
public sealed record SmartProxySettings(
    bool Enabled = false,
    bool Force = false,
    int BanSeconds = 300,
    int TimeoutSeconds = 10);
