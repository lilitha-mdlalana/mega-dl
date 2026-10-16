using System.Text.Json;
using System.Text.Json.Serialization;

namespace MegaDl.Core.Settings;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Everything the app remembers between runs, as one JSON file — no database (see
/// docs/PORT_PLAN.md: "Simplification vs MegaBasterd"). Every setter here is meant to be
/// applied immediately by the caller and then persisted with <see cref="Save"/>; this
/// class only owns the data and where it lives on disk, not the live UI effect of a change.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string? FontFamily { get; set; }        // null = platform default
    public double FontSize { get; set; } = 13;
    public string? DownloadFolder { get; set; }     // null = default (~/Downloads), see ResolvedDownloadFolder
    public bool ClipboardWatcherEnabled { get; set; } = true;

    // Phase 4: smart proxy. See MegaDl.Core.Proxy.ProxyPool for the list format.
    public bool SmartProxyEnabled { get; set; }
    public bool SmartProxyForce { get; set; }       // use proxies from the start, not just after a 509
    public int SmartProxyBanSeconds { get; set; } = 300;
    public int SmartProxyTimeoutSeconds { get; set; } = 10;
    public string ProxyList { get; set; } = "";

    [JsonIgnore]
    public string ResolvedDownloadFolder =>
        string.IsNullOrWhiteSpace(DownloadFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : DownloadFolder;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>%APPDATA%\MegaDl on Windows, ~/Library/Application Support/MegaDl on Mac.</summary>
    public static string SettingsDirectory =>
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MegaDl")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MegaDl");

    private static string FilePath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Never throws — a missing or corrupt settings file just means defaults.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable — fall through to defaults rather than crash the app.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
