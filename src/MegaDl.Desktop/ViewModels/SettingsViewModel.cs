using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaDl.Core.Settings;
using MegaDl.Desktop.Services;

namespace MegaDl.Desktop.ViewModels;

/// <summary>
/// Backs the settings window (Phase 3). Every property setter below applies the change
/// live (theme, font, clipboard watcher on/off) and persists it immediately — there's no
/// separate "Save" step, matching docs/PORT_PLAN.md ("applied immediately, no restart").
/// Java port note: behaviour only, not SettingsDialog.java's Swing layout.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ClipboardWatcher? _clipboardWatcher;
    private bool _loading;

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<string> FontFamilies { get; } =
        [.. FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n)];

    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private string? _fontFamily;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private string _downloadFolder = "";
    [ObservableProperty] private bool _clipboardWatcherEnabled;

    /// <summary>Raised by the "Browse…" button; the view owns the folder-picker dialog (it needs a TopLevel).</summary>
    public event Action<Action<string?>>? BrowseDownloadFolderRequested;

    public SettingsViewModel(AppSettings settings, ClipboardWatcher? clipboardWatcher)
    {
        _settings = settings;
        _clipboardWatcher = clipboardWatcher;

        _loading = true;
        Theme = settings.Theme;
        FontFamily = settings.FontFamily;
        FontSize = settings.FontSize;
        DownloadFolder = settings.ResolvedDownloadFolder;
        ClipboardWatcherEnabled = settings.ClipboardWatcherEnabled;
        _loading = false;
    }

    /// <summary>Used by the XAML previewer only.</summary>
    public SettingsViewModel() : this(new AppSettings(), null) { }

    [RelayCommand]
    private void BrowseDownloadFolder() =>
        BrowseDownloadFolderRequested?.Invoke(path =>
        {
            if (!string.IsNullOrWhiteSpace(path)) DownloadFolder = path;
        });

    partial void OnThemeChanged(AppTheme value)
    {
        ApplyTheme(value);
        Persist(s => s.Theme = value);
    }

    partial void OnFontFamilyChanged(string? value)
    {
        ApplyFont(value, FontSize);
        Persist(s => s.FontFamily = value);
    }

    partial void OnFontSizeChanged(double value)
    {
        ApplyFont(FontFamily, value);
        Persist(s => s.FontSize = value);
    }

    partial void OnDownloadFolderChanged(string value) => Persist(s => s.DownloadFolder = value);

    partial void OnClipboardWatcherEnabledChanged(bool value)
    {
        if (_clipboardWatcher is not null) _clipboardWatcher.Enabled = value;
        Persist(s => s.ClipboardWatcherEnabled = value);
    }

    private void Persist(Action<AppSettings> apply)
    {
        if (_loading) return; // don't re-save while populating from the ctor
        apply(_settings);
        _settings.Save();
    }

    public static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public static void ApplyFont(string? fontFamily, double fontSize)
    {
        if (Application.Current is null) return;
        Application.Current.Resources["AppFontFamily"] =
            string.IsNullOrWhiteSpace(fontFamily) ? Avalonia.Media.FontFamily.Default : new Avalonia.Media.FontFamily(fontFamily);
        Application.Current.Resources["AppFontSize"] = fontSize;
    }
}
