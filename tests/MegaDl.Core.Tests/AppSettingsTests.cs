using System.Text.Json;
using MegaDl.Core.Settings;
using Xunit;

namespace MegaDl.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Json_roundtrips_every_field()
    {
        var settings = new AppSettings
        {
            Theme = AppTheme.Dark,
            FontFamily = "Consolas",
            FontSize = 15,
            DownloadFolder = "/tmp/downloads",
            ClipboardWatcherEnabled = false,
        };

        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(loaded);
        Assert.Equal(AppTheme.Dark, loaded!.Theme);
        Assert.Equal("Consolas", loaded.FontFamily);
        Assert.Equal(15, loaded.FontSize);
        Assert.Equal("/tmp/downloads", loaded.DownloadFolder);
        Assert.False(loaded.ClipboardWatcherEnabled);
    }

    [Fact]
    public void Defaults_are_system_theme_and_clipboard_watcher_on()
    {
        var settings = new AppSettings();
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.True(settings.ClipboardWatcherEnabled);
    }

    [Fact]
    public void ResolvedDownloadFolder_defaults_to_user_downloads_when_unset()
    {
        var settings = new AppSettings { DownloadFolder = null };
        Assert.Contains("Downloads", settings.ResolvedDownloadFolder);
    }

    [Fact]
    public void ResolvedDownloadFolder_uses_the_explicit_value_when_set()
    {
        var settings = new AppSettings { DownloadFolder = "/custom/path" };
        Assert.Equal("/custom/path", settings.ResolvedDownloadFolder);
    }

    [Fact]
    public void Save_then_load_roundtrips_through_the_real_file()
    {
        // Exercises Save/Load's actual file I/O, but against a throwaway override of
        // SettingsDirectory so it never touches the real app-data folder.
        string originalHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        string originalAppData = Environment.GetEnvironmentVariable("APPDATA") ?? "";
        string tempRoot = Path.Combine(Path.GetTempPath(), "megadl-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("HOME", tempRoot);
        Environment.SetEnvironmentVariable("APPDATA", tempRoot);
        try
        {
            var settings = new AppSettings { Theme = AppTheme.Light, FontSize = 20 };
            settings.Save();

            var loaded = AppSettings.Load();
            Assert.Equal(AppTheme.Light, loaded.Theme);
            Assert.Equal(20, loaded.FontSize);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
