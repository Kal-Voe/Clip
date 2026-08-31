using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Clip.Core;
using Microsoft.Web.WebView2.Core;
using Svg;
using DrawingImage = System.Drawing.Image;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfImage = System.Windows.Controls.Image;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfListBoxItem = System.Windows.Controls.ListBoxItem;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPen = System.Windows.Media.Pen;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfPath = System.Windows.Shapes.Path;
using WpfShape = System.Windows.Shapes.Shape;
using WatcherAppChoice = Clip.Core.AppChoice;
using WatcherAppDiscovery = Clip.Core.OpenWithAppDiscovery;
using WatcherAppLauncher = Clip.Core.OpenWithAppLauncher;
using WatcherPackageLogoLookup = Clip.Watcher.PackageLogoLookup;
using WatcherPdfPreviewRenderer = Clip.Watcher.PdfPreviewRenderer;
using WatcherShellIconReader = Clip.Watcher.ShellIconReader;
using WatcherStartMenuIconLookup = Clip.Watcher.StartMenuIconLookup;
using WatcherStaticDocumentPreviewRenderer = Clip.Watcher.StaticDocumentPreviewRenderer;

namespace Clip.Shell;

internal enum ClipThemePreference
{
    System,
    Light,
    Dark,
}

internal enum AppIconPreference
{
    Light,
    Dark,
}

internal sealed class ClipShellSettings
{
    private const string ClipboardFolderName = "Clipboard History";
    private const string PreviousClipboardFolderName = "Clipboard";

    public ClipThemePreference Theme { get; set; } = ClipThemePreference.System;
    public AppIconPreference AppIcon { get; set; } = AppIconPreference.Light;
    public PasteFormatPreference DefaultPasteFormat { get; set; } = PasteFormatPreference.PlainText;
    public int? HistoryLimit { get; set; } = 500;
    public long? MaxItemSizeBytes { get; set; } = 50L * 1024 * 1024;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; } = true;

    /// <summary>
    /// Off by default on purpose. Recognized text turns a screenshot of a password manager or a
    /// bank page into plain, greppable text on disk where it was previously only pixels.
    /// </summary>
    public bool ExtractTextFromImages { get; set; }

    /// <summary>Show the source app on a second line under each list item.</summary>
    public bool ShowSourceAppInList { get; set; } = true;

    /// <summary>
    /// Put a real file in the drag as well as the text, so dropping a text or link clip on the
    /// desktop saves a .txt or .url.
    ///
    /// Off by default, and the default is the interesting part. Apps that accept both a file and
    /// text from one drag mostly prefer the file: VS Code opens it, Slack and Gmail attach it.
    /// Turning this on therefore buys the occasional drop onto the desktop at the cost of the
    /// everyday drop into a text field, so the everyday one is what is shipped switched on.
    /// </summary>
    public bool DragClipsAsFiles { get; set; }

    /// <summary>
    /// The acrylic glass look: real blur-behind sampled from the desktop, with the interior zones
    /// as light tints over it. On by default. It was off for 1.2.4 only because 1.2.0-1.2.3 shipped
    /// a DWM backdrop that painted a flat grey sheet instead of blurring anything — see
    /// PaletteBackdrop for the measurement. Now that the blur is real, the default is on again.
    /// Falls back silently to the opaque palette on builds older than Windows 10 1803, or if the
    /// compositor refuses the accent policy.
    /// </summary>
    public bool TranslucentBackground { get; set; } = true;

    /// <summary>
    /// Capture switched off entirely. Toggled from the watcher's tray menu (another process),
    /// so the live value is read from disk at each capture; this property exists so a settings
    /// save from this process round-trips the key instead of silently dropping it.
    /// </summary>
    public bool CapturePaused { get; set; }

    public string? ClipboardFolderPath { get; set; }
    public ClipHotkeySettings Hotkeys { get; set; } = new();
    public ClipPrivacySettings Privacy { get; set; } = new();
    public List<string> AltVPasteApps { get; set; } = new();
    public List<ClipAppOverride> AppOverrides { get; set; } = new();

    public static string DefaultClipboardFolderPath => Path.Combine(
        Clip.Core.ClipStoragePaths.Root,
        ClipboardFolderName);

    public static string PreviousDefaultClipboardFolderPath => Path.Combine(
        Clip.Core.ClipStoragePaths.Root,
        PreviousClipboardFolderName);

    public string EffectiveClipboardFolderPath()
    {
        return string.IsNullOrWhiteSpace(ClipboardFolderPath) ? DefaultClipboardFolderPath : ClipboardFolderPath;
    }

    public void ResetToDefaults()
    {
        Theme = ClipThemePreference.System;
        AppIcon = AppIconPreference.Light;
        DefaultPasteFormat = PasteFormatPreference.PlainText;
        HistoryLimit = 500;
        MaxItemSizeBytes = 50L * 1024 * 1024;
        CheckForUpdatesOnStartup = true;
        InstallUpdatesAutomatically = true;
        ExtractTextFromImages = false;
        ShowSourceAppInList = true;
        DragClipsAsFiles = false;
        TranslucentBackground = true;
        CapturePaused = false;
        ClipboardFolderPath = null;
        Hotkeys = new ClipHotkeySettings();
        Hotkeys.ResetToDefaults();
        Privacy = new ClipPrivacySettings();
        AltVPasteApps = new List<string>();
        AppOverrides = new List<ClipAppOverride>();
    }

    public static string SettingsPath => Clip.Core.ClipStoragePaths.SettingsPath;

    public static ClipShellSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new ClipShellSettings();
            }

            var settings = JsonSerializer.Deserialize<ClipShellSettings>(File.ReadAllText(SettingsPath)) ?? new ClipShellSettings();
            settings.Hotkeys ??= new ClipHotkeySettings();
            settings.Hotkeys.Normalize();
            settings.Privacy ??= new ClipPrivacySettings();
            settings.Privacy.Normalize();
            settings.AltVPasteApps ??= new List<string>();
            settings.AppOverrides ??= new List<ClipAppOverride>();
            foreach (var entry in settings.AppOverrides)
            {
                entry.Action = ClipAppOverride.NormalizeAction(entry.Action);
            }
            if (settings.AltVPasteApps.Count > 0)
            {
                foreach (var legacy in settings.AltVPasteApps)
                {
                    if (string.IsNullOrWhiteSpace(legacy)) continue;
                    if (!settings.AppOverrides.Any(o => string.Equals(o.AppName, legacy, StringComparison.OrdinalIgnoreCase) && string.Equals(o.Action, ClipAppOverride.ActionPaste, StringComparison.OrdinalIgnoreCase)))
                    {
                        settings.AppOverrides.Add(new ClipAppOverride { AppName = legacy, Action = ClipAppOverride.ActionPaste, Hotkey = "Alt+V" });
                    }
                }
                settings.AltVPasteApps.Clear();
            }
            if (string.Equals(settings.ClipboardFolderPath, PreviousDefaultClipboardFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                settings.ClipboardFolderPath = null;
            }

            return settings;
        }
        catch (JsonException ex)
        {
            // The file exists but does not parse — a truncated write or a hand-edit gone wrong.
            // Falling back to defaults is right, but the next Save would then flatten those
            // defaults over the user's only copy of their hotkeys and excluded apps. Park the
            // unreadable bytes next door first so they stay recoverable.
            var quarantined = Clip.Core.ClipSharedSettings.QuarantineCorruptSettings();
            ShellLog.Error(ex, $"settings load failed, corrupt file quarantined to {quarantined ?? "nowhere"}");
            return new ClipShellSettings();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings load failed");
            return new ClipShellSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            // Atomic temp-file + rename: a crash mid-write must never leave a truncated
            // settings.json, because a truncated file loads as defaults and the save after
            // that wipes the user's real settings.
            Clip.Core.ClipSharedSettings.WriteSettingsFileAtomic(json);
            ShellLog.Info($"settings saved path={SettingsPath} theme={Theme} appIcon={AppIcon} historyLimit={HistoryLimit?.ToString() ?? "Unlimited"} maxItemSize={ClipItemSizeLimit.MaxItemSizeLabel(MaxItemSizeBytes)} updateCheck={CheckForUpdatesOnStartup} autoInstall={InstallUpdatesAutomatically} clipboardFolder={EffectiveClipboardFolderPath()} openHotkey={Hotkeys.OpenClip} debugHotkey={Hotkeys.SaveDebugLog} excludedApps={Privacy.ExcludedApps.Count}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings save failed");
        }
    }
}

internal sealed class ClipAppOverride
{
    public const string ActionOpenClip = "Open Clip";
    public const string ActionPaste = "Paste";

    // Legacy action labels — migrated to ActionPaste on load.
    public const string LegacyActionPasteImage = "Paste image";
    public const string LegacyActionPasteText = "Paste text";
    public const string LegacyActionPasteFiles = "Paste files";
    public const string LegacyActionPasteLink = "Paste link";

    public static readonly string[] AvailableActions =
    {
        ActionOpenClip,
        ActionPaste,
    };

    public string AppName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public string Action { get; set; } = ActionPaste;
    public string Hotkey { get; set; } = "Alt+V";

    public static string NormalizeAction(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Equals(ActionOpenClip, StringComparison.OrdinalIgnoreCase)) return ActionOpenClip;
        if (v.Equals(ActionPaste, StringComparison.OrdinalIgnoreCase)) return ActionPaste;
        if (v.Equals(LegacyActionPasteImage, StringComparison.OrdinalIgnoreCase)
            || v.Equals(LegacyActionPasteText, StringComparison.OrdinalIgnoreCase)
            || v.Equals(LegacyActionPasteFiles, StringComparison.OrdinalIgnoreCase)
            || v.Equals(LegacyActionPasteLink, StringComparison.OrdinalIgnoreCase))
        {
            return ActionPaste;
        }
        return ActionPaste;
    }
}

internal static class ClipItemSizeLimit
{
    public static bool Allows(ClipboardHistoryItem item, long? maxBytes)
    {
        if (maxBytes is null)
        {
            return true;
        }

        return EstimateBytes(item) <= Math.Max(0, maxBytes.Value);
    }

    public static long EstimateBytes(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color => TextBytes(item.Text) + TextBytes(item.HtmlText) + TextBytes(item.RtfText),
            ClipboardItemKind.Image => ExistingPathBytes(item.AssetPath),
            ClipboardItemKind.Files => item.FilePaths.Sum(TextBytes),
            _ => 0,
        };
    }

    private static long TextBytes(string? text)
    {
        return string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);
    }

    private static long ExistingPathBytes(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static string MaxItemSizeLabel(long? bytes)
    {
        if (bytes is null)
        {
            return "Unlimited";
        }

        return $"{Math.Max(0, bytes.Value) / 1024 / 1024} MB";
    }
}

internal sealed class ClipPrivacySettings
{
    public List<ClipExcludedApp> ExcludedApps { get; set; } = [];

    public void AddExcludedApp(string name, string? executablePath)
    {
        var app = ClipExcludedApp.Create(name, executablePath);
        if (app is null || ExcludedApps.Any(existing => existing.MatchesEntry(app)))
        {
            return;
        }

        ExcludedApps.Add(app);
    }

    public void RemoveExcludedApp(ClipExcludedApp app)
    {
        ExcludedApps.RemoveAll(existing => existing.MatchesEntry(app));
    }

    public void RemoveExcludedApp(string name, string? executablePath)
    {
        var app = ClipExcludedApp.Create(name, executablePath);
        if (app is null)
        {
            return;
        }

        RemoveExcludedApp(app);
    }

    public bool IsExcluded(string? sourceName, string? sourcePath)
    {
        return ExcludedApps.Any(app => app.MatchesSource(sourceName, sourcePath));
    }

    public void Normalize()
    {
        ExcludedApps = ExcludedApps
            .Concat(MigrateLegacyExcludedApps())
            .Select(app => ClipExcludedApp.Create(app.Name, app.ExecutablePath))
            .Where(app => app is not null)
            .Select(app => app!)
            .DistinctBy(app => app.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<ClipExcludedApp> MigrateLegacyExcludedApps()
    {
        var migrated = new List<ClipExcludedApp>();
        if (ExcludedApps.Count > 0)
        {
            return migrated;
        }

        // Older builds stored this as a string array. Keep reading it so users do not lose exclusions.
        try
        {
            var json = File.Exists(ClipShellSettings.SettingsPath) ? File.ReadAllText(ClipShellSettings.SettingsPath) : "";
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Privacy", out var privacy) ||
                !privacy.TryGetProperty("ExcludedApps", out var apps) ||
                apps.ValueKind != JsonValueKind.Array)
            {
                return migrated;
            }

            foreach (var app in apps.EnumerateArray())
            {
                if (app.ValueKind == JsonValueKind.String)
                {
                    var entry = ClipExcludedApp.Create(app.GetString(), null);
                    if (entry is not null)
                    {
                        migrated.Add(entry);
                    }
                }
            }
        }
        catch
        {
        }

        return migrated;
    }
}

internal sealed class ClipExcludedApp
{
    public string Name { get; set; } = "";
    public string? ExecutablePath { get; set; }

    public string Key => NormalizePath(ExecutablePath) ?? NormalizeName(Name) ?? Name;

    public static ClipExcludedApp? Create(string? name, string? executablePath)
    {
        var path = NormalizeEntry(executablePath);
        var displayName = NormalizeEntry(name) ?? Path.GetFileNameWithoutExtension(path);
        if (displayName is null)
        {
            return null;
        }

        return new ClipExcludedApp
        {
            Name = displayName,
            ExecutablePath = path,
        };
    }

    public bool MatchesEntry(ClipExcludedApp other)
    {
        return string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesSource(string? sourceName, string? sourcePath)
    {
        var sourceNameKey = NormalizeName(sourceName);
        var sourcePathKey = NormalizePath(sourcePath);
        var sourcePathNameKey = NormalizeName(Path.GetFileNameWithoutExtension(sourcePath));
        var appPathKey = NormalizePath(ExecutablePath);
        var appNameKey = NormalizeName(Name);
        var appPathNameKey = NormalizeName(Path.GetFileNameWithoutExtension(ExecutablePath));

        return (!string.IsNullOrWhiteSpace(appPathKey) && string.Equals(appPathKey, sourcePathKey, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(appNameKey) &&
                (string.Equals(appNameKey, sourceNameKey, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(appNameKey, sourcePathNameKey, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrWhiteSpace(appPathNameKey) && string.Equals(appPathNameKey, sourceNameKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeEntry(string? value)
    {
        var trimmed = value?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? NormalizeName(string? value)
    {
        var normalized = NormalizeEntry(value);
        return normalized is null ? null : Path.GetFileNameWithoutExtension(normalized);
    }

    private static string? NormalizePath(string? value)
    {
        var normalized = NormalizeEntry(value);
        return normalized is null || !Path.IsPathRooted(normalized) ? null : Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

internal sealed class ClipHotkeySettings
{
    public string OpenClip { get; set; } = ClipHotkeyDefaults.OpenClip;
    public string PasteSelected { get; set; } = ClipHotkeyDefaults.PasteSelected;
    public string CopySelected { get; set; } = ClipHotkeyDefaults.CopySelected;
    public string PinSelected { get; set; } = ClipHotkeyDefaults.PinSelected;
    public string OpenActions { get; set; } = ClipHotkeyDefaults.OpenActions;
    public string OpenSelected { get; set; } = ClipHotkeyDefaults.OpenSelected;
    public string EditSelected { get; set; } = ClipHotkeyDefaults.EditSelected;
    public string SaveDebugLog { get; set; } = ClipHotkeyDefaults.SaveDebugLog;
    public string DeleteSelected { get; set; } = ClipHotkeyDefaults.DeleteSelected;
    public string CloseClip { get; set; } = ClipHotkeyDefaults.CloseClip;

    public void ResetToDefaults()
    {
        OpenClip = ClipHotkeyDefaults.OpenClip;
        PasteSelected = ClipHotkeyDefaults.PasteSelected;
        CopySelected = ClipHotkeyDefaults.CopySelected;
        PinSelected = ClipHotkeyDefaults.PinSelected;
        OpenActions = ClipHotkeyDefaults.OpenActions;
        OpenSelected = ClipHotkeyDefaults.OpenSelected;
        EditSelected = ClipHotkeyDefaults.EditSelected;
        SaveDebugLog = ClipHotkeyDefaults.SaveDebugLog;
        DeleteSelected = ClipHotkeyDefaults.DeleteSelected;
        CloseClip = ClipHotkeyDefaults.CloseClip;
    }

    public void Normalize()
    {
        OpenClip = NormalizeGlobal(OpenClip, ClipHotkeyDefaults.OpenClip);
        PasteSelected = NormalizeLocal(PasteSelected, ClipHotkeyDefaults.PasteSelected);
        CopySelected = NormalizeLocal(CopySelected, ClipHotkeyDefaults.CopySelected);
        PinSelected = NormalizeLocal(PinSelected, ClipHotkeyDefaults.PinSelected);
        OpenActions = NormalizeLocal(OpenActions, ClipHotkeyDefaults.OpenActions);
        OpenSelected = NormalizeLocal(OpenSelected, ClipHotkeyDefaults.OpenSelected);
        EditSelected = NormalizeLocal(EditSelected, ClipHotkeyDefaults.EditSelected);
        SaveDebugLog = NormalizeGlobal(SaveDebugLog, ClipHotkeyDefaults.SaveDebugLog);
        DeleteSelected = NormalizeLocal(DeleteSelected, ClipHotkeyDefaults.DeleteSelected);
        CloseClip = NormalizeLocal(CloseClip, ClipHotkeyDefaults.CloseClip);
    }

    // An empty value means the hotkey is intentionally unbound (no key) — preserve it as "" rather
    // than snapping back to the default. Only a NON-empty, unparseable value falls back.
    private static string NormalizeLocal(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? string.Empty
            : ClipHotkeyGesture.TryParse(value, out var gesture) ? gesture.DisplayText : fallback;

    private static string NormalizeGlobal(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? string.Empty
            : ClipHotkeyGesture.TryParseGlobal(value, out var gesture) ? gesture.DisplayText : fallback;
}

internal static class ClipHotkeyDefaults
{
    public const string OpenClip = "Alt+V";
    public const string PasteSelected = "Enter";
    public const string CopySelected = "Ctrl+C";
    public const string PinSelected = "Ctrl+P";
    public const string OpenActions = "Ctrl+K";
    public const string OpenSelected = "Ctrl+O";
    public const string EditSelected = "Ctrl+E";
    public const string SaveDebugLog = "Ctrl+Shift+L";
    public const string DeleteSelected = "Delete";
    public const string CloseClip = "Esc";
}

internal readonly record struct ClipHotkeyGesture(int WinModifiers, int VirtualKey, ModifierKeys WpfModifiers, Key WpfKey, string DisplayText)
{
    private const int WinModAlt = 0x0001;
    private const int WinModControl = 0x0002;
    private const int WinModShift = 0x0004;
    private const int WinModWindows = 0x0008;

    public static bool TryParse(string? text, out ClipHotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var winModifiers = 0;
        var wpfModifiers = ModifierKeys.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "ALT":
                    winModifiers |= WinModAlt;
                    wpfModifiers |= ModifierKeys.Alt;
                    break;
                case "CTRL":
                case "CONTROL":
                    winModifiers |= WinModControl;
                    wpfModifiers |= ModifierKeys.Control;
                    break;
                case "SHIFT":
                    winModifiers |= WinModShift;
                    wpfModifiers |= ModifierKeys.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    winModifiers |= WinModWindows;
                    wpfModifiers |= ModifierKeys.Windows;
                    break;
                default:
                    return false;
            }
        }

        if (!TryKey(parts[^1], out var key))
        {
            return false;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            return false;
        }

        gesture = new ClipHotkeyGesture(winModifiers, virtualKey, wpfModifiers, key, Format(wpfModifiers, key));
        return true;
    }

    public static bool TryParseGlobal(string? text, out ClipHotkeyGesture gesture)
    {
        return TryParse(text, out gesture) && gesture.WpfModifiers != ModifierKeys.None;
    }

    private static bool TryKey(string text, out Key key)
    {
        key = Key.None;
        if (text.Length == 1 && char.IsLetterOrDigit(text[0]) && Enum.TryParse("D" + char.ToUpperInvariant(text[0]), out key))
        {
            return true;
        }

        // The defaults and every existing settings.json say "Esc", but WPF's Key enum only knows
        // "Escape", so without this alias the whole close-palette binding silently never parsed.
        // Aliasing here fixes installs in the field with no settings migration. "Del" likewise.
        switch (text.ToUpperInvariant())
        {
            case "ESC":
                key = Key.Escape;
                return true;
            case "DEL":
                key = Key.Delete;
                return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out key) && key is not Key.None;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyDisplay(key));
        return string.Join("+", parts);
    }

    private static string KeyDisplay(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        // "Esc" is what keyboards print and what the default CloseClip value stores, so a
        // parsed Escape must format back to "Esc" or Normalize would rewrite settings.
        if (key == Key.Escape)
        {
            return "Esc";
        }

        // Same for Enter, and this one was visible: Key.Enter and Key.Return are one value whose
        // ToString() is "Return", so Normalize turned the default PasteSelected of "Enter" into
        // "Return" in settings.json and the footer keycap advertised a key no keyboard prints.
        if (key == Key.Enter)
        {
            return "Enter";
        }

        return key >= Key.A && key <= Key.Z ? key.ToString() : key.ToString();
    }
}

/// <summary>
/// Decides where the selection lands after a render changed what is visible — most often a
/// search narrowing the list. A selection that is no longer on screen must not survive the
/// render: Enter would paste the off-screen item while the preview shows it as if it matched.
/// </summary>
internal static class PaletteSelection
{
    /// <summary>
    /// The item that should be selected after a render: the current selection while it is
    /// still visible, otherwise the first visible item, and null when nothing is visible
    /// (the caller clears the preview pane). A null <paramref name="selectedId"/> means
    /// nothing is selected — typically because an earlier query emptied the list and cleared
    /// the selection — and lands on the first visible item so Enter has a target again once
    /// the list refills.
    /// </summary>
    public static ClipboardHistoryItem? Reconcile(string? selectedId, IReadOnlyList<ClipboardHistoryItem> visibleItems)
    {
        foreach (var item in visibleItems)
        {
            if (item.Id == selectedId)
            {
                return item;
            }
        }

        return visibleItems.Count > 0 ? visibleItems[0] : null;
    }

    /// <summary>
    /// How many rows PageUp/PageDown jump. The list shows roughly this many rows per screen;
    /// close enough that a page feels like a page without measuring the viewport.
    /// </summary>
    public const int PageStep = 8;

    /// <summary>
    /// The item <paramref name="delta"/> rows away from the current selection in the on-screen
    /// order, clamped to the ends of the list. With no current selection any movement lands on
    /// the first item — arrowing into an unselected list should start at the top, not guess.
    /// Home/End are just deltas of ±Count. Null only when nothing is visible.
    /// </summary>
    public static ClipboardHistoryItem? Step(IReadOnlyList<ClipboardHistoryItem> visibleOrder, string? selectedId, int delta)
    {
        if (visibleOrder.Count == 0)
        {
            return null;
        }

        var index = -1;
        for (var i = 0; i < visibleOrder.Count; i++)
        {
            if (visibleOrder[i].Id == selectedId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return visibleOrder[0];
        }

        // long, so End's +Count on a huge list cannot overflow past the clamp.
        var next = Math.Clamp((long)index + delta, 0, visibleOrder.Count - 1);
        return visibleOrder[(int)next];
    }

    /// <summary>
    /// The item the Ctrl+digit shortcut names: the digit-th row of the on-screen order
    /// (1-based), or null when the list is shorter than that — a shortcut past the end must
    /// do nothing rather than paste the nearest row.
    /// </summary>
    public static ClipboardHistoryItem? DigitPick(IReadOnlyList<ClipboardHistoryItem> visibleOrder, int digit)
    {
        return digit >= 1 && digit <= visibleOrder.Count ? visibleOrder[digit - 1] : null;
    }
}


public partial class MainWindow : Window
{
    private const int OpenHotkeyId = 0x4350;
    private const int DebugLogHotkeyId = 0x4351;
    private const int OpenOverrideHotkeyId = 0x4352;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const int WmHotkey = 0x0312;
    private const int WmClipboardUpdate = 0x031D;
    private const int ClipboardReadAttempts = 4;
    private const int ClipboardReadRetryDelayMs = 90;

    private static PROPERTYKEY PkeyAppUserModelId => new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr data;
        public IntPtr data2;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);
    private static readonly TimeSpan PendingTextRescueThreshold = TimeSpan.FromMilliseconds(250);
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaCloak = 13;
    /// <summary>
    /// The logical size every row asks <see cref="IconFor"/> for. Named because it is also the
    /// key a shell thumbnail is cached under, so the drag preview has to ask for the same size to
    /// find what the row already resolved.
    /// </summary>
    private const int RowIconLogicalSize = 96;

    private const int RowIconDecodePixels = 48;
    private const int PreviewImageDecodePixels = 900;
    private const int MaxCachedRasterImages = 256;
    private const int TextPreviewCharacterLimit = 80_000;
    // A screenful. Three was enough only because appending used to cascade through the layout pass
    // and fill the rest before anyone looked; now that batches yield, three rows is what the open
    // actually shows, and the rest arrives a dispatcher turn later. Rendering what fits costs a few
    // milliseconds and is the difference between an open that looks finished and one that fills in.
    // Entries, not rows: date headers take some of them, so this has to run ahead of the screenful
    // it is meant to fill. Dropping it to 8 to match the first-paint query built only 7 rows, left
    // the list short of a screen, and doubled the open while it waited for the next batch.
    private const int InitialRenderEntryBatch = 12;
    private const int DeferredRenderEntryBatch = 36;
    // Rows a concealed palette is allowed to keep materialized. Sits well above what an ordinary
    // open builds (the initial batch plus enough deferred batches to make the list scrollable,
    // ~48 rows), so only a genuinely deep scroll pays a rebuild on the next open.
    private const int ConcealedRowReclaimThreshold = InitialRenderEntryBatch + (3 * DeferredRenderEntryBatch);
    private const int InitialSummaryFirstPaintLimit = 8;
    private const int DebugOpenSurfaceMaxAttempts = 80;
    private const long SummaryPreloadMaximumBytes = 2L * 1024 * 1024;
    private static readonly TimeSpan WindowsHistoryImportMinimumInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WindowsHistoryImportAfterShowDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ClipboardDuplicateBurstWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PaletteSessionKeepAlive = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<string, ImageSource> SvgImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> SvgTextCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SvgCacheGate = new();
    private static readonly RecentImageCache RasterImageCache = new(MaxCachedRasterImages);
    private static readonly object RasterImageCacheGate = new();

    // Kept apart from the row-icon cache because the sizes are nothing alike: a 48px icon is a few
    // KB and hundreds are wanted, a 900px preview is a megabyte or two and only the last handful
    // looked at are. Twelve covers stepping up and down a run of screenshots, which is the case
    // that used to decode from disk every single time.
    private const int MaxCachedPreviewImages = 12;
    private static readonly RecentImageCache PreviewImageCache = new(MaxCachedPreviewImages);
    private static System.Drawing.Rectangle _cachedMouseScreenWorkingArea;
    private static bool _hasCachedMouseScreenWorkingArea;

    private readonly ClipShellSettings _settings = ClipShellSettings.Load();
    private readonly ClipboardHistoryStore _store;
    private readonly PeriodicWorkThrottle _windowsHistoryImportThrottle = new(WindowsHistoryImportMinimumInterval);
    private readonly ClipboardCaptureBurstGate _clipboardCaptureBurstGate = new(ClipboardDuplicateBurstWindow);
    private readonly SemaphoreSlim _clipboardPersistGate = new(1, 1);
    private readonly object _clipboardPersistTasksGate = new();
    private readonly List<Task> _clipboardPersistTasks = [];
    private readonly ClipUpdateService _updates = new();
    private readonly Dictionary<string, Border> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeletedItemUndoBuffer _deleteUndo = new();
    private static readonly TimeSpan DefaultToastDuration = TimeSpan.FromSeconds(2.4);
    private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new() { Interval = DefaultToastDuration };
    private readonly System.Windows.Threading.DispatcherTimer _hotkeyRetryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Threading.DispatcherTimer _outsideClickTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? _mouseHookProc;
    private readonly System.Windows.Threading.DispatcherTimer _clipboardSettleTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly System.Windows.Threading.DispatcherTimer _startupUpdateCheckTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly System.Windows.Threading.DispatcherTimer _updateCheckTimer = new() { Interval = TimeSpan.FromHours(4) };
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly System.Windows.Threading.DispatcherTimer _historyPreloadTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly System.Windows.Threading.DispatcherTimer _paletteSessionExitTimer = new() { Interval = PaletteSessionKeepAlive };
    private bool _settingsCachesWarmed;
    private IReadOnlyList<ClipboardHistoryItem> _allItems = [];
    private IReadOnlyList<ClipboardHistoryItem>? _renderedVisibleItems;
    private ClipboardHistoryItem? _selected;
    private ClipboardHistoryItem? _pendingTextClipboardItem;
    private DateTime _pendingTextClipboardItemAt;
    private uint _lastClipboardSequenceNumber;
    private HwndSource? _source;
    private bool _openHotkeyRegistered;
    private bool _debugLogHotkeyRegistered;
    private bool _openHotkeyUnavailable;
    private bool _openHotkeyConflictNotified;
    private bool _debugLogHotkeyUnavailable;
    private bool _openOverrideRegistered;
    private string? _activeOpenOverrideApp;
    private string? _activeOpenOverrideHotkey;
    private string _kindFilter = "all";
    private string _dateFilter = "all";
    private string _fileFilter = "all";
    private int _previewToken;
    private bool _suppressDeactivate;
    private bool _updateCheckInProgress;
    private string? _promptedUpdateVersion;
    private bool _itemsDirtySinceRender = true;
    private bool _historySummariesPreloaded;
    private bool _historyPreloadInProgress;
    private bool _historyImportInProgress;
    private Task<(IReadOnlyList<ClipboardHistoryItem> Items, long QueryElapsedMs)>? _recentFirstPaintPreloadTask;
    private bool _paletteRequested;
    private bool _paletteOpen;
    private bool _paletteNoActivate;
    private bool _paletteSessionExitRequested;
    private bool _isClosing;
    private bool _chromeIconsReady;
    private bool _appHeaderIconReady;
    private int _renderGeneration;
    private int _loadGeneration;
    private System.Windows.Threading.DispatcherOperation? _backgroundFullRefreshOperation;
    private IReadOnlyList<(string? Header, ClipboardHistoryItem? Item)> _deferredRenderEntries = [];
    private int _deferredRenderIndex;
    private int _deferredRenderGeneration;
    private string _deferredRenderReason = string.Empty;
    private Stopwatch? _deferredRenderWatch;
    private string? _debugOpenSurface;
    private int _debugOpenSurfaceAttempts;
    private IntPtr _returnFocusHwnd;
    private IntPtr _returnFocusChildHwnd;
    private AutomationElement? _returnFocusElement;
    private string _returnFocusElementSummary = "none";
    private string? _returnFocusValueBefore;
    private bool _returnFocusCommitsPasteWithEnter;
    private bool _returnFocusCouldNeedNoActivate;
    private ClipboardHistoryItem? _menuItem;

    /// <summary>The action menu's actionable rows in display order, so arrow keys can walk them.</summary>
    private readonly List<(Border Row, MenuAction Action)> _menuRows = [];
    private int _menuHighlightIndex = -1;
    private bool _expandedImagePanning;
    private System.Windows.Point _expandedImageLastPoint;
    private System.Windows.Point _expandedImageDownPoint;
    private bool _expandedImageMoved;
    private bool _expandedImageDownOnImage;
    private double _expandedImageZoom = 1.0;
    private double _expandedImageNaturalWidth = 1.0;
    private double _expandedImageNaturalHeight = 1.0;
    private Rect _expandedRestoreBounds;
    private CornerRadius _expandedRestoreCornerRadius;
    private bool _expandedWindowResized;
    private Border? _inlineModalOverlay;
    private Border? _settingsOverlay;
    private SettingsWindow? _hostedSettings;
    private bool _settingsOverlayKeepPaletteOnClose;
    private Border? _prewarmedSettingsOverlay;
    // WebView2 is an HWND with its own airspace — no WPF ZIndex can paint over it, so the browser
    // pane is hidden while the hosted settings overlay is up and restored when it closes.
    private bool _previewHiddenForSettings;
    private SettingsWindow? _prewarmedSettings;
    private bool _prewarmedSettingsReady;
    private bool _settingsPrewarmQueued;
    private bool _windowsHistoryImportAfterShowQueued;
    private FrameworkElement? _htmlPreview;
    private Action<System.Drawing.Color>? _setHtmlPreviewBackground;
    private string? _currentPreviewImagePath;
    private string? _currentPreviewPdfPath;
    private ClipUpdateStatus _lastUpdateStatus = ClipUpdateStatus.NotChecked(ClipUpdateService.CurrentVersion);
    public bool KeepOpenForDebug { get; set; }
    public string? DebugInitialSearch { get; set; }
    public int? DebugAutoConcealMs { get; set; }
    public bool DebugOpenSettings { get; set; }
    public string? DebugOpenSurface { get; set; }
    public string? TrayStartupAction { get; set; }
    public bool PaletteSessionMode { get; set; }
    public bool PaletteSessionStartHidden { get; set; }
    public bool KeepWarmSession { get; set; }
    public bool OpenTestOffscreen
    {
        get => _openTestOffscreen;
        set
        {
            _openTestOffscreen = value;
            MeasuringOffScreen = value;
        }
    }

    private bool _openTestOffscreen;

    /// <summary>
    /// Set while a harness is driving the window off the side of the desktop. Static because the
    /// WebView2 environment is created once for the process, before any window asks for it.
    /// </summary>
    internal static bool MeasuringOffScreen { get; private set; }

    /// <summary>
    /// Set when a harness intends to photograph the preview pane. Chromium produces no frames for a
    /// window it thinks nobody can see, so an off-screen capture comes back empty (a 0-byte PNG is
    /// how this was found) unless occlusion handling is turned off. Kept separate from
    /// <see cref="MeasuringOffScreen"/> because these flags cost time — an un-throttled browser
    /// competes for the UI thread — so they belong on a verification run, never a timing one.
    /// </summary>
    internal static bool CapturingPreview { get; set; }
    internal ClipUpdateStatus LastUpdateStatus => _lastUpdateStatus;
    internal AppIconPreference AppIconPreference => _settings.AppIcon;
    internal event Action<AppIconPreference>? AppIconChanged;
    internal event Action<string>? UserNotificationRequested;
    internal event Action<string>? UpdateNotification;

    public MainWindow()
    {
        _store = new ClipboardHistoryStore(contentRootPath: _settings.EffectiveClipboardFolderPath(), enableLoadMaintenance: false);
        InitializeComponent();
        TextPreview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnTextPreviewScrollChanged));
        RenderOptions.SetClearTypeHint(Shell, ClearTypeHint.Auto);
        FaviconCache.Warm();
        _htmlPreviewIdleTimer.Tick += OnHtmlPreviewIdle;
        ApplyTheme(_settings.Theme, save: false);
        UpdateFooterHotkeyHints();
        Opacity = 0;
        TitleText.Cursor = System.Windows.Input.Cursors.IBeam;
        TitleText.ToolTip = "Double-click to rename";
        TitleText.MouseLeftButtonDown += OnTitleTextMouseLeftButtonDown;
        TitleText.Foreground = (WpfBrush)FindResource("Text");
        SubTitleText.Foreground = (WpfBrush)FindResource("Muted");
        TitleText.MouseEnter += (_, _) => TitleText.Foreground = (WpfBrush)FindResource("Accent");
        TitleText.MouseLeave += (_, _) => TitleText.Foreground = (WpfBrush)FindResource("Text");
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _hotkeyRetryTimer.Tick += (_, _) => EnsureHotkeyRegistered("retry");
        _outsideClickTimer.Tick += (_, _) => HideIfMousePressedOutsidePalette();
        _clipboardSettleTimer.Tick += (_, _) =>
        {
            _clipboardSettleTimer.Stop();
            SavePendingTextClipboardItem();
        };
        _startupUpdateCheckTimer.Tick += (_, _) =>
        {
            _startupUpdateCheckTimer.Stop();
            if (_settings.CheckForUpdatesOnStartup)
            {
                _ = CheckForUpdatesAsync(showToastWhenCurrent: false);
            }
        };
        _updateCheckTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(showToastWhenCurrent: false);
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            QueueLoadItems(selectFirst: false, reason: "search");
        };
        _historyPreloadTimer.Tick += (_, _) =>
        {
            _historyPreloadTimer.Stop();
            PreloadHistorySummariesIfCheap();
        };
        _paletteSessionExitTimer.Tick += (_, _) => ExitPaletteSessionIfIdle();
    }

    public void InitializeShell()
    {
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WndProc);
            var hwnd = new WindowInteropHelper(this).Handle;
            // Measured, not assumed: DWMWA_WINDOW_CORNER_PREFERENCE DOES clip a layered window,
            // and it is the only thing that rounds the acrylic. The blur is painted by the
            // compositor across the whole window rect and ignores WPF's per-pixel alpha, so the
            // Shell's own CornerRadius cannot round it — a rounded Shell over square blur just
            // leaves four wedges of tint outside the arc. SetWindowRgn is silently ignored on a
            // layered window (it returns success and changes nothing; verified on this machine).
            // DWM's clip is also antialiased, which a GDI region would not have been.
            ApplyRoundedWindowCorners(hwnd);
            //
            // The constructor's theme pass ran before the hwnd existed, so it resolved to the
            // opaque palette; now that the window is real, re-theme so the acrylic blur
            // (if enabled and supported) can actually take.
            ApplyTheme(_settings.Theme, save: false);

            // Landing on a monitor with a different scale makes Windows resize the palette after
            // the move, anchored wherever it decides — which un-centres what was just centred.
            // Re-running the placement once the new size is real is what makes "always centred"
            // true across mixed-DPI monitors. It cannot loop: the palette is already on the
            // cursor's monitor by then, so re-centring there changes no scale.
            DpiChanged += (_, _) =>
            {
                if (IsVisible && !_isMediaFullScreen && !_expandedWindowResized)
                {
                    PositionOnMouseScreen(log: false);
                }
            };

            _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
            var hotkey = false;
            var listener = false;
            if (!PaletteSessionMode)
            {
                hotkey = EnsureHotkeyRegistered("startup");
                listener = AddClipboardFormatListener(hwnd);
                InstallForegroundHook();
            }

            ShellLog.Info($"window initialized hwnd={hwnd} session={PaletteSessionMode} hotkey={hotkey} listener={listener} clipboardSequence={_lastClipboardSequenceNumber} win32={Marshal.GetLastWin32Error()}");
        };

        Loaded += async (_, _) =>
        {
            var showAfterPreRender =
                _paletteRequested ||
                (PaletteSessionMode && !PaletteSessionStartHidden) ||
                !string.IsNullOrWhiteSpace(DebugOpenSurface) ||
                (KeepOpenForDebug && !DebugOpenSettings);
            if (showAfterPreRender || (PaletteSessionMode && KeepWarmSession))
            {
                StartRecentFirstPaintPreload();
            }

            MoveOffscreen();
            Opacity = 1;

            // Focus the search box during the one moment at startup when the window is really
            // shown, off screen and about to be hidden again. The first focus of a TextBox costs
            // about 100ms while WPF brings up the text services behind it, and that cost lands on
            // whichever open happens first unless it is paid here. It has to happen while the
            // window is visible: focusing a control in a hidden window returns immediately and
            // initialises nothing, which is what an earlier attempt at this measured (focusMs=0).
            SearchBox.Focus();

            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            ConcealPalette("startup");
            WarmMouseScreenCache();
            PositionOnMouseScreen(log: false);

            ShellLog.Info("window pre-rendered while hidden");
            if (showAfterPreRender)
            {
                _ = Dispatcher.BeginInvoke(new Action(() => ShowPalette()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            else
            {
                WarmFirstPaintWhileHidden();
            }

            if (!string.IsNullOrWhiteSpace(TrayStartupAction))
            {
                _ = Dispatcher.BeginInvoke(new Action(RunTrayStartupAction), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            if (!PaletteSessionMode && _settings.CheckForUpdatesOnStartup)
            {
                _startupUpdateCheckTimer.Start();
            }

            if (!PaletteSessionMode)
            {
                ApplyUpdateCheckSchedule();
                if (DebugOpenSettings)
                {
                    WarmSettingsCachesSoon();
                    PrewarmHostedSettingsSoon();
                }

                _ = Task.Run(() => ClipboardSharePayload.CleanupStaleTemporaryFilesIfDue());
            }

            if (DebugOpenSettings && !PaletteSessionMode)
            {
                _ = Dispatcher.BeginInvoke(new Action(OpenSettingsForDebug), System.Windows.Threading.DispatcherPriority.SystemIdle);
            }
        };

        Closing += (_, _) =>
        {
            _isClosing = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeyRetryTimer.Stop();
            _searchTimer.Stop();
            _historyPreloadTimer.Stop();
            _paletteSessionExitTimer.Stop();
            _startupUpdateCheckTimer.Stop();
            _updateCheckTimer.Stop();
            if (_openHotkeyRegistered)
            {
                var released = UnregisterHotKey(hwnd, OpenHotkeyId);
                ShellLog.Info($"open hotkey unregistered={released} hwnd={hwnd} win32={Marshal.GetLastWin32Error()}");
                _openHotkeyRegistered = false;
            }

            if (_debugLogHotkeyRegistered)
            {
                var released = UnregisterHotKey(hwnd, DebugLogHotkeyId);
                ShellLog.Info($"debug log hotkey unregistered={released} hwnd={hwnd} win32={Marshal.GetLastWin32Error()}");
                _debugLogHotkeyRegistered = false;
            }

            if (_openOverrideRegistered)
            {
                UnregisterHotKey(hwnd, OpenOverrideHotkeyId);
                _openOverrideRegistered = false;
            }

            // The drag preview is its own top-level window and is deliberately not owned by the
            // palette, so nothing else closes it — an undisposed one would keep the process alive
            // past the last visible window.
            _dragPreview?.Dispose();
            _dragPreview = null;

            FlushPendingClipboardPersists();
            if (!PaletteSessionMode)
            {
                UninstallForegroundHook();
                RemoveClipboardFormatListener(hwnd);
            }

            ShellLog.Info("window closing");
        };

        Show();
    }

    private void WarmSettingsCachesSoon()
    {
        if (_settingsCachesWarmed)
        {
            return;
        }

        _settingsCachesWarmed = true;
        _ = Dispatcher.BeginInvoke(
            new Action(SettingsWindow.WarmCaches),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void PrewarmHostedSettingsSoon()
    {
        if (_settingsPrewarmQueued ||
            _prewarmedSettingsOverlay is not null ||
            PaletteSessionMode ||
            _isClosing ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _settingsPrewarmQueued = true;
        _ = Dispatcher.BeginInvoke(
            new Action(PrewarmHostedSettings),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void PrewarmHostedSettings()
    {
        _settingsPrewarmQueued = false;
        if (_isClosing ||
            _settingsOverlay is not null ||
            _prewarmedSettingsOverlay is not null ||
            _paletteOpen ||
            Opacity > 0 ||
            IsHitTestVisible ||
            Shell.Child is not Grid host)
        {
            return;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var settings = CreateSettingsWindow();
            var overlay = CreateHostedSettingsOverlay(settings);
            overlay.Opacity = 0;
            overlay.IsHitTestVisible = false;
            host.Children.Add(overlay);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _prewarmedSettings = settings;
                _prewarmedSettingsOverlay = overlay;
                _prewarmedSettingsReady = true;
                ShellLog.Info($"settings prewarmed elapsedMs={watch.ElapsedMilliseconds}");
                SchedulePrewarmedSettingsExpiry(overlay);
            }), System.Windows.Threading.DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            ClearPrewarmedHostedSettings();
            ShellLog.Error(ex, "settings prewarm failed");
        }
    }

    private void ClearPrewarmedHostedSettings()
    {
        if (_prewarmedSettingsOverlay?.Parent is System.Windows.Controls.Panel parent)
        {
            parent.Children.Remove(_prewarmedSettingsOverlay);
        }

        _prewarmedSettingsOverlay = null;
        _prewarmedSettings = null;
        _prewarmedSettingsReady = false;
        _settingsPrewarmQueued = false;
    }

    private void SchedulePrewarmedSettingsExpiry(Border overlay)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(2.5)).ContinueWith(_task =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_settingsOverlay is null &&
                    ReferenceEquals(_prewarmedSettingsOverlay, overlay) &&
                    !_paletteOpen &&
                    Opacity == 0)
                {
                    ClearPrewarmedHostedSettings();
                    ShellLog.Info("settings prewarm cleared reason=idle");
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    public void ShowPalette(bool loadItems = true)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _paletteRequested = true;
        _paletteOpen = true;
        _paletteSessionExitTimer.Stop();
        var watch = Stopwatch.StartNew();
        var ownHwnd = new WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != ownHwnd)
        {
            CaptureReturnFocus(foreground);
        }

        BenchMarks.Mark("return-focus");
        _paletteNoActivate = _returnFocusCouldNeedNoActivate && ShouldShowPaletteWithoutActivation(_returnFocusHwnd, _returnFocusElement);
        ApplyNoActivatePaletteStyle(_paletteNoActivate);
        BenchMarks.Mark("no-activate-decided");

        if (!IsVisible)
        {
            // Cloak before the surface ever exists, so even the first Show() of the session
            // presents nothing until the reveal below — the OS never gets a blank frame to flash.
            _windowCloaked = TryCloakPaletteWindow(true);
            Show();
        }

        BenchMarks.Mark("window-shown");

        Opacity = 0;
        IsHitTestVisible = false;
        if (OpenTestOffscreen)
        {
            // The harness measures with the window parked off screen; Windows renders it all the same.
            MoveOffscreen();
        }
        else
        {
            PositionOnMouseScreen();
        }

        BenchMarks.Mark("positioned");
        EnsureAppHeaderIcon();
        EnsureChromeIcons();
        RefreshCapturePausedBadge();
        BenchMarks.Mark("chrome-icons");

        // Fill the list before the window is allowed to be seen. Showing first and loading after
        // put an empty column on screen for the ~60ms it took the rows to arrive — the frame was
        // up, with the search box, the filters and the preview all painted, and the clipboard list
        // simply blank. Nothing here is faster than it was; the window just no longer appears
        // before it has anything to say.
        if (loadItems && (_itemsDirtySinceRender || _rows.Count == 0))
        {
            // Re-pick the top item when nothing was chosen by hand: a clip arriving while the
            // palette was closed is the reason it is being opened, so that is what should be
            // selected — not whatever the startup warm-up happened to land on. A selection the
            // user made themselves is left alone, as it always was.
            QueueLoadItems(selectFirst: _selected is null || _selectionIsAutomatic, reason: "show-refresh");
        }
        else if (loadItems && _selected is null)
        {
            // The rows can already exist at the very first open, because they are rendered while
            // the window is still hidden at startup. That skips the reload above — and the reload
            // is what used to pick the first item.
            SelectInitialItemIfNeeded(selectFirst: true, visibleItems: FilteredItems(), defer: false);
        }

        // Adding the rows is not the same as having them on screen: they are in the tree but have
        // not been measured or arranged, so a window made visible in the same breath still shows an
        // empty column for a frame. Laying out here, while still transparent, is what makes the
        // first visible frame the finished one.
        UpdateLayout();
        BenchMarks.Mark("laid-out");

        Opacity = 1;
        IsHitTestVisible = true;
        if (_windowCloaked)
        {
            // Uncloak at Loaded priority: that runs right after the render pass for the layout
            // done above, so the first frame the compositor presents is the finished one —
            // rows, chrome and position all current. Uncloaking synchronously here would show
            // the previous session's surface for one frame instead.
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                // A conceal can land between the show and this callback (fast escape);
                // uncloaking then would leave the concealed window in the wrong state.
                if (_paletteOpen && TryCloakPaletteWindow(false))
                {
                    _windowCloaked = false;
                    ReassertBackdropAfterReveal();
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        BenchMarks.Mark("shown");
        if (!OpenTestOffscreen && ShouldActivatePaletteWindow(_paletteNoActivate))
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ActivatePaletteWindow(ownHwnd)), System.Windows.Threading.DispatcherPriority.Input);
        }
        else if (OpenTestOffscreen)
        {
            // Off screen there is no foreground to take, but typing has to land in the search box
            // for the window to count as open, and the wait for it is part of what is being timed.
            BenchMarks.Mark("focus-queued");
            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    BenchMarks.Mark("focus-start");
                    SearchBox.Focus();
                    BenchMarks.Mark("focus-searchbox");
                    Keyboard.Focus(SearchBox);
                    BenchMarks.Mark("focused");
                }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        StartOutsideClickWatch();

        // Closing the palette tears down the WebView2, so an HTML, code or media preview is gone
        // by the time it reopens. Without re-rendering, reopening on the same item shows an empty
        // pane until the user selects something else.
        if (_selected is not null && PreviewAlreadyShowing(_selected))
        {
            BenchMarks.Mark("preview-ready");
            ShellLog.Info($"preview reused id={_selected.Id}");
        }
        else if (_selected is not null)
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => RenderPreview(_selected)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        ShellLog.Info($"palette shown elapsedMs={watch.ElapsedMilliseconds} selected={_selected?.Id ?? "none"} rows={_rows.Count} dirty={_itemsDirtySinceRender} noActivate={_paletteNoActivate}");

        if (loadItems)
        {
            ScheduleDebugInitialSearch();
            ScheduleDebugOpenSurface();
            WarmOfficePreviewsInBackground();
        }

        if (loadItems && !PaletteSessionMode)
        {
            QueueWindowsHistoryImportAfterShow();
            PromptForKnownUpdate();
        }

        if (loadItems)
        {
            ScheduleDebugAutoConceal();
        }
    }

    public void HandleExternalShowPaletteSignal()
    {
        var action = TrayActionRequest.Consume();
        if (string.IsNullOrWhiteSpace(action))
        {
            ShowPalette();
            return;
        }

        RunTrayAction(action);
    }

    private void RunTrayStartupAction()
    {
        var action = TrayStartupAction;
        TrayStartupAction = null;
        if (!string.IsNullOrWhiteSpace(action))
        {
            RunTrayAction(action);
        }
    }

    private void RunTrayAction(string action)
    {
        switch (NormalizeTrayAction(action))
        {
            case "settings":
                ShowPalette();
                OpenSettingsFromTray();
                break;
            case "check-updates":
                ShowPalette();
                CheckForUpdatesFromTray();
                break;
            case "save-log":
                WriteDebugSnapshot("tray");
                ShowToast("Log snapshot saved");
                break;
            default:
                ShowPalette();
                break;
        }
    }

    private static string NormalizeTrayAction(string action) =>
        action.Trim().ToLowerInvariant();

    internal static bool ShouldActivatePaletteWindow(bool noActivate) => !noActivate;

    private void QueueWindowsHistoryImportAfterShow()
    {
        if (_windowsHistoryImportAfterShowQueued)
        {
            return;
        }

        _windowsHistoryImportAfterShowQueued = true;
        _ = Task.Delay(WindowsHistoryImportAfterShowDelay).ContinueWith(_ =>
        {
            if (_isClosing)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _windowsHistoryImportAfterShowQueued = false;
                    if (_isClosing || !_paletteOpen)
                    {
                        return;
                    }

                    _ = ImportWindowsClipboardHistoryAsync("show", refreshVisible: true);
                }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }, TaskScheduler.Default);
    }

    private void ScheduleDebugInitialSearch()
    {
        if (string.IsNullOrWhiteSpace(DebugInitialSearch))
        {
            return;
        }

        var text = DebugInitialSearch;
        DebugInitialSearch = null;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            SearchBox.Text = text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            _searchTimer.Stop();
            QueueLoadItems(selectFirst: false, reason: "search");
            ShellLog.Info($"debug search applied queryLength={text.Length}");
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ScheduleDebugOpenSurface()
    {
        if (string.IsNullOrWhiteSpace(DebugOpenSurface))
        {
            return;
        }

        _debugOpenSurface = DebugOpenSurface.Trim();
        DebugOpenSurface = null;
        _debugOpenSurfaceAttempts = 0;
        QueueDebugOpenSurface();
    }

    private void QueueDebugOpenSurface(int delayMs = 40)
    {
        if (delayMs <= 0)
        {
            _ = Dispatcher.BeginInvoke(new Action(TryOpenDebugSurface), System.Windows.Threading.DispatcherPriority.ContextIdle);
            return;
        }

        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(TryOpenDebugSurface), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }, TaskScheduler.Default);
    }

    private void TryOpenDebugSurface()
    {
        if (string.IsNullOrWhiteSpace(_debugOpenSurface) || _isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        var surface = NormalizeDebugSurface(_debugOpenSurface);
        var item = FindDebugSurfaceItem(surface);
        if (item is null)
        {
            if (++_debugOpenSurfaceAttempts < DebugOpenSurfaceMaxAttempts)
            {
                QueueDebugOpenSurface();
                return;
            }

            ShellLog.Info($"debug surface skipped surface={_debugOpenSurface} reason=no-item");
            _debugOpenSurface = null;
            return;
        }

        _debugOpenSurface = null;
        ShellLog.Info($"debug surface opening surface={surface} item={item.Id}");
        switch (surface)
        {
            case "rename":
                RenameItem(item);
                break;
            case "edit-text":
                EditText(item);
                break;
            case "open-with":
                OpenWith(item);
                break;
            default:
                ShellLog.Info($"debug surface skipped surface={surface} reason=unknown");
                break;
        }
    }

    private ClipboardHistoryItem? FindDebugSurfaceItem(string surface)
    {
        foreach (var item in DebugSurfaceCandidates())
        {
            var fullItem = _store.GetItem(item.Id) ?? item;
            if (surface switch
                {
                    "edit-text" => fullItem.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link,
                    "open-with" => HasOpenWithTarget(fullItem),
                    _ => true,
                })
            {
                return fullItem;
            }
        }

        return null;
    }

    private IEnumerable<ClipboardHistoryItem> DebugSurfaceCandidates()
    {
        if (_selected is not null)
        {
            yield return _selected;
        }

        foreach (var item in _allItems)
        {
            if (_selected is null || !string.Equals(item.Id, _selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                yield return item;
            }
        }
    }

    private static string NormalizeDebugSurface(string surface)
    {
        return surface.Trim().ToLowerInvariant() switch
        {
            "edit" or "text" or "edit-text" => "edit-text",
            "open" or "openwith" or "open-with" => "open-with",
            "rename" => "rename",
            var normalized => normalized,
        };
    }

    private static bool HasOpenWithTarget(ClipboardHistoryItem item)
    {
        var targetPath = item.Kind == ClipboardItemKind.Image ? item.AssetPath : item.FilePaths.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(targetPath) && (File.Exists(targetPath) || Directory.Exists(targetPath));
    }

    private void ScheduleDebugAutoConceal()
    {
        if (DebugAutoConcealMs is not int delayMs || delayMs <= 0)
        {
            return;
        }

        DebugAutoConcealMs = null;
        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => ConcealPalette("debug-auto-conceal")), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void ActivatePaletteWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, ShowWindowRestore);
        }
        else
        {
            ShowWindow(hwnd, ShowWindowShow);
        }

        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        BenchMarks.Mark("focused");
    }

    public void CheckForUpdatesFromTray()
    {
        _ = CheckForUpdatesAsync(showToastWhenCurrent: true, promptIfAvailable: true, nativeNotify: true);
    }

    public void InstallKnownUpdateFromTray()
    {
        if (IsUpdateAvailable(_lastUpdateStatus))
        {
            _ = InstallUpdateAsync(_lastUpdateStatus);
            return;
        }

        CheckForUpdatesFromTray();
    }

    internal void ConcealForOpenTest() => ConcealPalette("open-test");

    // What the open-latency harness needs to see from outside. It decides what "open" means; this
    // only reports the state, so the rule lives in one place next to the numbers it produces.
    internal bool BenchWindowShown => IsVisible && Opacity >= 1 && !_windowCloaked;

    internal bool BenchSearchFocused => SearchBox.IsFocused;

    internal int BenchRenderedRows => _rows.Count;

    internal int BenchTotalItems => _allItems.Count;

    internal IReadOnlyList<ClipboardHistoryItem> BenchItems => _allItems;

    internal ClipboardHistoryItem? BenchSelected => _selected;

    internal void BenchSelect(ClipboardHistoryItem item) => SelectItem(item, "bench");

    /// <summary>
    /// Puts the window back to the state a real reopen starts from, so run five measures what run
    /// one did. Concealing keeps the focus and the rendered rows, which no reopen in real use gets:
    /// focus has moved to whatever the user was typing in, and something was almost always copied
    /// in between — that is the reason the palette is being opened — which is what makes the list
    /// stale and forces the refresh. Without this a warm run measures only Show(), and reports a
    /// number no user will ever see.
    /// </summary>
    internal void BenchResetForRun(bool clipboardChanged)
    {
        Keyboard.ClearFocus();
        System.Windows.Input.FocusManager.SetFocusedElement(this, null);

        if (clipboardChanged)
        {
            _itemsDirtySinceRender = true;
        }
    }

    internal void BenchOpenSettings() => OpenSettingsFromTray();

    internal bool BenchSettingsOpen => _settingsOverlay is not null;

    /// <summary>
    /// Puts the list back to the state a fresh open should have: no leftover search text, the
    /// list scrolled to the top, and the top item selected again.
    ///
    /// This happens on conceal rather than on show because <see cref="ShowPalette"/> has ~20
    /// callers, many of which are a re-show inside one flow (returning from settings, from
    /// picture-in-picture, from an inline editor) rather than the user opening the palette.
    /// Concealing is the one thing that always separates one visit from the next.
    /// </summary>
    private void ResetPaletteViewForNextOpen()
    {
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = string.Empty;
            SearchBox.CaretIndex = 0;
            // OnSearchChanged just restarted the debounce; the reload on the next open covers it.
            _searchTimer.Stop();
            // The rows still hold the filtered set, so the next open has to re-query.
            _itemsDirtySinceRender = true;
        }

        // Re-pick the top item on the next open. Without this the list shows the top while the
        // selection (and preview) sit on whatever row was picked last time, possibly far below.
        // A hand-picked row also has to mark the rows dirty, or ShowPalette skips the reload —
        // and the reload is what re-selects.
        if (!_selectionIsAutomatic)
        {
            _itemsDirtySinceRender = true;
            _selectionIsAutomatic = true;
        }

        ListScroll.ScrollToTop();
    }

    /// <summary>
    /// Whether enough rows are materialized that reclaiming them is worth re-rendering the
    /// initial batch on the next open. Marking the items dirty is what triggers that rebuild;
    /// under the threshold the rows are left alone, because reusing them is exactly what makes a
    /// warm reopen instant.
    /// </summary>
    internal static bool ShouldReclaimRowsOnConceal(int materializedRows) =>
        materializedRows > ConcealedRowReclaimThreshold;

    /// <param name="resetView">
    /// False only for the conceal-paste-reopen of a paste-and-stay, where this is one visit to the
    /// palette rather than the end of one: clearing the search and re-picking the top row would
    /// throw away the very list the next paste is being aimed at.
    /// </param>
    private void ConcealPalette(string reason, bool resetView = true)
    {
        StopOutsideClickWatch();
        if (resetView)
        {
            ResetPaletteViewForNextOpen();
            // A deep scroll materializes hundreds of rows, and a concealed palette would keep that
            // whole visual tree alive until something else re-rendered. Going dirty makes the next
            // open rebuild just the initial batch — the same path a fresh open takes.
            if (ShouldReclaimRowsOnConceal(_rows.Count))
            {
                _itemsDirtySinceRender = true;
            }
        }

        _paletteOpen = false;
        Opacity = 0;
        IsHitTestVisible = false;
        // Off screen as well as cloaked: cloaking removes the pixels but not the HWND, and an
        // invisible topmost window parked over the desktop could still swallow hit-testing.
        MoveOffscreen();
        if (TryCloakPaletteWindow(true))
        {
            _windowCloaked = true;
            // Hide() used to hand activation back to the previous window as a side effect.
            // Cloaking keeps the window "visible", so give focus back deliberately — but only
            // when the palette actually holds it (escape/settings-close); after an outside
            // click or a no-activate open, focus already sits where it belongs.
            if (GetForegroundWindow() == new WindowInteropHelper(this).Handle)
            {
                RestoreReturnFocus();
            }
        }
        else
        {
            // Fallback to the old behavior: truly hide the window. A topmost WPF window left at
            // Opacity=0 can leave a stale surface on a secondary monitor (DWM compositing glitch)
            // until something forces a repaint.
            Hide();
        }

        // A video or song must not keep playing out of a hidden window — and when the palette is
        // concealed for picture-in-picture, the palette's copy must stop before the mini window's
        // copy starts, or both play at once.
        PauseHtmlPreviewMedia();

        // Don't tear the browser down the instant the palette closes. Rebuilding it costs a
        // noticeable pause on the next video, audio, code or HTML preview, and the palette is
        // usually reopened within seconds. Keep it warm briefly, then release it if it goes unused.
        _htmlPreviewIdleTimer.Stop();
        _htmlPreviewIdleTimer.Start();

        ShellLog.Info($"palette concealed reason={reason}");
        if (PaletteSessionMode && KeepWarmSession)
        {
            _paletteSessionExitTimer.Stop();
            ShellLog.Info($"palette session kept resident reason={reason}");
            return;
        }

        if (PaletteSessionMode && !string.Equals(reason, "startup", StringComparison.OrdinalIgnoreCase))
        {
            if (_paletteSessionExitRequested || _isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (string.Equals(reason, "debug-auto-conceal", StringComparison.OrdinalIgnoreCase))
            {
                ExitPaletteSession(reason);
                return;
            }

            _paletteSessionExitTimer.Stop();
            _paletteSessionExitTimer.Start();
            ShellLog.Info($"palette session kept warm reason={reason} keepAliveMs={(int)PaletteSessionKeepAlive.TotalMilliseconds}");
        }
        else if (PaletteSessionMode && string.Equals(reason, "startup", StringComparison.OrdinalIgnoreCase))
        {
            _paletteSessionExitTimer.Stop();
            _paletteSessionExitTimer.Start();
            ShellLog.Info($"palette session startup guard keepAliveMs={(int)PaletteSessionKeepAlive.TotalMilliseconds}");
        }
    }

    private void ExitPaletteSessionIfIdle()
    {
        _paletteSessionExitTimer.Stop();
        if (!PaletteSessionMode || KeepWarmSession || _paletteOpen || _isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        ExitPaletteSession("idle-timeout");
    }

    private void ExitPaletteSession(string reason)
    {
        _paletteSessionExitRequested = true;
        _paletteSessionExitTimer.Stop();
        ShellLog.Info($"palette session exiting reason={reason}");
        _ = Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Watches for the click that dismisses the palette.
    ///
    /// This used to poll <see cref="Forms.Control.MouseButtons"/> every 50ms, which only sees a
    /// button that happens to be physically held at the moment of a tick. A normal click is held
    /// for well under that on a fast hand, and the dispatcher timer slips further whenever the
    /// palette is busy building previews — so the first click outside was regularly missed and it
    /// took a second one to dismiss. A low-level mouse hook gets every button-down, so one click
    /// is always enough. The timer stays as a fallback if the hook cannot be installed.
    /// </summary>
    private void StartOutsideClickWatch()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        try
        {
            _mouseHookProc = OnLowLevelMouse;
            _mouseHook = SetWindowsHookEx(WhMouseLowLevel, _mouseHookProc, GetModuleHandle(null), 0);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "outside-click hook install failed");
            _mouseHook = IntPtr.Zero;
        }

        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHookProc = null;
            ShellLog.Info("outside-click hook unavailable, polling instead");
            _outsideClickTimer.Start();
        }
    }

    private void StopOutsideClickWatch()
    {
        _outsideClickTimer.Stop();
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseHookProc = null;
    }

    private IntPtr OnLowLevelMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        // A low-level hook runs on every mouse message for the whole desktop: do the cheapest
        // possible check here and hand the real work to the dispatcher, or the pointer stutters.
        if (code >= 0 && IsButtonDownMessage((int)wParam))
        {
            // Read the cursor with GetCursorPos rather than trusting MSLLHOOKSTRUCT.pt. The hook
            // struct's point arrives in a different DPI space than GetWindowRect reports for our
            // own window: measured on the 150%-scaled second monitor, a click at 1044,-996 landed
            // in the struct as 696,-664 — exactly divided by the scale factor, so it tested as
            // outside a window spanning 851..2051. GetCursorPos answers in the same space as
            // GetWindowRect because both are this process's, so the two never disagree. It is read
            // here, not after the dispatcher hop, so the cursor is still where it was clicked.
            if (GetCursorPos(out var cursor))
            {
                _ = Dispatcher.BeginInvoke(
                    new Action(() => HideIfMousePressedOutsidePalette(cursor.X, cursor.Y)),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsButtonDownMessage(int message) =>
        message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown;

    private void HideIfMousePressedOutsidePalette()
    {
        if (Forms.Control.MouseButtons == Forms.MouseButtons.None)
        {
            return;
        }

        var mouse = Forms.Control.MousePosition;
        HideIfMousePressedOutsidePalette(mouse.X, mouse.Y);
    }

    private void HideIfMousePressedOutsidePalette(int screenX, int screenY)
    {
        if (Opacity <= 0 || !IsHitTestVisible)
        {
            StopOutsideClickWatch();
            return;
        }

        if (KeepOpenForDebug || OpenTestOffscreen || _suppressDeactivate || ActionMenuPopup.IsOpen || IsContextMenuOpen(this))
        {
            return;
        }

        // Compare the click against the window's Win32 rect, in raw screen pixels, the same
        // space the mouse hook reports. PointFromScreen would convert with the DPI of the monitor
        // WPF still thinks the window lives on, so on a differently-scaled second monitor a click
        // on a row landed outside Rect(0,0,ActualWidth,ActualHeight) and dismissed the palette.
        // Same trap PositionOnMouseScreen already avoids: stay in one coordinate space.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        if (screenX < rect.Left || screenX >= rect.Right || screenY < rect.Top || screenY >= rect.Bottom)
        {
            ConcealPalette("outside-click");
            ShellLog.Info($"palette hidden on outside click at {screenX},{screenY} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
        }
    }

    private const int WhMouseLowLevel = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// Renders the rows the first open will show, while the window is still hidden.
    ///
    /// Pre-rendering the window at startup built the frame but left the list empty, so the first
    /// Alt+V still paid for the query and for building every visible row — and building a row is
    /// not cheap on a cold process: the first file row measured 193ms and the first image row 49ms,
    /// because a row resolves its icon by asking the shell for the file type's icon or by decoding
    /// the picture itself. That is most of what a cold open cost.
    ///
    /// Doing it here spends the same work while the machine is idle after login, minutes before
    /// anyone presses anything, and it stays correct because it changes nothing about staleness:
    /// a clip arriving marks the list dirty exactly as before, and the next open re-queries. All
    /// this removes is the case where the first open rebuilds a list that nothing had changed.
    ///
    /// Runs at ApplicationIdle so it cannot delay startup or compete with anything the user does.
    /// </summary>
    private void WarmFirstPaintWhileHidden()
    {
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_isClosing || _paletteOpen || _rows.Count > 0)
                {
                    return;
                }

                try
                {
                    var watch = Stopwatch.StartNew();
                    QueueLoadItems(selectFirst: false, reason: "startup-warm");
                    ShellLog.Info($"first paint warmed while hidden elapsedMs={watch.ElapsedMilliseconds} rows={_rows.Count}");

                    // Select what the first open will land on and render its preview now, so that
                    // open finds the pane already showing the right thing and reuses it. Without
                    // this the first video or code preview of a session costs over a second —
                    // creating the browser, then navigating, in front of the user.
                    SelectInitialItemIfNeeded(selectFirst: true, visibleItems: FilteredItems(), defer: false);

                    // And stand the browser up whether or not that first item needed it, so the
                    // first video, code, HTML or PDF preview of the session does not pay ~810ms to
                    // create it. This was measured costing ~23ms per open and left out on those
                    // grounds; instant previews are worth more than that, and the browser ends up
                    // alive within a few opens anyway the moment any file preview is looked at.
                    WarmHtmlPreviewInBackground();
                }
                catch (Exception ex)
                {
                    // A warm-up that fails must cost nothing but the warm-up: the open path is
                    // unchanged and will do the work itself.
                    ShellLog.Error(ex, "first paint warm failed");
                }
            }),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Stands the preview browser up so the first browser-backed preview does not have to.</summary>
    private void WarmHtmlPreviewInBackground()
    {
        _ = Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                if (_isClosing || _htmlPreviewWarmed)
                {
                    return;
                }

                _htmlPreviewWarmed = true;

                try
                {
                    var watch = Stopwatch.StartNew();
                    var view = (Microsoft.Web.WebView2.Wpf.WebView2)EnsureHtmlPreview();
                    await EnsureWebViewReadyAsync(view);
                    _htmlPreviewWarmReady = true;
                    ShellLog.Info($"html preview warmed elapsedMs={watch.ElapsedMilliseconds}");
                }
                catch (Exception ex)
                {
                    // The preview path creates it on demand anyway; a failed warm costs only itself.
                    ShellLog.Error(ex, "html preview warm failed");
                }
            }),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private bool _htmlPreviewWarmed;
    private bool _htmlPreviewWarmReady;

    /// <summary>
    /// Whether everything the shell warms at startup has finished. A harness has to wait for this
    /// before timing anything: a real user's first open comes minutes after login, so measuring
    /// while the warm-up is still running measures contention nobody experiences.
    /// </summary>
    internal bool BenchWarmupComplete => _rows.Count > 0 && _htmlPreviewWarmReady;

    /// <summary>
    /// Saves a picture of whatever the browser preview pane is currently showing.
    ///
    /// Skipping a redundant re-render is only correct if the pane really is still showing the right
    /// thing, and no timing number can tell you that — a blank pane is very fast. This renders from
    /// the browser itself rather than the screen, so the check costs nobody their display.
    /// </summary>
    /// <summary>
    /// Photographs the palette's own visual tree, off screen.
    ///
    /// "Is anything actually in the window yet" is not a question a stopwatch can answer, and it is
    /// the exact complaint about seeing an empty frame that then fills in. Rendering the tree gives
    /// a frame-by-frame answer without anyone's display being touched.
    ///
    /// Caveat worth knowing before trusting one of these: WebView2 draws into its own child window,
    /// so a browser-backed preview shows up blank here. Use BenchCapturePreviewAsync for that pane.
    /// </summary>
    internal bool BenchCaptureWindow(string path)
    {
        var width = (int)Math.Ceiling(ActualWidth);
        var height = (int)Math.Ceiling(ActualHeight);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var target = new RenderTargetBitmap(
            (int)(width * dpi.DpiScaleX),
            (int)(height * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);

        target.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(path);
        encoder.Save(file);
        return true;
    }

    internal async Task<bool> BenchCapturePreviewAsync(string path)
    {
        if (_htmlPreview is not Microsoft.Web.WebView2.Wpf.WebView2 { CoreWebView2: not null } view)
        {
            return false;
        }

        using var file = File.Create(path);
        await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        return true;
    }

    private void MoveOffscreen()
    {
        Left = SystemParameters.VirtualScreenLeft - Math.Max(ActualWidth, Width) - 100;
        Top = SystemParameters.VirtualScreenTop - Math.Max(ActualHeight, Height) - 100;
    }

    // DWM cloaking is what makes the palette open like Raycast: the window keeps WS_VISIBLE, so
    // WPF keeps its D3D surface alive and rendered while concealed, and the compositor simply
    // stops presenting it. Uncloaking presents the already-finished frame in one compositor
    // flip — no blank surface, no hydration. Hide() (the old way) drops the surface, so every
    // re-show flashed an empty window while WPF re-rendered from scratch.
    private bool _windowCloaked;

    private bool TryCloakPaletteWindow(bool cloak)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var value = cloak ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "cloak toggle failed");
            return false;
        }
    }

    /// <summary>True only while the compositor has actually accepted the acrylic blur for this window.</summary>
    private bool _backdropActive;

    /// <summary>
    /// The ABGR tint the accent policy is currently carrying. Kept so a reveal can re-assert the
    /// exact same glass without re-running the whole theme pass.
    /// </summary>
    private uint _backdropTint;

    /// <summary>
    /// Syncs the acrylic blur with the Translucent background setting. Called from ApplyTheme so
    /// theme changes, the settings toggle, and the post-SourceInitialized re-theme all go through
    /// one path. Before the hwnd exists this resolves to opaque; InitializeShell re-runs the theme
    /// once the window is real.
    ///
    /// Takes the background hex rather than reading the Bg brush, because ApplyTheme has to run
    /// this <em>before</em> SetBrush — the brushes it would read are still the previous theme's.
    /// </summary>
    private void ApplyBackdropPreference(string backgroundHex)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            _backdropActive = false;
            return;
        }

        var wantGlass = _settings.TranslucentBackground && PaletteBackdrop.IsSupported();
        _backdropTint = PaletteBackdrop.GradientColor(backgroundHex, PaletteBackdrop.TintAlpha);
        _backdropActive = wantGlass && PaletteBackdrop.TryApply(hwnd, _backdropTint);
        if (!wantGlass)
        {
            // Toggled off (or unsupported build): make sure a previously-applied blur is gone
            // rather than blurring behind a now-opaque palette.
            PaletteBackdrop.Remove(hwnd);
        }

        // The window is layered either way (AllowsTransparency, see MainWindow.xaml), so the D3D
        // clear colour is always transparent: with the glass on it would sit between the blur and
        // the tints, and with the glass off it would square off the Shell's rounded corners.
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        // Nothing to do for the corners here: DWM's rounding is set once at SourceInitialized and
        // clips the window whether or not the glass is on.
        ShellLog.Info($"backdrop preference applied want={_settings.TranslucentBackground} supported={PaletteBackdrop.IsSupported()} tint={_backdropTint:X8} active={_backdropActive}");
    }

    /// <summary>
    /// Cloak/uncloak keeps the window alive but round-trips it through the compositor, and
    /// per-window composition state is exactly the thing that does not survive that. Re-asserting
    /// the accent policy is one call, so every reveal pays it rather than trusting the compositor.
    /// </summary>
    private void ReassertBackdropAfterReveal()
    {
        if (!_backdropActive)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && !PaletteBackdrop.TryApply(hwnd, _backdropTint))
        {
            // The compositor said no this time (driver reset, DWM restart). Re-theme opaque rather
            // than leave a see-through window with nothing behind it.
            ApplyTheme(_settings.Theme, save: false);
        }
    }

    public void WriteDebugSnapshot(string reason = "hotkey")
    {
        ShellLog.Snapshot("=== Snapshot ===");
        ShellLog.Snapshot($"reason={reason} visible={IsVisible} paletteOpen={_paletteOpen} selected={_selected?.Id ?? "none"} kind={_selected?.Kind.ToString() ?? "none"} filter={_kindFilter} date={_dateFilter} file={_fileFilter}");
        ShellLog.Snapshot($"items all={_allItems.Count} renderedRows={_rows.Count} search={SearchBox.Text}");
        if (_selected is not null)
        {
            ShellLog.Snapshot($"selected preview={_selected.Preview} pinned={_selected.IsPinned} source={_selected.SourceApplication} path={_selected.SourceApplicationPath}");
        }

        ShellLog.Snapshot($"scroll listV={ListScroll.VerticalOffset}/{ListScroll.ScrollableHeight} listH={ListScroll.HorizontalOffset}/{ListScroll.ScrollableWidth} info={InfoScroll.VerticalOffset}/{InfoScroll.ScrollableHeight}");
        ShellLog.Snapshot($"ui popupOpen={ActionMenuPopup.IsOpen} rows={ItemsHost.Children.Count} infoRows={InfoHost.Children.Count}");
        ShellLog.Snapshot("=== End Snapshot ===");
        ShellLog.Flush();
        ShowToast("Clip log saved");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate && _paletteNoActivate)
        {
            handled = true;
            return new IntPtr(MouseActivateNoActivate);
        }

        if (ExpandedImageOverlay.Visibility == Visibility.Visible && (msg == WmMouseWheel || msg == WmMouseHWheel))
        {
            var delta = WheelDelta(wParam);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                ZoomExpandedImage(Math.Pow(1.0018, delta), MousePointInExpandedViewport());
            }
            else if (msg == WmMouseHWheel)
            {
                PanExpandedImage(-delta, 0);
            }
            else
            {
                PanExpandedImage(0, delta);
            }

            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == OpenHotkeyId)
        {
            ShellLog.Info($"{_settings.Hotkeys.OpenClip} received open={_paletteOpen}");
            if (_paletteOpen)
            {
                ConcealPalette("hotkey-toggle");
            }
            else
            {
                // The clock starts where the user's key press arrives, not where the show begins:
                // anything between the two is part of what the press costs.
                BenchMarks.Begin();
                ShowPalette();
            }

            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == OpenOverrideHotkeyId)
        {
            ShellLog.Info($"open override hotkey received key={_activeOpenOverrideHotkey ?? "?"} app={_activeOpenOverrideApp ?? "?"}");
            ShowPalette();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == DebugLogHotkeyId)
        {
            ShellLog.Info($"{_settings.Hotkeys.SaveDebugLog} received");
            WriteDebugSnapshot("global-hotkey");
            handled = true;
        }
        else if (msg == WmClipboardUpdate)
        {
            var sequence = GetClipboardSequenceNumber();
            if (IsDuplicateClipboardSequence(sequence))
            {
                handled = true;
                return IntPtr.Zero;
            }

            // Only consume the sequence once the read actually succeeded. Marking it up front
            // meant a single transient clipboard lock lost the copy permanently, because the
            // retry notification for the same sequence was then discarded as a duplicate.
            if (CaptureClipboard() && sequence != 0)
            {
                _lastClipboardSequenceNumber = sequence;
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    private bool IsDuplicateClipboardSequence(uint sequence)
    {
        if (sequence == 0)
        {
            return false;
        }

        if (sequence == _lastClipboardSequenceNumber)
        {
            ShellLog.Info($"clipboard skipped duplicate sequence={sequence}");
            return true;
        }

        return false;
    }

    private bool EnsureHotkeyRegistered(string reason)
    {
        if (_openHotkeyRegistered && _debugLogHotkeyRegistered)
        {
            _hotkeyRetryTimer.Stop();
            return true;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            ShellLog.Info($"hotkey skipped reason={reason} hwnd=0");
            return false;
        }

        if (!_openHotkeyRegistered && !_openHotkeyUnavailable)
        {
            _openHotkeyRegistered = RegisterConfiguredHotkey(
                hwnd,
                OpenHotkeyId,
                _settings.Hotkeys.OpenClip,
                ClipHotkeyDefaults.OpenClip,
                "open",
                reason,
                out _openHotkeyUnavailable);
        }

        if (!_debugLogHotkeyRegistered && !_debugLogHotkeyUnavailable)
        {
            _debugLogHotkeyRegistered = RegisterConfiguredHotkey(
                hwnd,
                DebugLogHotkeyId,
                _settings.Hotkeys.SaveDebugLog,
                ClipHotkeyDefaults.SaveDebugLog,
                "debug-log",
                reason,
                out _debugLogHotkeyUnavailable);
        }

        if (_openHotkeyRegistered && _debugLogHotkeyRegistered)
        {
            _hotkeyRetryTimer.Stop();
        }
        else if ((_openHotkeyUnavailable || _openHotkeyRegistered) &&
            (_debugLogHotkeyUnavailable || _debugLogHotkeyRegistered))
        {
            _hotkeyRetryTimer.Stop();

            // The retries are giving up for good: another app owns the gesture and waiting will
            // not free it. Left silent, "Alt+V does nothing" is indistinguishable from Clip not
            // running at all, so say so once via the tray balloon.
            if (_openHotkeyUnavailable && !_openHotkeyConflictNotified)
            {
                _openHotkeyConflictNotified = true;
                var gesture = string.IsNullOrWhiteSpace(_settings.Hotkeys.OpenClip) ? ClipHotkeyDefaults.OpenClip : _settings.Hotkeys.OpenClip;
                UserNotificationRequested?.Invoke($"{gesture} is in use by another app — change Clip's hotkey in Settings");
            }
        }
        else if (!_hotkeyRetryTimer.IsEnabled)
        {
            _hotkeyRetryTimer.Start();
        }

        return _openHotkeyRegistered && _debugLogHotkeyRegistered;
    }

    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    private WinEventDelegate? _foregroundHookDelegate;
    private IntPtr _foregroundHook = IntPtr.Zero;

    private void InstallForegroundHook()
    {
        if (_foregroundHook != IntPtr.Zero) return;
        _foregroundHookDelegate = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundHookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        ShellLog.Info($"foreground hook installed handle={_foregroundHook}");
        ApplyForegroundOverride(GetForegroundWindow());
    }

    private void UninstallForegroundHook()
    {
        if (_foregroundHook == IntPtr.Zero) return;
        UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundHookDelegate = null;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0) return;
        if (hwnd == IntPtr.Zero) return;
        var own = new WindowInteropHelper(this).Handle;
        if (hwnd == own) return;
        ApplyForegroundOverride(hwnd);
    }

    private void ApplyForegroundOverride(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var processName = TryGetProcessNameForWindow(hwnd);
        if (string.IsNullOrEmpty(processName)) return;
        using (var self = Process.GetCurrentProcess())
        {
            if (string.Equals(processName, self.ProcessName, StringComparison.OrdinalIgnoreCase)) return;
        }

        var match = _settings.AppOverrides.FirstOrDefault(o =>
            string.Equals(o.Action, ClipAppOverride.ActionOpenClip, StringComparison.OrdinalIgnoreCase)
            && string.Equals(StripExeSuffix(o.AppName), processName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(o.Hotkey));

        var mainHwnd = new WindowInteropHelper(this).Handle;
        if (mainHwnd == IntPtr.Zero) return;

        if (match is not null)
        {
            if (_openOverrideRegistered && string.Equals(_activeOpenOverrideHotkey, match.Hotkey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (_openOverrideRegistered)
            {
                UnregisterHotKey(mainHwnd, OpenOverrideHotkeyId);
                _openOverrideRegistered = false;
            }
            if (_openHotkeyRegistered)
            {
                UnregisterHotKey(mainHwnd, OpenHotkeyId);
                _openHotkeyRegistered = false;
                _openHotkeyUnavailable = false;
            }
            if (ClipHotkeyGesture.TryParseGlobal(match.Hotkey, out var gesture))
            {
                var ok = RegisterHotKey(mainHwnd, OpenOverrideHotkeyId, gesture.WinModifiers, gesture.VirtualKey);
                if (ok)
                {
                    _openOverrideRegistered = true;
                    _activeOpenOverrideApp = processName;
                    _activeOpenOverrideHotkey = match.Hotkey;
                    ShellLog.Info($"open override registered app={processName} key={match.Hotkey}");
                    return;
                }
                ShellLog.Info($"open override register failed app={processName} key={match.Hotkey} win32={Marshal.GetLastWin32Error()}");
            }
            EnsureHotkeyRegistered("override-fallback");
        }
        else
        {
            if (_openOverrideRegistered)
            {
                UnregisterHotKey(mainHwnd, OpenOverrideHotkeyId);
                _openOverrideRegistered = false;
                _activeOpenOverrideApp = null;
                _activeOpenOverrideHotkey = null;
                ShellLog.Info("open override cleared");
            }
            if (!_openHotkeyRegistered && !_openHotkeyUnavailable)
            {
                EnsureHotkeyRegistered("foreground-default");
            }
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private static bool RegisterConfiguredHotkey(IntPtr hwnd, int id, string configured, string fallback, string name, string reason, out bool unavailable)
    {
        unavailable = false;
        // Empty = the user intentionally unbound this hotkey: register nothing and report "done"
        // (true) so the retry timer stops and no "unavailable" error is raised. Re-binding runs
        // through ReRegisterHotkeys, which clears the flag and registers the new gesture.
        if (string.IsNullOrWhiteSpace(configured))
        {
            ShellLog.Info($"hotkey unbound name={name} reason={reason}");
            return true;
        }
        if (!ClipHotkeyGesture.TryParseGlobal(configured, out var gesture) && !ClipHotkeyGesture.TryParseGlobal(fallback, out gesture))
        {
            ShellLog.Info($"hotkey register skipped name={name} configured={configured} reason={reason}");
            return false;
        }

        var registered = RegisterHotKey(hwnd, id, gesture.WinModifiers, gesture.VirtualKey);
        var win32 = Marshal.GetLastWin32Error();
        unavailable = !registered && win32 == ErrorHotkeyAlreadyRegistered;
        ShellLog.Info($"hotkey register name={name} key={gesture.DisplayText} reason={reason} registered={registered} hwnd={hwnd} win32={win32}");
        return registered;
    }

    /// <summary>
    /// Reads the clipboard and captures whatever is on it. Returns false only when the
    /// clipboard could not be read at all, so the caller knows not to consume the sequence
    /// number. Another process holding the clipboard lock is normal and transient on Windows,
    /// so the read is retried rather than dropped.
    /// </summary>
    private bool CaptureClipboard()
    {
        for (var attempt = 1; attempt <= ClipboardReadAttempts; attempt++)
        {
            try
            {
                ReadAndCaptureClipboard();
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == ClipboardReadAttempts)
                {
                    ShellLog.Error(ex, $"clipboard capture failed after {attempt} attempts");
                    return false;
                }

                ShellLog.Info($"clipboard capture retry {attempt} error={ex.GetType().Name}");
                System.Threading.Thread.Sleep(ClipboardReadRetryDelayMs);
            }
        }

        return false;
    }

    private void ReadAndCaptureClipboard()
    {
        {
            // Paused is a cross-process switch the tray can flip at any moment, so it is read
            // from disk here rather than trusted to the settings loaded at startup. A capture
            // is a rare event; the file read costs nothing that matters.
            if (RefreshCapturePaused())
            {
                ShellLog.Info("clipboard skipped capture paused");
                return;
            }

            ClipboardHistoryItem? item = null;
            var source = ForegroundSource();
            if (_settings.Privacy.IsExcluded(source.Name, source.Path))
            {
                ShellLog.Info($"clipboard skipped excluded source={source.Name} path={source.Path}");
                return;
            }

            // Password managers flag transient secrets with dedicated clipboard formats; a copy
            // carrying one must never land in history. Same shared check as the Watcher's path.
            var dataObject = System.Windows.Clipboard.GetDataObject();
            if (dataObject is not null &&
                ClipboardPrivacyFormats.ShouldExcludeFromHistory(dataObject.GetDataPresent, dataObject.GetData))
            {
                ShellLog.Info($"clipboard skipped privacy format source={source.Name}");
                return;
            }

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList();
                item = new ClipboardHistoryItem
                {
                    Kind = ClipboardItemKind.Files,
                    FilePaths = files,
                    Preview = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files",
                    ContentHash = HashText(string.Join("|", files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))),
                    SourceApplication = source.Name,
                    SourceApplicationPath = source.Path,
                    SourceAppUserModelId = source.Aumid,
                };
            }
            else if (System.Windows.Clipboard.ContainsImage() ||
                     System.Windows.Clipboard.ContainsData("PNG"))
            {
                var image = ClipboardImageReader.Read();
                if (image is not null)
                {
                    QueueImageClipboardCapture(image, source);
                    return;
                }
            }
            else if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                var captureRichText = _settings.DefaultPasteFormat == PasteFormatPreference.OriginalFormatting;
                var htmlText = captureRichText ? ClipboardTextOrNull(System.Windows.TextDataFormat.Html) : null;
                var rtfText = captureRichText ? ClipboardTextOrNull(System.Windows.TextDataFormat.Rtf) : null;
                if (TryNormalizeColorText(text, source.Name, out var colorHex))
                {
                    item = new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Color,
                        Text = colorHex,
                        Preview = colorHex,
                        ContentHash = HashText(colorHex),
                        HtmlText = htmlText,
                        RtfText = rtfText,
                        SourceApplication = source.Name,
                        SourceApplicationPath = source.Path,
                        SourceAppUserModelId = source.Aumid,
                    };
                }
                else
                {
                    item = new ClipboardHistoryItem
                    {
                        Kind = ClipboardLinkDetector.IsLinkOrEmail(text) ? ClipboardItemKind.Link : ClipboardItemKind.Text,
                        Text = text,
                        Preview = ClipboardHistoryStore.PreviewText(text),
                        ContentHash = HashText(text),
                        HtmlText = htmlText,
                        RtfText = rtfText,
                        SourceApplication = source.Name,
                        SourceApplicationPath = source.Path,
                        SourceAppUserModelId = source.Aumid,
                    };
                }
            }

            if (item is null)
            {
                return;
            }

            CaptureClipboardItem(item);
        }
    }

    private void CaptureClipboardItem(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            // Two real copies inside the settle window used to silently destroy the first one.
            // An app rewriting its own clipboard in a burst is what the debounce is for, so only
            // rescue a pending item that has been sitting long enough to look like a user action.
            if (_pendingTextClipboardItem is not null &&
                DateTime.UtcNow - _pendingTextClipboardItemAt >= PendingTextRescueThreshold)
            {
                ShellLog.Info($"clipboard text rescued before supersede preview={_pendingTextClipboardItem.Preview}");
                SavePendingTextClipboardItem(requireStillOnClipboard: false);
            }

            _pendingTextClipboardItem = item;
            _pendingTextClipboardItemAt = DateTime.UtcNow;
            _clipboardSettleTimer.Stop();
            _clipboardSettleTimer.Start();
            ShellLog.Info($"clipboard text pending kind={item.Kind} source={item.SourceApplication} preview={item.Preview}");
            return;
        }

        DropPendingTextClipboardItem("replaced-before-settle");
        if (ShouldSkipDuplicateClipboardBurst(item))
        {
            return;
        }

        QueueClipboardItemSave(item, "clipboard-live");
    }

    private bool ShouldSkipDuplicateClipboardBurst(ClipboardHistoryItem item)
    {
        var fingerprint = ClipboardFingerprint(item);
        if (!_clipboardCaptureBurstGate.ShouldSkip(fingerprint, DateTimeOffset.UtcNow))
        {
            return false;
        }

        DeleteUnsavedCaptureAsset(item);
        ShellLog.Info($"clipboard skipped duplicate burst kind={item.Kind} source={item.SourceApplication} preview={item.Preview}");
        return true;
    }

    private static string? ClipboardFingerprint(ClipboardHistoryItem item)
    {
        return string.IsNullOrWhiteSpace(item.ContentHash)
            ? null
            : $"{item.Kind}:{item.ContentHash}";
    }

    private void SavePendingTextClipboardItem() => SavePendingTextClipboardItem(requireStillOnClipboard: true);

    private void SavePendingTextClipboardItem(bool requireStillOnClipboard)
    {
        var pending = _pendingTextClipboardItem;
        _pendingTextClipboardItem = null;
        if (pending is null)
        {
            return;
        }

        if (requireStillOnClipboard && !ClipboardStillContains(pending))
        {
            ShellLog.Info($"clipboard text skipped transient source={pending.SourceApplication} preview={pending.Preview}");
            return;
        }

        QueueClipboardItemSave(pending, "clipboard-live");
    }

    private void DropPendingTextClipboardItem(string reason)
    {
        if (_pendingTextClipboardItem is null)
        {
            return;
        }

        ShellLog.Info($"clipboard text skipped reason={reason} source={_pendingTextClipboardItem.SourceApplication} preview={_pendingTextClipboardItem.Preview}");
        _pendingTextClipboardItem = null;
        _clipboardSettleTimer.Stop();
    }

    private OcrQueue? _ocrQueue;

    private void QueueOcrIfEnabled(ClipboardHistoryItem? item)
    {
        if (item is null ||
            item.Kind != ClipboardItemKind.Image ||
            !_settings.ExtractTextFromImages ||
            item.OcrText is not null)
        {
            return;
        }

        // Respect the excluded-apps list: if the user does not want an app's clipboard recorded,
        // they certainly do not want its screenshots transcribed.
        if (_settings.Privacy.IsExcluded(item.SourceApplication, item.SourceApplicationPath))
        {
            return;
        }

        _ocrQueue ??= new OcrQueue(() => _store, ShellLog.Info);
        _ocrQueue.Enqueue(item.Id, item.AssetPath);
    }

    private void QueueClipboardItemSave(ClipboardHistoryItem item, string renderReason)
    {
        if (!ClipItemSizeLimit.Allows(item, _settings.MaxItemSizeBytes))
        {
            var itemBytes = ClipItemSizeLimit.EstimateBytes(item);
            ShellLog.Info($"clipboard skipped oversized kind={item.Kind} bytes={itemBytes} limit={ClipItemSizeLimit.MaxItemSizeLabel(_settings.MaxItemSizeBytes)} source={item.SourceApplication} preview={item.Preview}");
            DeleteUnsavedCaptureAsset(item);
            ShowToast("Clipboard item skipped: too large");
            return;
        }

        var maxItems = EffectiveHistoryLimit();
        var persist = PersistClipboardItemAsync(item, maxItems);
        TrackClipboardPersist(persist);
        _ = persist.ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                DeleteUnsavedCaptureAsset(item);
                ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("clipboard persist failed"), "clipboard persist failed");
                return;
            }

            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            QueueOcrIfEnabled(task.Result);

            _ = Dispatcher.BeginInvoke(new Action(() => ApplySavedClipboardItem(task.Result, renderReason)), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private async Task<ClipboardHistoryItem> PersistClipboardItemAsync(ClipboardHistoryItem item, int maxItems)
    {
        await _clipboardPersistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _store.AddOrUpdate(item, maxItems)).ConfigureAwait(false);
        }
        finally
        {
            _clipboardPersistGate.Release();
        }
    }

    private void ApplySavedClipboardItem(ClipboardHistoryItem saved, string renderReason)
    {
        ShellLog.Info($"clipboard captured id={saved.Id} kind={saved.Kind} source={saved.SourceApplication} preview={saved.Preview}");
        ClearRecentFirstPaintPreload();
        _allItems = _store.QueryItemSummaries();
        _historySummariesPreloaded = string.IsNullOrWhiteSpace(SearchBox.Text);
        if (_paletteOpen)
        {
            RenderItems(reason: renderReason);
        }
        else
        {
            _itemsDirtySinceRender = true;
        }
    }

    private void PreloadHistorySummariesIfCheap()
    {
        if (_historySummariesPreloaded || _historyPreloadInProgress || _paletteOpen || !CanPreloadHistorySummaries())
        {
            return;
        }

        _historyPreloadInProgress = true;
        var watch = Stopwatch.StartNew();
        _ = Task.Run(() => _store.QueryItemSummaries()).ContinueWith(task =>
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _historyPreloadInProgress = false;
                if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                if (task.IsFaulted)
                {
                    ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("history preload failed"), "history preload failed");
                    return;
                }

                if (_paletteOpen || !string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    return;
                }

                _allItems = task.Result;
                _historySummariesPreloaded = true;
                _itemsDirtySinceRender = true;
                ShellLog.Info($"history summaries preloaded count={_allItems.Count} elapsedMs={watch.ElapsedMilliseconds}");
            }), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void StartRecentFirstPaintPreload()
    {
        if (_recentFirstPaintPreloadTask is not null || !_store.HasCurrentRecentSummaryIndex())
        {
            return;
        }

        _recentFirstPaintPreloadTask = Task.Run(() =>
        {
            var queryWatch = Stopwatch.StartNew();
            var items = _store.QueryRecentItemSummaries(InitialSummaryFirstPaintLimit);
            return ((IReadOnlyList<ClipboardHistoryItem>)items, queryWatch.ElapsedMilliseconds);
        });
    }

    private void ClearRecentFirstPaintPreload()
    {
        _recentFirstPaintPreloadTask = null;
    }

    private bool CanPreloadHistorySummaries()
    {
        try
        {
            if (!_store.HasCurrentSummaryIndex() || !File.Exists(_store.HistoryIndexFilePath))
            {
                return false;
            }

            return new FileInfo(_store.HistoryIndexFilePath).Length <= SummaryPreloadMaximumBytes;
        }
        catch
        {
            return false;
        }
    }

    private void QueueImageClipboardCapture(BitmapSource image, (string? Name, string? Path, string? Aumid) source)
    {
        var capturedAt = DateTimeOffset.Now;
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var path = _store.NewAssetFilePath(".png");

        if (!image.IsFrozen && image.CanFreeze)
        {
            image.Freeze();
        }

        if (!image.IsFrozen)
        {
            try
            {
                CaptureClipboardItem(CreateImageClipboardItem(image, path, width, height, source, capturedAt));
            }
            catch (Exception ex)
            {
                DeleteCaptureFile(path);
                ShellLog.Error(ex, "clipboard image capture failed");
            }

            return;
        }

        _ = Task.Run(() => CreateImageClipboardItem(image, path, width, height, source, capturedAt)).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                DeleteCaptureFile(path);
                ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("clipboard image capture failed"), "clipboard image capture failed");
                return;
            }

            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                DeleteCaptureFile(path);
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => CaptureClipboardItem(task.Result)), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private static ClipboardHistoryItem CreateImageClipboardItem(BitmapSource image, string path, int width, int height, (string? Name, string? Path, string? Aumid) source, DateTimeOffset capturedAt)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = path,
            Preview = $"Image {width} x {height}",
            ContentHash = HashFile(path),
            ImageWidth = width,
            ImageHeight = height,
            SourceApplication = source.Name,
            SourceApplicationPath = source.Path,
            SourceAppUserModelId = source.Aumid,
            CreatedAt = capturedAt,
            LastUsedAt = capturedAt,
            FirstCopiedAt = capturedAt,
            LastCopiedAt = capturedAt,
        };
    }

    private void TrackClipboardPersist(Task task)
    {
        lock (_clipboardPersistTasksGate)
        {
            _clipboardPersistTasks.Add(task);
        }

        _ = task.ContinueWith(completed =>
        {
            lock (_clipboardPersistTasksGate)
            {
                _clipboardPersistTasks.Remove(completed);
            }
        }, TaskScheduler.Default);
    }

    private void FlushPendingClipboardPersists()
    {
        Task[] pending;
        lock (_clipboardPersistTasksGate)
        {
            pending = _clipboardPersistTasks.Where(task => !task.IsCompleted).ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "clipboard persist flush failed");
        }
    }

    private static void DeleteCaptureFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void DeleteUnsavedCaptureAsset(ClipboardHistoryItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.AssetPath) ||
                !Path.GetFullPath(item.AssetPath).StartsWith(Path.GetFullPath(_store.ContentRootPath), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(item.AssetPath))
            {
                return;
            }

            File.Delete(item.AssetPath);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"oversized capture cleanup failed path={item.AssetPath}");
        }
    }

    private static bool ClipboardStillContains(ClipboardHistoryItem item)
    {
        // A transient clipboard lock must not look like "the user replaced it" — that would
        // silently discard the pending item. Retry, and if the clipboard stays unreadable,
        // prefer keeping the capture over dropping it.
        for (var attempt = 0; attempt < ClipboardReadAttempts; attempt++)
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsText())
                {
                    return false;
                }

                return ClipboardCaptureMatch.MatchesClipboardText(item, System.Windows.Clipboard.GetText());
            }
            catch (Exception ex) when (attempt < ClipboardReadAttempts - 1)
            {
                ShellLog.Info($"clipboard settle read retry {attempt + 1} error={ex.GetType().Name}");
                System.Threading.Thread.Sleep(ClipboardReadRetryDelayMs);
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "clipboard settle read failed, keeping pending item");
                return true;
            }
        }

        return true;
    }

    private void QueueLoadItems(bool selectFirst, string reason)
    {
        var generation = ++_loadGeneration;
        var query = SearchBox.Text;
        var totalWatch = Stopwatch.StartNew();
        var isSearch = string.Equals(reason, "search", StringComparison.OrdinalIgnoreCase);
        if (isSearch)
        {
            CancelBackgroundFullSummaryRefresh(reason);
        }

        if (string.IsNullOrWhiteSpace(query) && _historySummariesPreloaded)
        {
            try
            {
                var visibleItems = RenderItems(reason);
                _itemsDirtySinceRender = false;
                SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: true);

                ShellLog.Info($"load items reason={reason} count={_allItems.Count} queryElapsedMs=0 elapsedMs={totalWatch.ElapsedMilliseconds} preloaded=True");
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, $"load preloaded items failed reason={reason}");
            }

            return;
        }

        if (ShouldUseRecentSummaryFirstPaint(query, reason))
        {
            try
            {
                var preloaded = false;
                long queryElapsedMs;
                var preloadTask = _recentFirstPaintPreloadTask;
                if (preloadTask is { IsCompletedSuccessfully: true })
                {
                    _allItems = preloadTask.Result.Items;
                    queryElapsedMs = preloadTask.Result.QueryElapsedMs;
                    preloaded = true;
                    ClearRecentFirstPaintPreload();
                }
                else
                {
                    var queryWatch = Stopwatch.StartNew();
                    _allItems = _store.QueryRecentItemSummaries(InitialSummaryFirstPaintLimit);
                    queryElapsedMs = queryWatch.ElapsedMilliseconds;
                }

                var visibleItems = RenderItems(reason);
                _itemsDirtySinceRender = true;
                SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: true);

                ShellLog.Info($"load items reason={reason} count={_allItems.Count} queryElapsedMs={queryElapsedMs} elapsedMs={totalWatch.ElapsedMilliseconds} recent=True preloaded={preloaded}");
                QueueFullSummaryRefreshAfterFirstPaint(generation, selectFirst);
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, $"load recent items failed reason={reason}");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(query) && _store.HasCurrentSummaryIndex())
        {
            try
            {
                var queryWatch = Stopwatch.StartNew();
                _allItems = _store.QueryItemSummaries();
                _historySummariesPreloaded = true;
                var queryElapsedMs = queryWatch.ElapsedMilliseconds;
                var visibleItems = RenderItems(reason);
                _itemsDirtySinceRender = false;
                SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: true);

                ShellLog.Info($"load items reason={reason} count={_allItems.Count} queryElapsedMs={queryElapsedMs} elapsedMs={totalWatch.ElapsedMilliseconds} inline=True");
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, $"load items failed reason={reason}");
            }

            return;
        }

        _ = Task.Run(() =>
        {
            var queryWatch = Stopwatch.StartNew();
            var items = _store.QueryItemSummaries(query);
            return (Items: items, QueryElapsedMs: queryWatch.ElapsedMilliseconds);
        }).ContinueWith(task =>
        {
            var dispatcherPriority = isSearch
                ? System.Windows.Threading.DispatcherPriority.Send
                : System.Windows.Threading.DispatcherPriority.Background;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _loadGeneration)
                {
                    ShellLog.Info($"load items canceled reason={reason} generation={generation}");
                    return;
                }

                if (task.IsFaulted)
                {
                    ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("load items failed"), $"load items failed reason={reason}");
                    return;
                }

                _allItems = task.Result.Items;
                _historySummariesPreloaded = string.IsNullOrWhiteSpace(query);
                var visibleItems = RenderItems(reason);
                _itemsDirtySinceRender = false;
                SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: false);

                ShellLog.Info($"load items reason={reason} count={_allItems.Count} queryElapsedMs={task.Result.QueryElapsedMs} elapsedMs={totalWatch.ElapsedMilliseconds}");
            }), dispatcherPriority);
        });
    }

    private void CancelBackgroundFullSummaryRefresh(string reason)
    {
        var operation = _backgroundFullRefreshOperation;
        if (operation is null)
        {
            return;
        }

        _backgroundFullRefreshOperation = null;
        if (operation.Status == System.Windows.Threading.DispatcherOperationStatus.Pending)
        {
            _ = operation.Abort();
            ShellLog.Info($"background full refresh canceled reason={reason}");
        }
    }

    private bool ShouldUseRecentSummaryFirstPaint(string? query, string reason)
    {
        return string.IsNullOrWhiteSpace(query) &&
            string.Equals(reason, "show-refresh", StringComparison.OrdinalIgnoreCase) &&
            _store.HasCurrentRecentSummaryIndex();
    }

    private void QueueFullSummaryRefreshAfterFirstPaint(int generation, bool selectFirst)
    {
        var totalWatch = Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            await Task.Delay(180);
            var queryWatch = Stopwatch.StartNew();
            var items = _store.QueryItemSummaries();
            return (Items: items, QueryElapsedMs: queryWatch.ElapsedMilliseconds);
        }).ContinueWith(task =>
        {
            QueueApply(0);

            void QueueApply(int delayMs)
            {
                if (delayMs <= 0)
                {
                    _backgroundFullRefreshOperation = Dispatcher.BeginInvoke(new Action(Apply), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    return;
                }

                _ = Task.Delay(delayMs).ContinueWith(_ =>
                {
                    if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    {
                        return;
                    }

                    _backgroundFullRefreshOperation = Dispatcher.BeginInvoke(new Action(Apply), System.Windows.Threading.DispatcherPriority.ContextIdle);
                }, TaskScheduler.Default);
            }

            void Apply()
            {
                _backgroundFullRefreshOperation = null;
                if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                if (generation != _loadGeneration || !string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    ShellLog.Info($"load items canceled reason=background-full-refresh generation={generation}");
                    return;
                }

                if (task.IsFaulted)
                {
                    ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("load items failed"), "load items failed reason=background-full-refresh");
                    return;
                }

                _allItems = task.Result.Items;
                _historySummariesPreloaded = true;
                if (_paletteOpen)
                {
                    if (IsPaletteInteractionBusy())
                    {
                        QueueApply(120);
                        return;
                    }

                    var visibleItems = RenderItems("background-full-refresh");
                    _itemsDirtySinceRender = false;
                    SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: false);
                }
                else
                {
                    _itemsDirtySinceRender = true;
                }

                ShellLog.Info($"load items reason=background-full-refresh count={_allItems.Count} queryElapsedMs={task.Result.QueryElapsedMs} elapsedMs={totalWatch.ElapsedMilliseconds}");
            }
        }, TaskScheduler.Default);
    }

    private bool IsPaletteInteractionBusy()
    {
        return _suppressDeactivate ||
            ActionMenuPopup.IsOpen ||
            ShareSubmenuPopup.IsOpen ||
            IsContextMenuOpen(this);
    }

    private void LoadItems(bool selectFirst, string reason)
    {
        _loadGeneration++;
        var watch = Stopwatch.StartNew();
        try
        {
            _allItems = _store.QueryItemSummaries(SearchBox.Text);
            _historySummariesPreloaded = string.IsNullOrWhiteSpace(SearchBox.Text);
            var visibleItems = RenderItems(reason);
            _itemsDirtySinceRender = false;
            SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: false);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"load items failed reason={reason}");
        }
        finally
        {
            ShellLog.Info($"load items reason={reason} count={_allItems.Count} elapsedMs={watch.ElapsedMilliseconds}");
        }
    }

    private void SelectInitialItemIfNeeded(bool selectFirst, IReadOnlyList<ClipboardHistoryItem> visibleItems, bool defer)
    {
        // An automatic selection is replaceable; one the user made is not. Before the startup
        // warm-up existed there was never a selection at this point, so "already selected" was
        // enough to mean "the user chose it".
        if (!selectFirst || (_selected is not null && !_selectionIsAutomatic))
        {
            return;
        }

        var first = visibleItems.FirstOrDefault();
        if (first is null)
        {
            return;
        }

        // Nobody chose this one; the list did. That matters because the selection is now made
        // before the palette is ever opened, and an automatic choice must not outrank the newest
        // clip: copy something, open, and the thing you just copied is what should be highlighted.
        _selectionIsAutomatic = true;

        if (!defer)
        {
            SelectItem(first, reason: "initial");
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_selected is null)
            {
                SelectItem(first, reason: "initial");
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task ImportWindowsClipboardHistoryAsync(string reason, bool refreshVisible)
    {
        if (_historyImportInProgress)
        {
            return;
        }

        if (!_windowsHistoryImportThrottle.TryBegin(DateTimeOffset.UtcNow))
        {
            ShellLog.Info($"windows history import skipped reason={reason} throttle={WindowsHistoryImportMinimumInterval}");
            return;
        }

        _historyImportInProgress = true;
        var watch = Stopwatch.StartNew();
        try
        {
            var imported = await ImportWindowsClipboardHistoryInHelperAsync(EffectiveHistoryLimit());
            if (imported > 0)
            {
                _itemsDirtySinceRender = true;
                _historySummariesPreloaded = false;
                if (refreshVisible && _paletteOpen)
                {
                    _ = Dispatcher.BeginInvoke(new Action(() => QueueLoadItems(selectFirst: _selected is null, reason: $"windows-history-{reason}")), System.Windows.Threading.DispatcherPriority.Background);
                }
            }

            ShellLog.Info($"windows history import reason={reason} imported={imported} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"windows history import failed reason={reason}");
        }
        finally
        {
            _historyImportInProgress = false;
        }
    }

    private static async Task<int> ImportWindowsClipboardHistoryInHelperAsync(int maxItems)
    {
        var helper = FindWindowsHistoryExecutable();
        if (helper is null)
        {
            ShellLog.Info("windows history import skipped helper=missing");
            return 0;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(helper) ?? AppContext.BaseDirectory,
            },
        };
        process.StartInfo.ArgumentList.Add("import-windows-history");
        process.StartInfo.ArgumentList.Add("--max");
        process.StartInfo.ArgumentList.Add(maxItems.ToString());

        if (!process.Start())
        {
            return 0;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            ShellLog.Info($"windows history import helper failed exit={process.ExitCode} error={error.Trim()}");
            return 0;
        }

        return ParseImportCount(output);
    }

    private static string? FindWindowsHistoryExecutable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Clip.WindowsHistory.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory, "Clip.WindowsHistory.exe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return null;
    }

    private static int ParseImportCount(string output)
    {
        foreach (var line in output.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (int.TryParse(line.Trim(), out var count))
            {
                return count;
            }
        }

        return 0;
    }

    private IReadOnlyList<ClipboardHistoryItem> RenderItems(string reason)
    {
        var watch = Stopwatch.StartNew();
        var selectedId = _selected?.Id;
        var visibleItems = FilteredItems();
        _renderedVisibleItems = visibleItems;
        var entries = RenderEntries(visibleItems);
        var generation = ++_renderGeneration;
        ItemsHost.Children.Clear();
        _rows.Clear();
        _deferredRenderEntries = [];
        _deferredRenderIndex = 0;
        _deferredRenderGeneration = generation;
        _deferredRenderReason = string.Empty;
        _deferredRenderWatch = null;
        UpdateFilterVisuals();

        var nextIndex = AddRenderEntries(entries, 0, InitialRenderEntryBatch);

        if (selectedId is not null && _rows.TryGetValue(selectedId, out var selectedRow))
        {
            selectedRow.Background = (WpfBrush)FindResource("Selected");
            selectedRow.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
        }

        if (nextIndex < entries.Count)
        {
            _deferredRenderEntries = entries;
            _deferredRenderIndex = nextIndex;
            _deferredRenderGeneration = generation;
            _deferredRenderReason = reason;
            _deferredRenderWatch = watch;
            QueueDeferredAppend();
        }

        UpdateEmptyState(visibleItems.Count);

        // Only reconcile an existing selection — except on search renders, where a null
        // selection means an earlier query emptied the list (which cleared the selection) and
        // this render refilled it: Enter needs a target again, so reconcile lands on the first
        // result. For every other render with nothing selected the choice belongs to
        // SelectInitialItemIfNeeded, which defers the (expensive) first preview render so it
        // cannot slow the palette's first paint.
        var isSearchRender = string.Equals(reason, "search", StringComparison.OrdinalIgnoreCase);
        if (selectedId is not null || isSearchRender)
        {
            var reconciled = PaletteSelection.Reconcile(selectedId, visibleItems);
            if (reconciled is null)
            {
                if (selectedId is not null)
                {
                    ClearSelection();
                }
            }
            else if (reconciled.Id != selectedId)
            {
                _selectionIsAutomatic = true;
                SelectItem(reconciled, reason: "reconcile");
            }
        }

        BenchMarks.Mark("rows-first");
        ShellLog.Info($"render items reason={reason} rows={_rows.Count}/{visibleItems.Count} selected={selectedId ?? "none"} elapsedMs={watch.ElapsedMilliseconds} deferred={nextIndex < entries.Count}");
        return visibleItems;
    }

    /// <summary>
    /// The items in the order the list actually shows them: pinned first, then the date groups.
    /// This is NOT FilteredItems' order — keyboard navigation and Ctrl+digit indexing must walk
    /// the rows the user sees, or "the third item" pastes something other than the third row.
    /// </summary>
    internal static List<ClipboardHistoryItem> VisibleOrder(IReadOnlyList<ClipboardHistoryItem> filteredItems)
    {
        return GroupItems(filteredItems).SelectMany(group => group.Items).ToList();
    }

    private static List<(string? Header, ClipboardHistoryItem? Item)> RenderEntries(IReadOnlyList<ClipboardHistoryItem> visibleItems)
    {
        var entries = new List<(string? Header, ClipboardHistoryItem? Item)>();
        foreach (var group in GroupItems(visibleItems))
        {
            if (group.Items.Count == 0)
            {
                continue;
            }

            entries.Add(($"{group.Header.ToUpperInvariant()}  {group.Items.Count}", null));
            foreach (var item in group.Items)
            {
                entries.Add((null, item));
            }
        }

        return entries;
    }

    private int AddRenderEntries(IReadOnlyList<(string? Header, ClipboardHistoryItem? Item)> entries, int start, int count)
    {
        var end = Math.Min(entries.Count, start + count);
        for (var index = start; index < end; index++)
        {
            var entry = entries[index];
            if (entry.Header is not null)
            {
                var header = new TextBlock
                {
                    Text = entry.Header,
                    Foreground = (WpfBrush)FindResource("Muted"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(16, 12, 8, 4),
                };
                ItemsHost.Children.Add(header);
                continue;
            }

            if (entry.Item is null)
            {
                continue;
            }

            var row = BuildRow(entry.Item);
            ItemsHost.Children.Add(row);
            _rows[entry.Item.Id] = row;
        }

        return end;
    }

    /// <summary>
    /// Asks for the next batch of rows on a later dispatcher turn, and only ever has one such ask
    /// outstanding.
    ///
    /// Batching the rows is supposed to leave the thread free between batches. It did not: adding
    /// rows changes the layout, laying out raises ScrollChanged, and the handler appended the next
    /// batch straight from that event. Because layout runs at Render priority — above Input — the
    /// whole list rendered in one unbroken cascade that outranked anything waiting to run, and the
    /// search box did not get focus until it was over: 377ms into a cold open. Going back through
    /// the queue for each batch restores the yield the batching was for.
    /// </summary>
    private void QueueDeferredAppend()
    {
        if (_deferredAppendQueued || _deferredRenderEntries.Count == 0)
        {
            return;
        }

        _deferredAppendQueued = true;
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _deferredAppendQueued = false;
                // Re-read rather than capture: whether the list can scroll yet decides whether more
                // rows are needed, and it changes as the batches land.
                AppendDeferredRowsIfNeeded(force: ListScroll.ScrollableHeight <= 0);
            }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool _deferredAppendQueued;

    private void AppendDeferredRowsIfNeeded(bool force = false)
    {
        if (_deferredRenderEntries.Count == 0 || _deferredRenderIndex >= _deferredRenderEntries.Count)
        {
            return;
        }

        if (_deferredRenderGeneration != _renderGeneration)
        {
            ShellLog.Info($"render items canceled reason={_deferredRenderReason} start={_deferredRenderIndex}");
            _deferredRenderEntries = [];
            return;
        }

        if (!force && ListScroll.ScrollableHeight - ListScroll.VerticalOffset > 140)
        {
            return;
        }

        BenchMarks.Mark("deferred-batch");
        _deferredRenderIndex = AddRenderEntries(_deferredRenderEntries, _deferredRenderIndex, DeferredRenderEntryBatch);
        if (_deferredRenderIndex < _deferredRenderEntries.Count)
        {
            ShellLog.Info($"render items appended reason={_deferredRenderReason} rows={_rows.Count} next={_deferredRenderIndex}/{_deferredRenderEntries.Count}");
            // Keep filling until the list can scroll, otherwise there is nothing to scroll and no
            // scroll event to ask for the rest.
            QueueDeferredAppend();
            return;
        }

        BenchMarks.Mark("rows-complete");
        ShellLog.Info($"render items complete reason={_deferredRenderReason} rows={_rows.Count} elapsedMs={_deferredRenderWatch?.ElapsedMilliseconds ?? 0}");
        _deferredRenderEntries = [];
        _deferredRenderIndex = 0;
        _deferredRenderReason = string.Empty;
        _deferredRenderWatch = null;
    }

    private void RefreshClipboardManagerTextTheme()
    {
        RefreshClipboardManagerVisualTheme(ItemsHost);
        RefreshInfoPanelTheme(refreshIcon: false);
        UpdateFilterVisuals();
        TitleText.Foreground = (WpfBrush)FindResource("Text");
        SubTitleText.Foreground = (WpfBrush)FindResource("Muted");
        RefreshClipboardManagerIconTheme();
    }

    private void RefreshClipboardManagerVisualTheme(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case TextBlock text:
                    text.Foreground = IsPrimaryClipboardText(text) ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
                    break;
                case Border { Tag: ClipboardHistoryItem rowItem } row when rowItem.Id == _selected?.Id:
                    row.Background = (WpfBrush)FindResource("Selected");
                    row.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
                    break;
            }

            RefreshClipboardManagerVisualTheme(child);
        }
    }

    private void RefreshClipboardManagerIconTheme()
    {
        RefreshClipboardManagerIcons(ItemsHost);
        if (_selected is not null)
        {
            HeaderIcon.Source = IconFor(_selected, 96);
            AttachFavicon(HeaderIcon, _selected);
        }
    }

    private void RefreshClipboardManagerIcons(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is WpfImage image && FindRowItem(image) is { } imageItem)
            {
                // Rows are built with rich previews (thumbnails, real file icons); refreshing with
                // the flat vector fallback swapped every icon for the generic glyph until restart.
                // IconFor gives links their monogram, so the favicon has to be re-attached the same
                // way row construction does or the refresh wipes it.
                image.Source = IconFor(imageItem, 96);
                AttachFavicon(image, imageItem);
            }

            RefreshClipboardManagerIcons(child);
        }
    }

    private void RefreshInfoPanelTheme(bool refreshIcon = true)
    {
        RefreshInfoPanelTheme(InfoHost);
        if (refreshIcon && _selected is not null)
        {
            HeaderIcon.Source = IconFor(_selected, 96);
            AttachFavicon(HeaderIcon, _selected);
        }
    }

    private void RefreshInfoPanelTheme(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case TextBlock text:
                    text.Foreground = (WpfBrush)FindResource("Muted2");
                    break;
                case WpfTextBox box:
                    box.Foreground = (WpfBrush)FindResource("Text");
                    box.CaretBrush = (WpfBrush)FindResource("TextCursor");
                    break;
                case Border border when border.Height == 1:
                    border.Background = (WpfBrush)FindResource("Line");
                    break;
            }

            RefreshInfoPanelTheme(child);
        }
    }

    private static ClipboardHistoryItem? FindRowItem(DependencyObject child)
    {
        var current = child;
        while (VisualTreeHelper.GetParent(current) is { } parent)
        {
            if (parent is Border { Tag: ClipboardHistoryItem item })
            {
                return item;
            }

            current = parent;
        }

        return null;
    }

    private static bool IsPrimaryClipboardText(TextBlock text)
    {
        return text.FontWeight == FontWeights.SemiBold || text.FontSize >= 13;
    }

    /// <summary>
    /// Swaps a link row's monogram for the site's real icon. The monogram is what shows until the
    /// fetch lands, and what stays if the site has no usable icon, so a row is never blank.
    /// </summary>
    private void AttachFavicon(WpfImage target, ClipboardHistoryItem item)
    {
        // The tag is cleared on every non-favicon path: the header icon is one shared element, so
        // a stale host tag would let an in-flight fetch for the previous link paint over whatever
        // item is selected by the time it resolves.
        if (item.Kind != ClipboardItemKind.Link)
        {
            target.Tag = null;
            return;
        }

        var payload = TextPayload(item);
        if (ClipboardLinkDetector.IsEmail(payload))
        {
            target.Tag = null;
            return;
        }

        var host = FaviconCache.HostOf(payload);
        if (host is null)
        {
            target.Tag = null;
            return;
        }

        target.Tag = host;

        if (FaviconCache.TryGetCached(host, out var cached))
        {
            if (cached is not null)
            {
                target.Source = cached;
            }

            return;
        }

        FaviconCache.FetchAsync(host, resolved =>
        {
            if (resolved is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                // Rows are rebuilt on every filter and search change, so only paint if this
                // element still belongs to the host we fetched for.
                if (Equals(target.Tag, host))
                {
                    target.Source = resolved;
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private static double RowIconSize(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Text)
        {
            return 22;
        }

        if (item.Kind == ClipboardItemKind.Files &&
            item.FilePaths.Count > 0 &&
            IsAudioFile(Path.GetExtension(item.FilePaths[0]).ToLowerInvariant()))
        {
            return 22;
        }

        return 28;
    }

    private Border BuildRow(ClipboardHistoryItem item)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(6, 0, 6, 0),
            Background = item.Id == _selected?.Id ? (WpfBrush)FindResource("Selected") : WpfBrushes.Transparent,
            BorderBrush = item.Id == _selected?.Id ? (WpfBrush)FindResource("SelectedBorder") : WpfBrushes.Transparent,
            // Constant 1px border on every row; selection swaps only the brush. Toggling the
            // thickness 0<->1 shifted each row's content by a pixel as the selection moved.
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Tag = item,
        };

        var grid = new Grid { ClipToBounds = true };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new WpfImage
        {
            // The text and audio marks are denser shapes than the outlined document glyph, so
            // they read heavier at the same box and sit smaller.
            Width = RowIconSize(item),
            Height = RowIconSize(item),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        // Rich preview means an image row shows a thumbnail of the actual image rather than a
        // generic picture glyph, which is what makes a list of screenshots scannable. A cold
        // thumbnail arrives late through the callback: each icon element belongs to exactly one
        // item and rows are rebuilt per render, so a swap landing on a discarded element is
        // harmless — no tag guard needed the way the shared header icon needs one.
        icon.Source = IconFor(item, RowIconLogicalSize, preferRichPreview: true, onRicher: richer => icon.Source = richer);
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        AttachFavicon(icon, item);
        grid.Children.Add(icon);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        var title = new TextBlock
        {
            Text = TitleFor(item),
            Foreground = (WpfBrush)FindResource("Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var subtitle = new TextBlock
        {
            Text = SubtitleFor(item),
            Foreground = (WpfBrush)FindResource("Muted"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        textStack.Children.Add(title);
        if (_settings.ShowSourceAppInList)
        {
            textStack.Children.Add(subtitle);
        }
        Grid.SetColumn(textStack, 2);
        grid.Children.Add(textStack);

        if (item.IsPinned)
        {
            var pin = new TextBlock
            {
                Text = "●",
                Foreground = (WpfBrush)FindResource("Muted"),
                FontSize = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(pin, 3);
            grid.Children.Add(pin);
        }

        var meta = new TextBlock
        {
            Text = MetaFor(item),
            Foreground = (WpfBrush)FindResource("Muted"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(meta, 4);
        grid.Children.Add(meta);

        row.Child = grid;
        row.MouseEnter += (_, _) =>
        {
            if (_selected?.Id == item.Id)
            {
                return;
            }

            // Outline, never a fill. Every row already carries a 1px border for the selection, so
            // hover just lights it -- the grey wash this replaced was the same one the toolbar
            // dropped, and it read heaviest on the wider-gamut monitors.
            row.BorderBrush = (WpfBrush)FindResource("Line2");
        };
        row.MouseLeave += (_, _) =>
        {
            if (_selected?.Id == item.Id)
            {
                return;
            }

            row.BorderBrush = WpfBrushes.Transparent;
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                // Disarm: the first click of the pair armed a drag, and this one is a paste.
                _rowDragOrigin = null;
                SelectItem(item, "double-click-paste");
                PasteSelected();
                e.Handled = true;
                return;
            }

            SelectItem(item, "click");
            // Arm a possible drag out of the palette. Nothing is handled here, so this press is
            // still an ordinary click; only travel past the system threshold turns it into one.
            _rowDragOrigin = (e.GetPosition(this), item);
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            SelectItem(item, "right-click-up");
            ShowActionMenu(item);
            e.Handled = true;
        };
        return row;
    }

    /// <summary>Where a press on a row started, and on which item, while it is still undecided.</summary>
    private (System.Windows.Point Origin, ClipboardHistoryItem Item)? _rowDragOrigin;

    /// <summary>
    /// Turns a held press on a row into a drag out of the palette, using the same
    /// click-versus-drag rule the top and bottom bars already use for window drags: below the
    /// system threshold nothing happens here at all, so a click still selects and a double-click
    /// still pastes. The handler sits on the list rather than on each row because a fast drag
    /// leaves the row it started on within a frame or two, and the following moves are then
    /// raised by whichever row the pointer crossed — the armed item has to come from the field,
    /// not from the element that reported the move.
    /// </summary>
    private void OnListMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_rowDragOrigin is not { } armed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _rowDragOrigin = null;
            return;
        }

        var moved = e.GetPosition(this) - armed.Origin;
        if (!ShouldStartRowDrag(
            moved.X,
            moved.Y,
            SystemParameters.MinimumHorizontalDragDistance,
            SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        _rowDragOrigin = null;
        BeginRowDrag(armed.Item, sender as DependencyObject);
    }

    /// <summary>
    /// Whether a press that has travelled this far should become a drag. Thresholds are passed in
    /// rather than read from <see cref="SystemParameters"/> so the rule is decidable without a
    /// desktop; the caller supplies the real ones.
    /// </summary>
    internal static bool ShouldStartRowDrag(double movedX, double movedY, double minimumX, double minimumY) =>
        Math.Abs(movedX) >= minimumX || Math.Abs(movedY) >= minimumY;

    /// <summary>
    /// Drags an item out to another window.
    ///
    /// Ordering here is load-bearing and was got wrong once. The palette is topmost over the middle
    /// of the screen, so the field being aimed at is usually underneath it, and the first attempt
    /// concealed the palette and then called DoDragDrop. No drag ever started. WinShot's history
    /// window does the same gesture and works, and the difference is that it calls DoDragDrop from
    /// the element under the pointer while its window is still up. So: start the drag first, then
    /// get out of the way from inside the first GiveFeedback, which only runs once the OLE loop is
    /// already spinning and owns the mouse.
    ///
    /// <see cref="DragDrop.DoDragDrop"/> is modal: it does not return until the button comes up or
    /// the drag is cancelled. A completed drop ends the visit, exactly as a paste does. A drag that
    /// dropped on nothing — Escape, or a release over the desktop — brings the palette back, so a
    /// misfire costs a reopen rather than the whole list.
    /// </summary>
    private void BeginRowDrag(ClipboardHistoryItem item, DependencyObject? source)
    {
        var hydrated = ClipboardItemForPasteFormat(item);
        if (BuildDragData(hydrated, _settings.DefaultPasteFormat) is not { } data)
        {
            return;
        }

        // The drag source must be a live element in a window that is still up — passing the Window
        // itself, or an element of a window already concealed, is what silently produced no drag.
        var dragSource = source ?? ItemsHost;

        // Same reason the window drag drops capture first: the row would otherwise keep the mouse
        // and stay stuck in its hover visual, never seeing the button-up the OS loop now owns.
        Mouse.Capture(null);

        // Built before the drag rather than inside the feedback tick: decoding a thumbnail while
        // the OLE loop owns the mouse would stutter the first frames of the drag. Null is a
        // perfectly good answer — the drag then looks exactly as it did before this existed.
        var preview = BuildDragPreviewContent(hydrated);
        if (preview is not null)
        {
            try
            {
                _dragPreview ??= new DragPreview();
            }
            catch (Exception ex)
            {
                // Same rule as the content: no preview is a fine outcome, no drag is not.
                ShellLog.Error(ex, "drag preview window failed");
                preview = null;
            }
        }

        var concealed = false;
        System.Windows.GiveFeedbackEventHandler feedback = (_, e) =>
        {
            e.UseDefaultCursors = true;
            e.Handled = true;
            if (concealed)
            {
                _dragPreview?.MoveToCursor();
                return;
            }

            // First feedback tick: the loop has the mouse now, so the palette can leave without
            // taking the drag with it. Conceal is the same teardown a paste does, which is what
            // keeps the outside-click watch and the low-level mouse hook from being left running
            // behind a window that is no longer there.
            concealed = true;
            ConcealPalette("drag-out");

            // After the conceal, so the palette's own topmost window cannot end up over the
            // preview. The preview is not owned by the palette, so the conceal leaves it alone.
            // Guarded because this runs inside the OLE loop: an exception escaping here would
            // take the drag down with it.
            try
            {
                if (preview is not null)
                {
                    _dragPreview?.Show(preview, (WpfBrush)FindResource("Surface"));
                }
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "drag preview show failed");
                preview = null;
            }
        };

        System.Windows.DragDropEffects effect;
        System.Windows.DragDrop.AddGiveFeedbackHandler(dragSource, feedback);
        try
        {
            // Copy only. Dragging a clip out is a copy of history, never a move: the row has to
            // still be there afterwards, so no target is ever offered Move.
            effect = System.Windows.DragDrop.DoDragDrop(dragSource, data, System.Windows.DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "row drag failed");
            effect = System.Windows.DragDropEffects.None;
        }
        finally
        {
            System.Windows.DragDrop.RemoveGiveFeedbackHandler(dragSource, feedback);
            // Every exit lands here — dropped, cancelled with Escape, or thrown out of the loop —
            // and a preview left on screen after any of them would be a window stuck to the
            // cursor with no way to dismiss it.
            _dragPreview?.Hide();
        }

        ShellLog.Info($"row drag id={item.Id} kind={item.Kind} concealed={concealed} effect={effect}");
        if (effect == System.Windows.DragDropEffects.None)
        {
            ShowPalette();
        }
    }

    /// <summary>The one preview window, created on the first drag that has something to show.</summary>
    private DragPreview? _dragPreview;

    /// <summary>
    /// The visual that rides under the cursor for this item, or null when there is nothing worth
    /// showing. Every path here is allowed to give up, and the whole thing is wrapped: a drag must
    /// never fail because its preview could not be built, so a failure here simply drags with the
    /// plain cursor the way it always did.
    /// </summary>
    private FrameworkElement? BuildDragPreviewContent(ClipboardHistoryItem item)
    {
        try
        {
            var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (DragPreviewImagePath(item) is { } imagePath)
            {
                // Decoded for the monitor the drag starts on, then fitted by Uniform inside a
                // square of MaxImageEdge, which is what puts the longest edge on 180 whether the
                // clip is a portrait photo or a wide screenshot. DownOnly so something already
                // smaller than the box is shown at its own size rather than blown up into mush.
                var picture = new WpfImage
                {
                    Source = LoadBitmap(imagePath, (int)Math.Round(DragPreview.MaxImageEdge * Math.Max(1.0, dpiScale))),
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    MaxWidth = DragPreview.MaxImageEdge,
                    MaxHeight = DragPreview.MaxImageEdge,
                };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                return WrapDragPreview(picture, new Thickness(3));
            }

            var label = DragPreviewLabelFor(item);
            if (label.Length == 0)
            {
                return null;
            }

            var text = new TextBlock
            {
                Text = label,
                Foreground = (WpfBrush)FindResource("Text"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // A file whose shell thumbnail is already cached gets it as the card's lead — the same
            // picture the row is showing. Cache-only on purpose: the cold call can spend hundreds
            // of milliseconds inside the shell, and the gesture is already moving by now.
            if (item.Kind == ClipboardItemKind.Files &&
                item.FilePaths.Count == 1 &&
                SourceAppIcons.TryGetCachedThumbnail(item.FilePaths[0], RowIconLogicalSize, dpiScale, out var thumbnail) &&
                thumbnail is not null)
            {
                var stack = new StackPanel { Orientation = WpfOrientation.Horizontal };
                stack.Children.Add(new WpfImage
                {
                    Source = thumbnail,
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                stack.Children.Add(text);
                return WrapDragPreview(stack, new Thickness(8, 6, 10, 6));
            }

            return WrapDragPreview(text, new Thickness(9, 6, 9, 6));
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"drag preview failed id={item.Id}");
            return null;
        }
    }

    /// <summary>
    /// The chip the preview shows: the palette's own surface, line and 6px radius, so a dragged
    /// clip reads as the row that left the list rather than as a tooltip. The preview window
    /// cannot be transparent — WPF drops to software rendering the moment a window allows it, and
    /// a juddering preview is worse than square corners — so the window behind this is filled with
    /// the same Surface brush and the rounding shows in the border, not in the silhouette.
    /// </summary>
    private Border WrapDragPreview(UIElement content, Thickness padding) => new()
    {
        Background = (WpfBrush)FindResource("Surface"),
        BorderBrush = (WpfBrush)FindResource("Line"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = padding,
        Child = content,
    };

    /// <summary>
    /// The picture a drag of this item should show, when it has one. An image item points at its
    /// stored asset; a single dropped image file is the same thing arriving as a path, and both
    /// want the picture rather than a card with a file name on it. Anything else — a video, a
    /// document — is left to the card, because getting a frame out of it means a shell call this
    /// path cannot afford.
    /// </summary>
    private static string? DragPreviewImagePath(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Image && item.AssetPath is { } asset && File.Exists(asset))
        {
            return asset;
        }

        if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1)
        {
            var path = item.FilePaths[0];
            if (File.Exists(path) && IsImageFile(Path.GetExtension(path).ToLowerInvariant()))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// The words on a text card. Files show their name — the whole point of dragging one out —
    /// with a count when the clip carries several; everything else shows the start of its text.
    /// </summary>
    private static string DragPreviewLabelFor(ClipboardHistoryItem item)
    {
        if (item.Kind != ClipboardItemKind.Files)
        {
            return DragPreview.CardLabel(TextPayload(item));
        }

        if (item.FilePaths.Count == 0)
        {
            return string.Empty;
        }

        var path = item.FilePaths[0];
        // A trailing separator on a folder would otherwise leave GetFileName with nothing to say.
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var label = DragPreview.CardLabel(string.IsNullOrWhiteSpace(name) ? path : name);
        return item.FilePaths.Count > 1 ? $"{label}  +{item.FilePaths.Count - 1} more" : label;
    }

    /// <summary>
    /// Fills the drag's data object from <see cref="ClipboardDragData"/>. Null when the item has
    /// nothing to offer, which is the signal not to start a drag at all — an empty data object
    /// would give every target the no-drop cursor and look like a bug.
    /// </summary>
    private System.Windows.DataObject? BuildDragData(ClipboardHistoryItem item, PasteFormatPreference pasteFormat)
    {
        var payload = ClipboardDragData.Create(item, pasteFormat);
        if (payload.IsEmpty)
        {
            return null;
        }

        var data = new System.Windows.DataObject();
        if (payload.Text is { } text)
        {
            // Both names for the same string. Modern targets ask for UnicodeText; a few older ones
            // only ever ask for Text, and this is the format that has to work in a plain field.
            data.SetText(text.Text, System.Windows.TextDataFormat.UnicodeText);
            data.SetText(text.Text, System.Windows.TextDataFormat.Text);
            if (text.Html is not null)
            {
                data.SetText(text.Html, System.Windows.TextDataFormat.Html);
            }

            if (text.Rtf is not null)
            {
                data.SetText(text.Rtf, System.Windows.TextDataFormat.Rtf);
            }
        }

        var paths = payload.FilePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

        if (item.Kind == ClipboardItemKind.Link && payload.Text is { Text.Length: > 0 } link)
        {
            // CFSTR_INETURLW, the format every browser puts on a dragged link. Explorer turns it
            // into a real .url shortcut on drop, and no text target has ever asked for it, so
            // unlike a FileDrop it costs the common case nothing — which is why it is offered
            // unconditionally while the file below is not. Raw bytes through a MemoryStream
            // because the shell wants a null-terminated wide string in an HGLOBAL; a plain string
            // would be handed over as whatever WPF chooses to serialise it into.
            data.SetData(
                "UniformResourceLocatorW",
                new MemoryStream(Encoding.Unicode.GetBytes(link.Text.Trim() + "\0")));
        }

        // Only when the item is not already files, and only when the user has asked for it: a
        // FileDrop on a text clip changes what every other app does with the drag. See
        // MaterializeDragFile.
        if (paths.Count == 0 && _settings.DragClipsAsFiles && payload.Text is { } fileText &&
            MaterializeDragFile(item.Kind, fileText.Text) is { } materialized)
        {
            paths.Add(materialized);
        }

        if (paths.Count > 0)
        {
            data.SetData(System.Windows.DataFormats.FileDrop, paths.ToArray());
        }

        if (payload.BitmapPath is { } bitmapPath && File.Exists(bitmapPath))
        {
            try
            {
                // SetImage is safe here in a way Clipboard.SetImage is not: nothing flushes, so
                // the FailFast that a big photo triggered on the clipboard path cannot run. A
                // decode that fails still leaves the FileDrop above, which most targets prefer.
                data.SetImage(LoadBitmap(bitmapPath));
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, $"drag bitmap decode failed path={bitmapPath}");
            }
        }

        return data;
    }

    /// <summary>
    /// Writes the clip out as a real file and returns its path, so a drop on the desktop leaves a
    /// .txt or a .url behind the way a dropped image leaves a .png.
    ///
    /// Gated on a setting, and off by default, because the FileDrop this produces is not free.
    /// Plenty of apps prefer a file to text when a drag offers both — VS Code opens it in a new
    /// tab, Slack and Gmail attach it — so switching this on trades "drag text into a field",
    /// which is the everyday gesture, for "drag text onto the desktop", which is the rare one.
    /// Format order cannot rescue that: WPF's DataObject keeps its formats in a hash table, so
    /// the order the shell enumerates them in is not insertion order and is not even stable
    /// between runs, and the apps that matter query for CF_HDROP by name regardless.
    /// </summary>
    private static string? MaterializeDragFile(ClipboardItemKind kind, string text)
    {
        try
        {
            var folder = ClipStoragePaths.DragFilesFolderPath;

            // Swept here rather than on a timer: a drag is the only thing that ever puts a file
            // in this folder, so it is the only moment the folder can have grown.
            ClipboardDragFile.CleanStale(folder, DateTime.UtcNow);
            return ClipboardDragFile.Materialize(folder, kind, text);
        }
        catch (Exception ex)
        {
            // A file we could not write is a missing convenience. It is never a reason to fail
            // the drag, which still carries the text.
            ShellLog.Error(ex, $"drag file materialise failed kind={kind}");
            return null;
        }
    }

    private void OnTitleTextMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && _selected is not null)
        {
            RenameItem(_selected);
            e.Handled = true;
        }
    }

    private void ShowActionMenu(ClipboardHistoryItem item, UIElement? target = null)
    {
        _menuItem = item;
        var actions = new List<MenuAction>
        {
            new("Paste", PasteSelected, true, shortcut: _settings.Hotkeys.PasteSelected),
            new("Copy", CopySelected, true, shortcut: _settings.Hotkeys.CopySelected),
            new("Rename", () => RenameItem(item)),
            new(item.IsPinned ? "Unpin" : "Pin", () => TogglePin(item), true, shortcut: _settings.Hotkeys.PinSelected),
            new("Move Pin Up", () => MovePin(item, -1), CanMovePin(item, -1)),
            new("Move Pin Down", () => MovePin(item, 1), CanMovePin(item, 1)),
            MenuAction.Separator,
        };

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            actions.Insert(2, new MenuAction(
                "Paste as Plain Text",
                () => PasteSelected(PasteFormatPreference.PlainText),
                true,
                shortcut: "Ctrl+Shift+V"));
        }

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            actions.Add(new MenuAction("Edit Text", () => EditText(item), true, shortcut: _settings.Hotkeys.EditSelected));
            actions.Add(new MenuAction("Append to Clipboard", () => AppendText(item)));
            AddTransformSubmenu(actions, item);
        }

        if (CanCopyOcrText(item.Kind, item.OcrText, _settings.ExtractTextFromImages, OcrTextExtractor.IsAvailable))
        {
            actions.Add(new MenuAction("Copy Text", () => CopyOcrText(item)));
        }

        if (item.Kind == ClipboardItemKind.Link)
        {
            actions.Add(new MenuAction("Open", () => OpenItem(item), true, shortcut: _settings.Hotkeys.OpenSelected));
        }

        if (item.Kind is ClipboardItemKind.Image or ClipboardItemKind.Files)
        {
            actions.Add(MenuAction.Separator);
            actions.Add(new MenuAction("Open", () => OpenItem(item), true, shortcut: "Ctrl+O"));
            actions.Add(new MenuAction("Open With...", () => OpenWith(item)));
        }

        var revealPath = ClipboardItemRevealTarget.GetPath(item);
        if (revealPath is not null)
        {
            if (item.Kind is not (ClipboardItemKind.Image or ClipboardItemKind.Files))
            {
                actions.Add(MenuAction.Separator);
            }

            actions.Add(new MenuAction("Show in File Explorer", () => ShowInFileExplorer(item)));
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            actions.Add(new MenuAction("Copy path", () => CopyPath(item)));
        }

        var shareActions = new List<MenuAction>();
        if (BlipShareLaunchPlan.IsInstalled())
        {
            shareActions.Add(new MenuAction("Blip", () => ShareWithBlip(item)));
        }

        shareActions.Add(new MenuAction("Windows Share...", () => ShareItem(item)));
        actions.Add(MenuAction.Separator);
        actions.Add(MenuAction.Submenu("Share", shareActions));
        actions.Add(new MenuAction("Save as File...", () => SaveItem(item)));
        actions.Add(new MenuAction("Delete", () => DeleteItem(item), true, danger: true, shortcut: "Del"));
        ShowStyledMenu(actions, target);
    }

    /// <summary>
    /// Offers only the transforms that would actually do something to this item. A transform
    /// whose result equals the text — lowercasing text that is already lowercase, extracting URLs
    /// from a paragraph with none — is a row that looks like it works and then silently does not,
    /// so it is left out rather than shown disabled.
    /// </summary>
    private void AddTransformSubmenu(List<MenuAction> actions, ClipboardHistoryItem item)
    {
        var text = FullTextPayload(item);
        var offers = TransformOffers(text);
        if (offers.Count == 0)
        {
            return;
        }

        actions.Add(MenuAction.Submenu(
            "Transform",
            offers.Select(o => new MenuAction(o.Label, () => CopyTransformed(o.Label, o.Result))).ToList()));
    }

    /// <summary>
    /// The Transform rows for a piece of text, in menu order.
    ///
    /// The five reshaping rows are always offered, even when one would hand back the same text.
    /// Hiding no-ops made the submenu change shape from item to item — on a tidy one-line URL only
    /// the three case rows survived, which reads as most of the feature being missing rather than
    /// as "those would do nothing here". "Copy links only" is the exception: its absence is
    /// informative (this text has no links in it) rather than baffling, so it is dropped when it
    /// would return the text unchanged or nothing at all.
    /// </summary>
    internal static IReadOnlyList<(string Label, string Result)> TransformOffers(string text)
    {
        var offers = new List<(string, string)>();
        if (string.IsNullOrEmpty(text))
        {
            return offers;
        }

        void Offer(string label, Func<string, string> transform, bool onlyWhenItChanges = false)
        {
            var result = transform(text);
            if (result.Length == 0 || (onlyWhenItChanges && string.Equals(result, text, StringComparison.Ordinal)))
            {
                return;
            }

            offers.Add((label, result));
        }

        Offer("UPPERCASE", ClipboardTextTransforms.Upper);
        Offer("lowercase", ClipboardTextTransforms.Lower);
        Offer("Title Case", ClipboardTextTransforms.TitleCase);
        Offer("Trim spaces and blank lines", ClipboardTextTransforms.Trim);
        Offer("Join into one line", ClipboardTextTransforms.SingleLine);
        Offer("Copy links only", ClipboardTextTransforms.ExtractUrls, onlyWhenItChanges: true);
        return offers;
    }

    /// <summary>
    /// A transform earns a menu row only when it would change something. One that returns the
    /// text unchanged, or nothing at all — Extract URLs over a paragraph with no links — reads as
    /// a working command and then silently does nothing, so it is left out rather than greyed.
    /// </summary>
    internal static bool ShouldOfferTransform(string source, string result) =>
        result.Length > 0 && !string.Equals(result, source, StringComparison.Ordinal);

    /// <summary>
    /// Transforms hand back a copy rather than rewriting the item: the stored clipboard entry is
    /// a record of what was copied, and quietly rewriting history would lose the original.
    /// </summary>
    private void CopyTransformed(string label, string result)
    {
        SetClipboardText(result);
        ShowToast($"Copied · {label}");
        ShellLog.Info($"transform copied label={label} length={result.Length}");
    }

    /// <summary>
    /// Recognized image text is only worth offering when it exists. OCR is off by default for
    /// privacy and needs a Windows language pack, so with either missing the action must not
    /// appear at all — a row that can only fail is worse than no row.
    /// </summary>
    internal static bool CanCopyOcrText(ClipboardItemKind kind, string? ocrText, bool extractTextEnabled, bool ocrEngineAvailable) =>
        kind == ClipboardItemKind.Image &&
        extractTextEnabled &&
        ocrEngineAvailable &&
        !string.IsNullOrWhiteSpace(ocrText);

    private void CopyOcrText(ClipboardHistoryItem item)
    {
        // The list rows carry a capped copy of OcrText so the summary index stays small, so the
        // full text has to come from the store or a screenshot's transcript arrives truncated.
        var text = _store.GetItem(item.Id)?.OcrText ?? item.OcrText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SetClipboardText(text);
        ShowToast("Copied text");
        ShellLog.Info($"ocr text copied id={item.Id} length={text.Length}");
    }

    /// <summary>
    /// Plain text onto the clipboard through the same Win32 path <see cref="SetClipboard"/> uses.
    /// Not WPF's Clipboard.SetText: it answers a failed OLE flush with FailFast, which has killed
    /// Clip outright before.
    /// </summary>
    private void SetClipboardText(string text)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Win32ClipboardWriter.TrySetText(hwnd, text, null, null))
        {
            return;
        }

        ShellLog.Snapshot("clipboard win32 set text failed; falling back to WPF without flush");
        var data = new System.Windows.DataObject();
        data.SetText(text, System.Windows.TextDataFormat.UnicodeText);
        System.Windows.Clipboard.SetDataObject(data, copy: false);
    }

    private void ShowStyledMenu(IEnumerable<MenuAction> actions, UIElement? target)
    {
        ActionMenuHost.Children.Clear();
        _menuRows.Clear();
        _menuHighlightIndex = -1;
        ShareSubmenuPopup.IsOpen = false;
        // Menu builders append separators per section; when a section contributes nothing the
        // separators land back to back, so consecutive (and leading) ones collapse here.
        var lastWasSeparator = true;
        foreach (var action in actions)
        {
            if (action.IsSeparator)
            {
                if (lastWasSeparator)
                {
                    continue;
                }

                ActionMenuHost.Children.Add(new Border { Height = 1, Background = (WpfBrush)FindResource("Line"), Margin = new Thickness(4, 4, 4, 4) });
                lastWasSeparator = true;
                continue;
            }

            lastWasSeparator = false;

            var row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Background = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = action.Enabled ? 1.0 : 0.45,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = action.Label,
                Foreground = action.Danger ? (WpfBrush)FindResource("Danger") : (WpfBrush)FindResource("Text"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(label);
            if (!string.IsNullOrWhiteSpace(action.Shortcut))
            {
                var shortcut = new TextBlock
                {
                    Text = action.Shortcut,
                    Foreground = (WpfBrush)FindResource("Muted"),
                    FontSize = 11,
                    Margin = new Thickness(20, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(shortcut, 1);
                grid.Children.Add(shortcut);
            }
            else if (action.Children.Count > 0)
            {
                var arrow = new TextBlock
                {
                    Text = ">",
                    Foreground = (WpfBrush)FindResource("Muted"),
                    FontSize = 12,
                    Margin = new Thickness(20, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(arrow, 1);
                grid.Children.Add(arrow);
            }

            row.Child = grid;
            if (action.Enabled)
            {
                // Hover and arrow keys share one highlight, or the menu shows two "current"
                // rows the moment a mouse user touches the keyboard.
                var menuIndex = _menuRows.Count;
                _menuRows.Add((row, action));
                row.MouseEnter += (_, _) =>
                {
                    HighlightMenuRow(menuIndex);
                    if (action.Children.Count > 0)
                    {
                        ShowShareSubmenu(action.Children, row);
                    }
                    else
                    {
                        ShareSubmenuPopup.IsOpen = false;
                        ActionMenuPopup.StaysOpen = false;
                    }
                };
                row.MouseLeave += (_, _) =>
                {
                    row.Background = WpfBrushes.Transparent;
                    row.BorderBrush = WpfBrushes.Transparent;
                    if (_menuHighlightIndex == menuIndex)
                    {
                        _menuHighlightIndex = -1;
                    }
                };
                row.MouseLeftButtonDown += (_, e) =>
                {
                    if (action.Children.Count > 0)
                    {
                        ShowShareSubmenu(action.Children, row);
                    }
                    else
                    {
                        CloseActionMenus();
                        action.Invoke();
                        ShellLog.Info($"menu action label={action.Label} item={_menuItem?.Id ?? "none"}");
                    }

                    e.Handled = true;
                };
            }

            ActionMenuHost.Children.Add(row);
        }

        _suppressDeactivate = true;
        ActionMenuBorder.MinWidth = target is null ? 220 : 178;
        ActionMenuPopup.Placement = target is null ? PlacementMode.MousePoint : PlacementMode.Bottom;
        ActionMenuPopup.PlacementTarget = target;
        ActionMenuPopup.HorizontalOffset = target is null ? 0 : -8;
        ActionMenuPopup.VerticalOffset = target is null ? 0 : 6;
        ActionMenuPopup.IsOpen = true;
        ActionMenuPopup.Closed -= OnActionMenuClosed;
        ActionMenuPopup.Closed += OnActionMenuClosed;
        ShellLog.Info($"menu opened target={(target is null ? "mouse" : target.GetType().Name)} count={ActionMenuHost.Children.Count}");
    }

    private void ShowShareSubmenu(IReadOnlyList<MenuAction> actions, UIElement owner)
    {
        ShareSubmenuHost.Children.Clear();
        foreach (var action in actions)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Background = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = action.Enabled ? 1.0 : 0.45,
                MinWidth = 170,
            };
            row.Child = new TextBlock
            {
                Text = action.Label,
                Foreground = action.Danger ? (WpfBrush)FindResource("Danger") : (WpfBrush)FindResource("Text"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (action.Enabled)
            {
                row.MouseEnter += (_, _) =>
                {
                    row.Background = (WpfBrush)FindResource("AccentSoft");
                    row.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
                };
                row.MouseLeave += (_, _) =>
                {
                    row.Background = WpfBrushes.Transparent;
                    row.BorderBrush = WpfBrushes.Transparent;
                };
                row.MouseLeftButtonDown += (_, e) =>
                {
                    CloseActionMenus();
                    action.Invoke();
                    ShellLog.Info($"submenu action label={action.Label} item={_menuItem?.Id ?? "none"}");
                    e.Handled = true;
                };
            }

            ShareSubmenuHost.Children.Add(row);
        }

        ShareSubmenuPopup.PlacementTarget = owner;
        ShareSubmenuPopup.HorizontalOffset = 4;
        ShareSubmenuPopup.VerticalOffset = -4;
        ActionMenuPopup.StaysOpen = true;
        ShareSubmenuPopup.StaysOpen = true;
        ShareSubmenuPopup.IsOpen = true;
    }

    private void CloseActionMenus()
    {
        ShareSubmenuPopup.IsOpen = false;
        ActionMenuPopup.IsOpen = false;
        ShareSubmenuPopup.StaysOpen = false;
        ActionMenuPopup.StaysOpen = false;
    }

    private void HighlightMenuRow(int index)
    {
        if (_menuHighlightIndex >= 0 && _menuHighlightIndex < _menuRows.Count)
        {
            var (oldRow, _) = _menuRows[_menuHighlightIndex];
            oldRow.Background = WpfBrushes.Transparent;
            oldRow.BorderBrush = WpfBrushes.Transparent;
        }

        _menuHighlightIndex = index;
        if (index < 0 || index >= _menuRows.Count)
        {
            return;
        }

        var (row, _) = _menuRows[index];
        row.Background = (WpfBrush)FindResource("AccentSoft");
        row.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
    }

    /// <summary>
    /// Arrow/Enter handling while the action menu is open. Focus never leaves the search box —
    /// the popup takes no keyboard focus — so the keys arrive on the window's tunnel pass and
    /// are steered here. Escape stays with OnWindowKeyDown's close branch. Returns true when
    /// the key was consumed.
    /// </summary>
    private bool HandleActionMenuKey(System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up or Key.Down:
                if (_menuRows.Count > 0)
                {
                    var next = _menuHighlightIndex < 0
                        ? (e.Key == Key.Down ? 0 : _menuRows.Count - 1)
                        : Math.Clamp(_menuHighlightIndex + (e.Key == Key.Down ? 1 : -1), 0, _menuRows.Count - 1);
                    HighlightMenuRow(next);
                }

                e.Handled = true;
                return true;
            case Key.Enter:
                if (_menuHighlightIndex >= 0 && _menuHighlightIndex < _menuRows.Count)
                {
                    var (row, action) = _menuRows[_menuHighlightIndex];
                    if (action.Children.Count > 0)
                    {
                        // The submenu stays mouse-operated; Enter just gets it on screen.
                        ShowShareSubmenu(action.Children, row);
                    }
                    else
                    {
                        CloseActionMenus();
                        action.Invoke();
                        ShellLog.Info($"menu action label={action.Label} item={_menuItem?.Id ?? "none"} via=keyboard");
                    }
                }

                e.Handled = true;
                return true;
        }

        return false;
    }

    private void OnActionMenuClosed(object? sender, EventArgs e)
    {
        ShareSubmenuPopup.IsOpen = false;
        ShareSubmenuPopup.StaysOpen = false;
        ActionMenuPopup.StaysOpen = false;
        _suppressDeactivate = false;
        _menuItem = null;
        ShellLog.Info("menu closed");
    }

    /// <summary>Whether the current selection was picked by the list rather than by the user.</summary>
    private bool _selectionIsAutomatic;

    private void SelectItem(ClipboardHistoryItem? item, string reason)
    {
        if (item is null || item.Id == _selected?.Id)
        {
            ShellLog.Info($"selection skipped reason={reason} id={item?.Id ?? "none"}");
            return;
        }

        // A reconcile is the list's choice, not the user's, so it must stay replaceable just
        // like the initial selection (the caller sets _selectionIsAutomatic itself).
        if (reason is not ("initial" or "reconcile"))
        {
            _selectionIsAutomatic = false;
        }

        if (_selected is not null && _rows.TryGetValue(_selected.Id, out var oldRow))
        {
            oldRow.Background = WpfBrushes.Transparent;
            oldRow.BorderBrush = WpfBrushes.Transparent;
        }

        _selected = item;
        if (_rows.TryGetValue(item.Id, out var newRow))
        {
            newRow.Background = (WpfBrush)FindResource("Selected");
            newRow.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
        }

        // The header is one shared element, so a slow thumbnail must not paint over whatever
        // item is selected by the time it arrives.
        HeaderIcon.Source = IconFor(item, 96, onRicher: richer =>
        {
            if (_selected?.Id == item.Id)
            {
                HeaderIcon.Source = richer;
            }
        });
        AttachFavicon(HeaderIcon, item);
        TitleText.Text = TitleFor(item);
        SubTitleText.Text = HeaderSubtitleFor(item);
        if (item.Kind == ClipboardItemKind.Text)
        {
            OpenButton.Content = "Edit";
            OpenButton.Visibility = Visibility.Visible;
        }
        else if (item.Kind is ClipboardItemKind.Link or ClipboardItemKind.Files or ClipboardItemKind.Image)
        {
            OpenButton.Content = "Open";
            OpenButton.Visibility = Visibility.Visible;
        }
        else
        {
            OpenButton.Visibility = Visibility.Collapsed;
        }
        RenderInfo(item);
        RenderPreview(item);
        PrefetchNeighbouringImages(item);
        ShellLog.Info($"selection changed reason={reason} id={item.Id} kind={item.Kind}");
    }

    /// <summary>
    /// Returns the right-hand pane to its neutral "nothing selected" state. Used when a search
    /// or filter leaves nothing visible: keeping the previous item's preview up would show an
    /// answer that no longer matches the (empty) list, and Enter would paste it unseen.
    /// </summary>
    private void ClearSelection()
    {
        _selected = null;
        _selectionIsAutomatic = false;
        // Invalidate any in-flight preview render so it cannot repaint the pane after the clear.
        _previewToken++;
        _previewItemId = null;
        _previewSourceStamp = null;
        HidePreviews();
        HeaderIcon.Source = null;
        TitleText.Text = "Clipboard";
        SubTitleText.Text = "Search, preview, and act on copied items";
        OpenButton.Visibility = Visibility.Collapsed;
        InfoHost.Children.Clear();
        ShellLog.Info("selection cleared");
    }

    private void UpdateEmptyState(int visibleCount)
    {
        if (visibleCount > 0)
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            return;
        }

        var query = SearchBox.Text;
        EmptyStateText.Text = !string.IsNullOrWhiteSpace(query)
            ? $"No matches for “{query.Trim()}”"
            : _allItems.Count == 0
                ? "Copy something to get started..."
                : "Nothing matches this filter";
        EmptyStateText.Visibility = Visibility.Visible;
    }

    /// <summary>The item the preview pane was last asked to show, for skipping redundant repeats.</summary>
    private string? _previewItemId;

    /// <summary>
    /// What the previewed file looked like when it was rendered, so a file edited while the palette
    /// was closed is not shown stale. Null for items with no file behind them.
    /// </summary>
    private string? _previewSourceStamp;

    private static string? SourceStampOf(ClipboardHistoryItem item)
    {
        var path = item.FilePaths?.FirstOrDefault() ?? item.AssetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}" : null;
        }
        catch
        {
            // Unreadable is a good enough reason to render again rather than trust what is up.
            return null;
        }
    }

    /// <summary>
    /// Is the browser pane already showing this item?
    ///
    /// Reopening the palette re-rendered the preview unconditionally, on the stated grounds that
    /// concealing tears the WebView2 down. It does not — it starts a three-minute idle timer, so a
    /// palette reopened within three minutes still has the page loaded and visible, and navigating
    /// to it again costs the whole 70–540ms load to arrive at the pixels already on screen. It also
    /// throws away a video's position.
    ///
    /// Only claims the pane is current when the browser is alive AND visible, which means the last
    /// navigation actually completed and was revealed; a failed or superseded one leaves it hidden
    /// and this returns false.
    /// </summary>
    private bool PreviewAlreadyShowing(ClipboardHistoryItem item)
    {
        try
        {
            return _previewItemId == item.Id &&
                _previewSourceStamp is not null &&
                _previewSourceStamp == SourceStampOf(item) &&
                _htmlPreview is Microsoft.Web.WebView2.Wpf.WebView2 { CoreWebView2: not null, Visibility: Visibility.Visible };
        }
        catch (Exception ex)
        {
            // The CoreWebView2 getter rethrows whatever failed during initialization, so a browser
            // that never came up must read as "not showing" — this check sits on the open path,
            // and letting it throw took the whole palette down with it.
            ShellLog.Error(ex, "preview-already-showing check failed");
            return false;
        }
    }

    private void RenderPreview(ClipboardHistoryItem item)
    {
        var token = ++_previewToken;
        _previewItemId = item.Id;
        _previewSourceStamp = SourceStampOf(item);
        HidePreviews();

        try
        {
            if (item.Kind == ClipboardItemKind.Color)
            {
                ColorPreviewSwatch.Fill = BrushFromHex(TextPayload(item));
                ColorPreviewText.Text = TextPayload(item);
                ColorPreview.Visibility = Visibility.Visible;
                BenchMarks.Mark("preview-ready");
                ShellLog.Info($"preview color id={item.Id} hex={TextPayload(item)}");
                return;
            }

            if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
            {
                TextPreview.Text = TextFilePreviewReader.Format(FullTextPayload(item), TextPreviewCharacterLimit);
                TextPreview.Foreground = (WpfBrush)FindResource("Text");
                TextPreview.Visibility = Visibility.Visible;
                BenchMarks.Mark("preview-ready");
                ShellLog.Info($"preview text id={item.Id} chars={TextPreview.Text.Length}");
                return;
            }

            if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
            {
                ShowImagePreview(item, item.AssetPath, token);
                ShellLog.Info($"preview image id={item.Id} path={item.AssetPath}");
                return;
            }

            if (item.Kind == ClipboardItemKind.Files)
            {
                var path = item.FilePaths.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(path))
                {
                    ShowPlaceholder(item, "No file selected");
                    return;
                }

                // A .png dropped in as a file is the same picture as one that was pasted, and gets
                // the same treatment: straight to the image, never a loading card.
                if (PreviewImagePathOf(item) is { } imagePath)
                {
                    ShowImagePreview(item, imagePath, token);
                    ShellLog.Info($"preview file image path={imagePath}");
                    return;
                }

                ShowPlaceholder(item, "Loading preview...");
                _ = LoadFilePreviewAsync(item, path, token);
                return;
            }

            ShowPlaceholder(item, item.Preview);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"preview failed id={item.Id}");
            ShowPlaceholder(item, "Preview unavailable");
        }
    }

    // This method starts on the UI thread and every await resumes there, so it mutates the pane
    // directly. It must NOT wrap work in `await Dispatcher.InvokeAsync(async ...)`: awaiting a
    // DispatcherOperation<Task> completes at the lambda's FIRST await and discards the inner task,
    // which made `shown` read as false while the live viewer was still loading — so the raster
    // fallback always ran, killed the navigation, and every Word/PDF preview flickered.
    // The "Loading preview..." placeholder from RenderPreview stays up until a branch has its
    // content fully ready; each branch swaps it out in a single dispatcher frame.
    private async Task LoadFilePreviewAsync(ClipboardHistoryItem item, string path, int token)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (Directory.Exists(path))
            {
                if (token != _previewToken) return;
                ShowPlaceholder(item, path);
                return;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (IsImageFile(ext))
            {
                await ShowImagePreviewAsync(item, path, token);
                ShellLog.Info($"preview file image path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            if (IsHtmlFile(ext))
            {
                await ShowHtmlPreviewAsync(path, token);
                ShellLog.Info($"preview html path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            if (IsVideoFile(ext) || IsAudioFile(ext))
            {
                await ShowMediaPreviewAsync(path, IsVideoFile(ext), token);
                ShellLog.Info($"preview media path={path} video={IsVideoFile(ext)} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            // Source files get a coloured, scrollable, selectable page instead of flat text.
            if (CodePreviewPage.IsCodeFile(ext))
            {
                await ShowCodePreviewAsync(path, token);
                ShellLog.Info($"preview code path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            if (IsTextFile(ext))
            {
                var text = await TextFilePreviewReader.ReadAsync(path, TextPreviewCharacterLimit);
                if (token != _previewToken) return;
                HidePreviews();
                TextPreview.Text = text;
                TextPreview.Visibility = Visibility.Visible;
                ShellLog.Info($"preview text-file path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            // A PDF opens in the WebView2's own viewer: scrollable, text-selectable and
            // searchable, instead of a flat picture of page one. Falls back to the rendered image
            // if that fails for any reason.
            if (ext == ".pdf")
            {
                if (await TryShowDocumentPreviewAsync(path, token))
                {
                    ShellLog.Info($"preview pdf-live path={path} elapsedMs={watch.ElapsedMilliseconds}");
                    return;
                }

                if (token != _previewToken) return;
            }

            // A workbook is read straight out of the file and drawn as a grid with a tab per sheet.
            // A .xlsx is a zip of XML, so this takes milliseconds and starts nothing, where asking
            // Excel to export the same workbook took the better part of twenty seconds the first
            // time and put a window on screen while it did. It is also the right shape: the export
            // showed sheets as pages of print output rather than as sheets.
            if (ExcelWorkbookReader.CanRead(path)
                && await Task.Run(() => ExcelWorkbookReader.TryRead(path)) is { } sheets)
            {
                if (token != _previewToken) return;
                await ShowWorkbookPreviewAsync(sheets, path, token);
                ShellLog.Info($"preview workbook path={path} sheets={sheets.Count} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            // Word and PowerPoint are exported to a cached PDF and shown in the same viewer
            // the .pdf branch uses. Rasterising instead threw away everything after the first page
            // along with the text layer, and only worked where a PDF rasteriser was installed —
            // and a document is not one page. A workbook arrives as a page per sheet and a deck as
            // a page per slide, so the viewer scrolls through them the way it scrolls any PDF.
            if (IsPdfBackedOfficeFile(ext))
            {
                var officePdf = await Task.Run(() => WatcherStaticDocumentPreviewRenderer.TryExportDocumentPdfOnStaThread(path));
                if (token != _previewToken) return;
                if (!string.IsNullOrWhiteSpace(officePdf) && await TryShowDocumentPreviewAsync(officePdf!, token))
                {
                    ShellLog.Info($"preview office-live path={path} elapsedMs={watch.ElapsedMilliseconds}");
                    return;
                }

                if (token != _previewToken) return;
            }

            DrawingImage? rendered = null;
            if (ext == ".pdf")
            {
                rendered = await Task.Run(() => WatcherPdfPreviewRenderer.TryRenderFirstPage(path, out var image) ? image : null);
            }
            else if (IsOfficeOrVisio(ext))
            {
                rendered = await Task.Run(() => WatcherStaticDocumentPreviewRenderer.TryRenderFirstPageOnStaThread(path));
            }

            if (rendered is not null)
            {
                if (token != _previewToken)
                {
                    rendered.Dispose();
                    return;
                }

                HidePreviews();
                ImagePreview.Source = BitmapFromDrawingImage(rendered);
                if (ext == ".pdf")
                {
                    _currentPreviewPdfPath = path;
                }

                ImagePreview.Visibility = Visibility.Visible;
                ExpandImageButton.Visibility = Visibility.Visible;
                rendered.Dispose();
                ShellLog.Info($"preview rendered file path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            // Guarded like every other branch: a slow render that finishes after the user has
            // moved on must not repaint the panel, which is how a Visio ended up showing under a
            // selected PDF.
            if (token != _previewToken) return;
            ShowPlaceholder(item, path);
            ShellLog.Info($"preview fallback file path={path} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"file preview failed path={path}");
            if (token != _previewToken) return;
            ShowPlaceholder(item, "Preview unavailable");
        }
    }

    /// <summary>
    /// Shows an image, without ever admitting to loading one.
    ///
    /// An already-decoded picture is assigned on the spot, so the next frame is the image and there
    /// is no intermediate state whatsoever — which, now that previews are cached, is what happens
    /// every time you come back to one. Only a genuinely new file needs decoding, and that happens
    /// off the UI thread with the *previous* preview left up until the new one is ready.
    ///
    /// What is deliberately gone is the "Loading preview..." card. It put a blown-up row thumbnail
    /// and a caption on screen for a few tens of milliseconds and then snapped to the real picture,
    /// which reads far worse than a moment of the previous image: it draws the eye to a wait that
    /// the swap would otherwise have hidden.
    /// </summary>
    private void ShowImagePreview(ClipboardHistoryItem item, string path, int token)
    {
        if (TryGetDecodedPreview(path, out var ready))
        {
            ApplyImagePreview(ready, path);
            return;
        }

        _ = ShowImagePreviewAsync(item, path, token);
    }

    private void ApplyImagePreview(ImageSource bitmap, string path)
    {
        HidePreviews();
        ImagePreview.Source = bitmap;
        _currentPreviewImagePath = path;
        ImagePreview.Visibility = Visibility.Visible;
        ExpandImageButton.Visibility = Visibility.Visible;
        BenchMarks.Mark("preview-ready");
    }

    private async Task ShowImagePreviewAsync(ClipboardHistoryItem item, string path, int token)
    {
        try
        {
            // LoadBitmap freezes what it returns, so decoding off-thread is safe.
            var bitmap = await Task.Run(() => LoadCachedBitmap(path, PreviewImageDecodePixels));
            if (token != _previewToken) return;
            ApplyImagePreview(bitmap, path);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"image preview failed path={path}");
            if (token != _previewToken) return;
            ShowPlaceholder(item, "Preview unavailable");
        }
    }

    /// <summary>
    /// Decodes the images either side of the selected one, quietly, so stepping through a run of
    /// screenshots with the arrow keys never waits for a disk read. Costs nothing visible: it runs
    /// off the UI thread and only fills the cache the preview is about to ask for.
    /// </summary>
    private void PrefetchNeighbouringImages(ClipboardHistoryItem selected)
    {
        var visible = LastRenderedVisibleItems();
        var index = -1;
        for (var i = 0; i < visible.Count; i++)
        {
            if (visible[i].Id == selected.Id)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var paths = new List<string>(2);
        foreach (var offset in new[] { 1, -1 })
        {
            var neighbour = index + offset;
            if (neighbour < 0 || neighbour >= visible.Count)
            {
                continue;
            }

            if (PreviewImagePathOf(visible[neighbour]) is { } path)
            {
                paths.Add(path);
            }
        }

        if (paths.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    LoadCachedBitmap(path, PreviewImageDecodePixels);
                }
                catch
                {
                    // A neighbour that will not decode is the preview's problem when it gets there.
                }
            }
        });
    }

    /// <summary>The file an item would show in the image pane, or null if it is not an image.</summary>
    private static string? PreviewImagePathOf(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Image)
        {
            return item.AssetPath is not null && File.Exists(item.AssetPath) ? item.AssetPath : null;
        }

        if (item.Kind != ClipboardItemKind.Files)
        {
            return null;
        }

        var path = item.FilePaths?.FirstOrDefault();
        return path is not null &&
            IsImageFile(Path.GetExtension(path).ToLowerInvariant()) &&
            File.Exists(path)
            ? path
            : null;
    }

    private void RenderInfo(ClipboardHistoryItem item)
    {
        InfoHost.Children.Clear();
        InfoScroll.ScrollToTop();
        AddSourceInfo(item);
        AddInfo("Content type", ContentType(item));

        // For an image the dimensions are the most useful fact about it, so they belong near the
        // top rather than after the copy counts where they scrolled out of view.
        if (item.Kind == ClipboardItemKind.Image)
        {
            if (item.ImageWidth is not null && item.ImageHeight is not null)
            {
                AddInfo("Dimensions", $"{item.ImageWidth} x {item.ImageHeight}");
            }

            AddInfo("Image size", FormatBytes(item.AssetSizeBytes ?? SizeOf(item.AssetPath)));
        }

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            AddInfo("Saved format", ClipboardPasteData.HasOriginalFormatting(item) ? "Plain text + formatting" : "Plain text");
        }

        AddInfo("Copied", item.LastCopiedAt.LocalDateTime.ToString("M/d/yyyy h:mm tt"));
        AddInfo("Times copied", item.CopyCount.ToString());

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            AddInfo("Characters", (item.CharacterCount ?? TextPayload(item).Length).ToString());
            AddInfo("Words", (item.WordCount ?? CountWords(TextPayload(item))).ToString());
        }

        if (item.Kind == ClipboardItemKind.Color)
        {
            AddInfo("Hex", TextPayload(item));
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            var paths = item.FilePaths;
            AddInfo("Files", paths.Count.ToString());
            if (paths.Count == 1)
            {
                var path = paths[0];
                AddInfo("File name", Path.GetFileName(path), scrollable: true);
                AddInfo("File type", Directory.Exists(path) ? "Folder" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant());
                AddInfo("File size", Directory.Exists(path) ? "Folder" : FormatBytes(SizeOf(path)));
                AddInfo("File path", path, scrollable: true);
            }
        }

        ShellLog.Info($"info rendered id={item.Id} kind={item.Kind} rows={InfoHost.Children.Count}");
    }

    /// <summary>
    /// Raycast-style reveal for an overlay scrollbar: fade in on a real user scroll, fade back
    /// out shortly after scrolling stops. One instance per ScrollViewer whose template has a
    /// PART_VerticalScrollBar starting at opacity 0.
    /// </summary>
    private sealed class OverlayBarFader
    {
        private readonly Func<ScrollViewer?> _viewer;
        private readonly System.Windows.Threading.DispatcherTimer _timer;

        public OverlayBarFader(Func<ScrollViewer?> viewer)
        {
            _viewer = viewer;
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Bar()?.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            };
        }

        private System.Windows.Controls.Primitives.ScrollBar? Bar()
        {
            var viewer = _viewer();
            return viewer?.Template?.FindName("PART_VerticalScrollBar", viewer) as System.Windows.Controls.Primitives.ScrollBar;
        }

        public void OnScroll(ScrollChangedEventArgs e)
        {
            // Only a user scroll shows the bar. A content swap also moves the offset when the
            // new extent clamps it, but that always comes with an extent change.
            if (e.VerticalChange == 0 || e.ExtentHeightChange != 0)
            {
                return;
            }

            Bar()?.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(80)));
            _timer.Stop();
            _timer.Start();
        }
    }

    private OverlayBarFader? _infoBarFader;
    private OverlayBarFader? _textPreviewBarFader;

    private void OnInfoScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        (_infoBarFader ??= new OverlayBarFader(() => InfoScroll)).OnScroll(e);
    }

    private void OnTextPreviewScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        (_textPreviewBarFader ??= new OverlayBarFader(() =>
            TextPreview.Template?.FindName("PART_ContentHost", TextPreview) as ScrollViewer)).OnScroll(e);
    }

    /// <summary>
    /// The Source row, with its icon resolved off the UI thread on a cache miss. The resolve goes
    /// through IShellItemImageFactory, and a cold hit there — a network exe, a packaged app the
    /// shell has not cached — blocks for long enough to be felt on an arrow step, because this row
    /// is rebuilt on every selection change. The slot is reserved so the value text does not shift
    /// when the icon lands.
    /// </summary>
    private void AddSourceInfo(ClipboardHistoryItem item)
    {
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (SourceAppIcons.TryGetCached(item.SourceAppUserModelId, item.SourceApplicationPath, 16, dpiScale, out var cached))
        {
            // A cached null means the identity was already tried and has no icon; the row
            // renders without one, as it always did.
            AddInfo("Source", SourceDisplayName(item), cached);
            return;
        }

        var image = AddInfo("Source", SourceDisplayName(item), icon: null, reserveIcon: true);
        var id = item.Id;
        SourceAppIcons.ResolveAsync(item.SourceAppUserModelId, item.SourceApplicationPath, 16, dpiScale, resolved =>
        {
            if (resolved is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                // The info panel is rebuilt per selection, so only paint if this row still
                // belongs to the item that is selected by the time the icon arrives.
                if (image is not null && _selected?.Id == id)
                {
                    image.Source = resolved;
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private WpfImage? AddInfo(string label, string value, ImageSource? icon = null, bool scrollable = false, bool reserveIcon = false)
    {
        var row = new Grid { MinHeight = 31 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new TextBlock
        {
            Text = label,
            Foreground = (WpfBrush)FindResource("Muted2"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        row.Children.Add(left);

        var valueHost = new DockPanel { LastChildFill = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        WpfImage? image = null;
        if (icon is not null || reserveIcon)
        {
            image = new WpfImage { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(image, Dock.Left);
            valueHost.Children.Add(image);
        }

        var text = new WpfTextBox
        {
            Text = value,
            Style = (Style)FindResource("CleanTextBox"),
            Foreground = (WpfBrush)FindResource("Text"),
            IsReadOnly = true,
            TextAlignment = TextAlignment.Right,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = scrollable ? TextWrapping.NoWrap : TextWrapping.Wrap,
            HorizontalScrollBarVisibility = scrollable ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
        };
        valueHost.Children.Add(text);
        Grid.SetColumn(valueHost, 1);
        row.Children.Add(valueHost);

        InfoHost.Children.Add(row);
        InfoHost.Children.Add(new Border { Height = 1, Background = (WpfBrush)FindResource("Line") });
        return image;
    }

    /// <summary>
    /// The visible list exactly as the last render computed it. Arrow keys, digit paste and the
    /// neighbour prefetch all fire per keystroke, and each used to re-run the whole filter chain
    /// over the full history to answer a question the last render had already answered. The list
    /// can only have drifted from the rows on screen if something marked the items dirty without
    /// re-rendering — then fall back to filtering fresh, exactly what the callers did before.
    /// </summary>
    private IReadOnlyList<ClipboardHistoryItem> LastRenderedVisibleItems() =>
        _renderedVisibleItems is not null && !_itemsDirtySinceRender ? _renderedVisibleItems : FilteredItems();

    private IReadOnlyList<ClipboardHistoryItem> FilteredItems()
    {
        IEnumerable<ClipboardHistoryItem> items = _allItems;
        items = _kindFilter switch
        {
            "text" => items.Where(i => i.Kind == ClipboardItemKind.Text),
            // "images" is the Media bucket: pasted images plus any copied image, video or audio
            // file. The sub-filters below narrow it to one kind.
            "images" => items.Where(i => i.Kind == ClipboardItemKind.Image || (i.Kind == ClipboardItemKind.Files && i.FilePaths.Any(p => IsMediaFile(Path.GetExtension(p).ToLowerInvariant())))),
            "media-images" => items.Where(i => i.Kind == ClipboardItemKind.Image || (i.Kind == ClipboardItemKind.Files && i.FilePaths.Any(p => IsImageFile(Path.GetExtension(p).ToLowerInvariant())))),
            "media-videos" => items.Where(i => i.Kind == ClipboardItemKind.Files && i.FilePaths.Any(p => IsVideoFile(Path.GetExtension(p).ToLowerInvariant()))),
            "media-audio" => items.Where(i => i.Kind == ClipboardItemKind.Files && i.FilePaths.Any(p => IsAudioFile(Path.GetExtension(p).ToLowerInvariant()))),
            "links" => items.Where(i => i.Kind == ClipboardItemKind.Link),
            "files" => items.Where(i => i.Kind == ClipboardItemKind.Files),
            "colors" => items.Where(i => i.Kind == ClipboardItemKind.Color),
            _ => items,
        };

        if (_dateFilter != "all")
        {
            var today = DateTime.Today;
            items = items.Where(i => DateKey(i, today) == _dateFilter);
        }

        if (_kindFilter == "files" && _fileFilter != "all")
        {
            items = items.Where(i => i.FilePaths.Any(path => FileKindKey(path) == _fileFilter));
        }

        return items.ToList();
    }

    private static IEnumerable<(string Header, List<ClipboardHistoryItem> Items)> GroupItems(IEnumerable<ClipboardHistoryItem> items)
    {
        var pinned = new List<ClipboardHistoryItem>();
        var todayItems = new List<ClipboardHistoryItem>();
        var yesterday = new List<ClipboardHistoryItem>();
        var week = new List<ClipboardHistoryItem>();
        var month = new List<ClipboardHistoryItem>();
        var year = new List<ClipboardHistoryItem>();
        var older = new List<ClipboardHistoryItem>();
        var today = DateTime.Today;

        foreach (var item in items)
        {
            if (item.IsPinned)
            {
                pinned.Add(item);
                continue;
            }

            switch (DateKey(item, today))
            {
                case "today":
                    todayItems.Add(item);
                    break;
                case "yesterday":
                    yesterday.Add(item);
                    break;
                case "week":
                    week.Add(item);
                    break;
                case "month":
                    month.Add(item);
                    break;
                case "year":
                    year.Add(item);
                    break;
                default:
                    older.Add(item);
                    break;
            }
        }

        pinned.Sort((left, right) => left.PinOrder.CompareTo(right.PinOrder));
        SortByLastCopied(todayItems);
        SortByLastCopied(yesterday);
        SortByLastCopied(week);
        SortByLastCopied(month);
        SortByLastCopied(year);
        SortByLastCopied(older);

        yield return ("Pinned items", pinned);
        yield return ("Today", todayItems);
        yield return ("Yesterday", yesterday);
        yield return ("This week", week);
        yield return ("This month", month);
        yield return ("This year", year);
        yield return ("Older", older);
    }

    private void SetFilter(string kind)
    {
        _kindFilter = kind;
        if (kind != "files")
        {
            _fileFilter = "all";
        }

        var visibleItems = RenderItems($"filter-{kind}");
        SelectItem(visibleItems.FirstOrDefault(), $"filter-{kind}");
        ShellLog.Info($"filter changed kind={kind} date={_dateFilter} file={_fileFilter}");
    }

    private void TogglePin(ClipboardHistoryItem item)
    {
        var next = !item.IsPinned;
        if (_store.SetPinned(item.Id, next))
        {
            item.IsPinned = next;
            _allItems = _store.QueryItemSummaries(SearchBox.Text);
            _historySummariesPreloaded = string.IsNullOrWhiteSpace(SearchBox.Text);
            RenderItems("pin-toggle");
            ShellLog.Info($"pin toggled id={item.Id} pinned={next}");
        }
    }

    private void MovePin(ClipboardHistoryItem item, int direction)
    {
        if (!_store.MovePinned(item.Id, direction))
        {
            ShellLog.Info($"pin move ignored id={item.Id} direction={direction}");
            return;
        }

        _allItems = _store.QueryItemSummaries(SearchBox.Text);
        _historySummariesPreloaded = string.IsNullOrWhiteSpace(SearchBox.Text);
        RenderItems("pin-move");
        ShellLog.Info($"pin moved id={item.Id} direction={direction}");
    }

    private bool CanMovePin(ClipboardHistoryItem item, int direction)
    {
        var pins = _allItems.Where(i => i.IsPinned).OrderBy(i => i.PinOrder).ToList();
        var index = pins.FindIndex(i => i.Id == item.Id);
        var target = index + Math.Sign(direction);
        return index >= 0 && target >= 0 && target < pins.Count;
    }

    /// <summary>
    /// Reads the live pause flag from disk and keeps the in-memory settings in step, so a later
    /// Save from the settings window cannot write a stale value back and silently flip capture.
    /// </summary>
    private bool RefreshCapturePaused()
    {
        var paused = ClipSharedSettings.Load().CapturePaused;
        _settings.CapturePaused = paused;
        return paused;
    }

    /// <summary>
    /// The pause toggle lives in the watcher's tray menu, in another process; showing its state
    /// here is what keeps a forgotten pause from silently eating every copy.
    /// </summary>
    private void RefreshCapturePausedBadge()
    {
        var paused = RefreshCapturePaused();
        CapturePausedBadge.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        // Paste & stay used to give way to this badge because six hints plus the buttons overflowed
        // the bar. The footer is down to two hints now, so both fit and neither has to hide.
        UpdateFooterHotkeyHints();
    }

    private void CopySelected()
    {
        if (_selected is null) return;
        var selected = ClipboardItemForPasteFormat(_selected);
        SetClipboard(selected, _settings.DefaultPasteFormat);
        // Unlike paste, a copy leaves the palette up with nothing visibly changed, so it needs
        // its own confirmation.
        ShowToast("Copied");
        ShellLog.Info($"copy selected id={_selected.Id}");
    }

    private void PasteSelected() => PasteSelected(null);

    /// <summary>
    /// Pastes the selection. <paramref name="formatOverride"/> forces a format for this one
    /// paste without touching the saved default, which is what "Paste as Plain Text" uses.
    ///
    /// <paramref name="keepPaletteOpen"/> is the Shift variant: filling three fields in a form
    /// otherwise means pressing the open hotkey three times. The palette still has to get out of
    /// the way for the paste itself — the synthetic Ctrl+V lands in whatever window holds focus,
    /// and while the palette is up that is the palette — so this is a conceal, the ordinary paste,
    /// and a reopen, rather than a second paste path. Everything about hitting the target (the
    /// integrity check, the app override, the verify-and-retry) is the same code either way.
    /// </summary>
    private void PasteSelected(PasteFormatPreference? formatOverride, bool keepPaletteOpen = false)
    {
        if (_selected is null) return;

        // Work out the follow-on selection from the list as it stands now: the reopen below can
        // reload the rows (the paste puts the item back on the clipboard, which the watcher
        // captures), and the row after this one in *that* list is not the one the user aimed at.
        // Step clamps at the end, so the last row stays put rather than wrapping to the top —
        // wrapping would silently re-paste an item that was already used a moment ago.
        var nextSelection = keepPaletteOpen
            ? PaletteSelection.Step(VisibleOrder(LastRenderedVisibleItems()), _selected.Id, 1)
            : null;
        var pasteFormat = formatOverride ?? _settings.DefaultPasteFormat;
        var selected = ClipboardItemForPasteFormat(_selected, pasteFormat);
        var payload = selected.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color
            ? ClipboardPasteData.Create(selected, pasteFormat)
            : null;
        SetClipboard(selected, pasteFormat);

        // Check this before concealing. Windows drops injected input aimed at a higher-integrity
        // window (UIPI) silently - no error, no failed return, the keystroke simply never arrives.
        // The clipboard is set above and crosses integrity levels fine, so a manual Ctrl+V works.
        //
        // The toast lives inside the palette, so raising it after ConcealPalette drew it onto a
        // window at Opacity 0 - which is why this never appeared on screen despite the gate firing
        // correctly every time. The tray balloon behind UserNotificationRequested is no fallback
        // either; Windows 11 suppresses NotifyIcon balloons. Keep the palette up instead: the
        // paste did not happen, so there is nothing to get out of the way for, and Escape then
        // Ctrl+V finishes the job.
        if (TargetRejectsSyntheticInput(_returnFocusHwnd))
        {
            ShellLog.Info($"paste blocked by integrity id={selected.Id} target={TargetIntegrityLevel(_returnFocusHwnd)} own={OwnIntegrityLevel()}");

            // Nothing is concealed yet, deliberately: the toast is a child of the palette window,
            // so raising it after ConcealPalette draws it at Opacity 0 and nothing appears. The
            // clipboard is already set and crosses integrity levels fine, so leave the palette up
            // with the message and let the user finish it with Ctrl+V.
            NotifyPasteBlockedByElevation();
            return;
        }

        ConcealPalette(keepPaletteOpen ? "paste-stay" : "paste", resetView: !keepPaletteOpen);
        RestoreReturnFocus();

        var actionKey = ClipAppOverride.ActionPaste;
        var overrideHotkey = ResolveOverrideHotkey(_returnFocusHwnd, actionKey);
        string pasteKeys;
        bool suspendHotkeys;
        if (!string.IsNullOrWhiteSpace(overrideHotkey))
        {
            pasteKeys = SendKeysFromGesture(overrideHotkey!);
            suspendHotkeys = true;
        }
        else if (selected.Kind == ClipboardItemKind.Image && AutoAltVForClaudeCli(_returnFocusHwnd))
        {
            pasteKeys = "%v";
            suspendHotkeys = true;
        }
        else
        {
            pasteKeys = "^v";
            suspendHotkeys = false;
        }

        if (TryPasteDirectlyIntoExplorerSearch(selected, payload?.Text))
        {
            ShellLog.Info($"paste selected id={selected.Id} keys=uia-explorer-search action={actionKey} override={overrideHotkey ?? "none"}");
            ReopenAfterPasteAndStay(keepPaletteOpen, nextSelection);
            return;
        }

        SendPasteKeys(pasteKeys, suspendHotkeys);
        var verified = VerifyPasteOrRetry(selected, pasteKeys, suspendHotkeys, payload?.Text);
        if (verified)
        {
            CommitPasteIfNeeded(payload?.Text, suspendHotkeys);
        }

        ShellLog.Info($"paste selected id={selected.Id} keys={pasteKeys} action={actionKey} override={overrideHotkey ?? "none"} verified={verified}");
        ReopenAfterPasteAndStay(keepPaletteOpen, nextSelection);
    }

    /// <summary>
    /// Brings the palette back after a paste-and-stay and moves to the row that was next when the
    /// paste started, so the next Shift+Enter needs no arrow keys. The reopen is a normal
    /// <see cref="ShowPalette"/>, which recaptures the return focus — by now the foreground window
    /// is the app just pasted into, which is exactly the target for the next one.
    /// </summary>
    private void ReopenAfterPasteAndStay(bool keepPaletteOpen, ClipboardHistoryItem? nextSelection)
    {
        if (!keepPaletteOpen)
        {
            return;
        }

        ShowPalette();
        if (nextSelection is null)
        {
            return;
        }

        // "keyboard" and not "reconcile": this is the user's choice, so a reload triggered by the
        // clip that the paste itself produced must not quietly replace it with the top row.
        SelectItem(nextSelection, reason: "keyboard");
        ScrollRowIntoView(nextSelection.Id);
    }

    private ClipboardHistoryItem ClipboardItemForPasteFormat(ClipboardHistoryItem item) =>
        ClipboardItemForPasteFormat(item, _settings.DefaultPasteFormat);

    private ClipboardHistoryItem ClipboardItemForPasteFormat(ClipboardHistoryItem item, PasteFormatPreference pasteFormat)
    {
        if (NeedsFullText(item) ||
            (pasteFormat == PasteFormatPreference.OriginalFormatting &&
            ClipboardPasteData.HasOriginalFormatting(item)))
        {
            return _store.GetItem(item.Id) ?? item;
        }

        return item;
    }

    private ClipboardHistoryItem FullTextItem(ClipboardHistoryItem item)
    {
        return NeedsFullText(item) ? _store.GetItem(item.Id) ?? item : item;
    }

    private static bool NeedsFullText(ClipboardHistoryItem item)
    {
        if (item.Kind is not (ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color))
        {
            return false;
        }

        if (item.Text is null)
        {
            return true;
        }

        return item.CharacterCount is int characterCount && item.Text.Length < characterCount;
    }

    private void CaptureReturnFocus(IntPtr foreground)
    {
        var watch = Stopwatch.StartNew();
        _returnFocusHwnd = foreground;
        var windowTitle = WindowTitle(foreground);
        var windowClass = WindowClass(foreground);
        _returnFocusCouldNeedNoActivate = CouldNeedNoActivatePalette(foreground, windowTitle);
        var needsAutomation = IsFileExplorerWindowClass(windowClass) || _returnFocusCouldNeedNoActivate;
        var processName = needsAutomation ? TryGetProcessNameForWindow(foreground) : null;
        _returnFocusChildHwnd = ShouldSkipFocusedChildCapture(needsAutomation)
            ? IntPtr.Zero
            : FocusedChildWindow(foreground);
        _returnFocusElement = needsAutomation ? FocusedAutomationElement() : null;
        _returnFocusElementSummary = _returnFocusElement is null ? "none" : "captured";
        _returnFocusValueBefore = null;
        _returnFocusCommitsPasteWithEnter = _returnFocusCouldNeedNoActivate && ShouldCommitPasteWithEnter(_returnFocusHwnd, _returnFocusElement);
        ShellLog.Info($"return focus captured hwnd={_returnFocusHwnd} child={_returnFocusChildHwnd} process={processName ?? "unknown"} element={_returnFocusElementSummary} elapsedMs={watch.ElapsedMilliseconds}");
    }

    private static bool ShouldSkipFocusedChildCapture(bool needsAutomation)
    {
        return !needsAutomation;
    }

    private void RestoreReturnFocus()
    {
        if (_returnFocusHwnd == IntPtr.Zero || !IsWindow(_returnFocusHwnd))
        {
            ShellLog.Info("return focus skipped hwnd=0");
            return;
        }

        var foregroundSet = ForceActivateWindow(_returnFocusHwnd);
        var focusSet = false;
        if (_returnFocusChildHwnd != IntPtr.Zero && IsWindow(_returnFocusChildHwnd))
        {
            var targetThread = GetWindowThreadProcessId(_returnFocusHwnd, out _);
            var currentThread = GetCurrentThreadId();
            var attached = targetThread != 0 && targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                focusSet = SetFocus(_returnFocusChildHwnd) != IntPtr.Zero;
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, targetThread, false);
                }
            }
        }

        var automationFocusSet = SetAutomationFocus(_returnFocusElement);
        ShellLog.Info($"return focus restored hwnd={_returnFocusHwnd} child={_returnFocusChildHwnd} foreground={foregroundSet} focus={focusSet} elementFocus={automationFocusSet} element={_returnFocusElementSummary}");
    }

    private bool TryPasteDirectlyIntoExplorerSearch(ClipboardHistoryItem item, string? text)
    {
        if (!IsFileExplorerSearchTarget(_returnFocusHwnd, _returnFocusElement))
        {
            return false;
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            if (_returnFocusElement!.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(text);
                ShellLog.Info($"explorer search set through UIA chars={text.Length} element={_returnFocusElementSummary}");
                return true;
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"explorer search UIA paste failed element={_returnFocusElementSummary}");
        }

        return false;
    }

    private void CommitPasteIfNeeded(string? expectedText, bool suspendHotkeys)
    {
        if (!_returnFocusCommitsPasteWithEnter || string.IsNullOrEmpty(expectedText))
        {
            return;
        }

        Thread.Sleep(80);
        RestoreReturnFocus();
        if (suspendHotkeys)
        {
            SuspendOwnHotkeysForSyntheticPaste(SendEnter);
        }
        else
        {
            SendEnter();
        }

        ShellLog.Info($"paste committed with Enter element={_returnFocusElementSummary}");
    }

    private bool VerifyPasteOrRetry(ClipboardHistoryItem item, string pasteKeys, bool suspendHotkeys, string? expectedText)
    {
        if (!CanVerifyPasteTarget(_returnFocusElement, expectedText))
        {
            // Nothing readable to check against, so this is "not disproven", not "worked". Saying
            // verified=True here made every blind paste look confirmed in the log.
            ShellLog.Info($"paste verify unavailable id={item.Id} element={_returnFocusElementSummary}");
            return true;
        }

        Thread.Sleep(180);
        var afterFirst = AutomationValue(_returnFocusElement);
        if (_returnFocusCommitsPasteWithEnter && PasteLooksApplied(_returnFocusValueBefore, afterFirst, expectedText))
        {
            ShellLog.Info($"paste verify succeeded id={item.Id} attempt=1-commit before={SafeLogValue(_returnFocusValueBefore)} after={SafeLogValue(afterFirst)}");
            return true;
        }

        if (PasteLooksApplied(_returnFocusValueBefore, afterFirst, expectedText) && PasteStillAppliedAfterSettle(afterFirst, expectedText))
        {
            ShellLog.Info($"paste verify succeeded id={item.Id} attempt=1 before={SafeLogValue(_returnFocusValueBefore)} after={SafeLogValue(afterFirst)}");
            return true;
        }

        ShellLog.Info($"paste verify retrying id={item.Id} before={SafeLogValue(_returnFocusValueBefore)} after={SafeLogValue(afterFirst)} element={_returnFocusElementSummary}");
        RestoreReturnFocus();
        SendPasteKeys(pasteKeys, suspendHotkeys);
        Thread.Sleep(240);

        var afterRetry = AutomationValue(_returnFocusElement);
        if (_returnFocusCommitsPasteWithEnter &&
            (PasteLooksApplied(afterFirst, afterRetry, expectedText) || PasteLooksApplied(_returnFocusValueBefore, afterRetry, expectedText)))
        {
            ShellLog.Info($"paste verify succeeded id={item.Id} attempt=2-commit after={SafeLogValue(afterRetry)}");
            return true;
        }

        if ((PasteLooksApplied(afterFirst, afterRetry, expectedText) || PasteLooksApplied(_returnFocusValueBefore, afterRetry, expectedText)) &&
            PasteStillAppliedAfterSettle(afterRetry, expectedText))
        {
            ShellLog.Info($"paste verify succeeded id={item.Id} attempt=2 after={SafeLogValue(afterRetry)}");
            return true;
        }

        if (TrySetAutomationValue(expectedText))
        {
            Thread.Sleep(240);
            var afterDirectSet = AutomationValue(_returnFocusElement);
            if (PasteLooksApplied(_returnFocusValueBefore, afterDirectSet, expectedText) &&
                PasteStillAppliedAfterSettle(afterDirectSet, expectedText))
            {
                ShellLog.Info($"paste verify succeeded id={item.Id} attempt=uia-set after={SafeLogValue(afterDirectSet)}");
                return true;
            }
        }

        NotifyPasteFailed();
        ShellLog.Info($"paste verify failed id={item.Id} expected={SafeLogValue(expectedText)} before={SafeLogValue(_returnFocusValueBefore)} after={SafeLogValue(afterRetry)} element={_returnFocusElementSummary}");
        return false;
    }

    private bool PasteStillAppliedAfterSettle(string? firstAppliedValue, string? expectedText)
    {
        Thread.Sleep(520);
        var afterSettle = AutomationValue(_returnFocusElement);
        var stable = PasteLooksApplied(firstAppliedValue, afterSettle, expectedText);
        if (!stable)
        {
            ShellLog.Info($"paste verify unstable first={SafeLogValue(firstAppliedValue)} afterSettle={SafeLogValue(afterSettle)} element={_returnFocusElementSummary}");
        }

        return stable;
    }

    private bool TrySetAutomationValue(string? text)
    {
        if (_returnFocusElement is null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            if (_returnFocusElement.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(text);
                ShellLog.Info($"paste fallback set through UIA chars={text.Length} element={_returnFocusElementSummary}");
                return true;
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"paste fallback UIA set failed element={_returnFocusElementSummary}");
        }

        return false;
    }

    /// <summary>
    /// Puts <paramref name="hwnd"/> in the foreground and confirms it actually got there.
    ///
    /// SetForegroundWindow returns true in cases where the foreground did not change - Windows
    /// applies a foreground lock, and a process that did not receive the last input often loses.
    /// The old code trusted that return value and typed Ctrl+V immediately, so the keystroke could
    /// land in whatever still held focus, or nowhere. Raycast's helper does not trust it either:
    /// its log strings spell out the same ladder (already-foreground, SetForegroundWindow,
    /// AttachThreadInput) with a GetForegroundWindow check between the rungs.
    /// </summary>
    private static bool ForceActivateWindow(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd)
        {
            return true;
        }

        SetForegroundWindow(hwnd);
        if (WaitForForeground(hwnd))
        {
            return true;
        }

        // Borrowing the target's input queue makes us "the foreground thread" for the length of
        // the attach, which is what gets past the foreground lock.
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var currentThread = GetCurrentThreadId();
        var attached = targetThread != 0 && targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }

        if (WaitForForeground(hwnd))
        {
            return true;
        }

        var actual = GetForegroundWindow();
        ShellLog.Info($"force activate failed target={hwnd} '{SafeLogValue(WindowTitle(hwnd))}' actual={actual} '{SafeLogValue(WindowTitle(actual))}'");
        return false;
    }

    private static bool WaitForForeground(IntPtr hwnd)
    {
        // Activation is asynchronous: SetForegroundWindow posts, it does not switch. Poll briefly
        // rather than sleeping a flat guess, so a fast app costs nothing and a slow one still wins.
        for (var i = 0; i < 10; i++)
        {
            if (GetForegroundWindow() == hwnd)
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    /// <summary>
    /// Releases any modifier the system still believes is held before a synthetic chord goes out.
    ///
    /// The palette opens on Alt+V. If Alt is still down when Ctrl+V is injected the target sees
    /// Ctrl+Alt+V, which most apps ignore - a silent no-paste that looks exactly like "it pasted
    /// into the wrong place". Raycast reads GetAsyncKeyState for the same reason (its binary even
    /// carries a "Reset stuck Caps Lock toggle" path).
    /// </summary>
    private static void ReleaseStuckModifiers()
    {
        ushort[] modifiers = { VirtualKeyMenu, VirtualKeyShift, VirtualKeyLeftWindows, VirtualKeyRightWindows };
        var stuck = modifiers.Where(k => (GetAsyncKeyState(k) & 0x8000) != 0).ToArray();
        if (stuck.Length == 0)
        {
            return;
        }

        var inputs = stuck.Select(k => KeyboardInput(k, true)).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        ShellLog.Info($"released stuck modifiers {string.Join(",", stuck.Select(k => k.ToString("X2")))}");
    }

    private void SendPasteKeys(string pasteKeys, bool suspendHotkeys)
    {
        if (pasteKeys == "^v")
        {
            if (suspendHotkeys)
            {
                SuspendOwnHotkeysForSyntheticPaste(SendCtrlV);
            }
            else
            {
                SendCtrlV();
            }

            return;
        }

        if (suspendHotkeys)
        {
            SuspendOwnHotkeysForSyntheticPaste(() => Forms.SendKeys.SendWait(pasteKeys));
        }
        else
        {
            Forms.SendKeys.SendWait(pasteKeys);
        }
    }

    private static void SendCtrlV()
    {
        ReleaseStuckModifiers();
        var inputs = new[]
        {
            KeyboardInput(VirtualKeyControl, false),
            KeyboardInput(VirtualKeyV, false),
            KeyboardInput(VirtualKeyV, true),
            KeyboardInput(VirtualKeyControl, true),
        };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            Forms.SendKeys.SendWait("^v");
        }
    }

    private static void SendEnter()
    {
        var inputs = new[]
        {
            KeyboardInput(VirtualKeyEnter, false),
            KeyboardInput(VirtualKeyEnter, true),
        };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            Forms.SendKeys.SendWait("{ENTER}");
        }
    }

    private static Input KeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = virtualKey,
                    // Carry the hardware scan code, not just the virtual key. Windows leaves Scan
                    // at 0 for anything that only fills the VK, but apps that consume raw input -
                    // 3D and canvas apps, and some browser-hosted editors - read the scan code and
                    // treat a zero as no key at all. Raycast maps the same way (MapVirtualKeyExW).
                    Scan = (ushort)MapVirtualKey(virtualKey, MapVirtualKeyToScanCode),
                    Flags = keyUp ? KeyEventKeyUp : 0,
                },
            },
        };
    }

    /// <summary>
    /// True when the paste target runs at a higher Windows integrity level than Clip — an app
    /// started as administrator, typically. UIPI drops synthetic keystrokes crossing that boundary
    /// without reporting anything, so SendInput returns success and nothing is pasted.
    ///
    /// Raycast solves this by shipping a helper manifested with uiAccess="true", which is exempt
    /// from UIPI. That needs an Authenticode-signed binary in a secure path (Program Files), and
    /// Clip installs under %APPDATA%, so it is a packaging decision rather than a code one. Until
    /// the log shows this actually happening, saying so plainly is the honest behaviour.
    /// </summary>
    private static bool TargetRejectsSyntheticInput(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var target = TargetIntegrityLevel(hwnd);
        var own = OwnIntegrityLevel();
        return target > 0 && own > 0 && target > own;
    }

    private static uint OwnIntegrityLevel() => IntegrityLevelOfProcess(GetCurrentProcess());

    private static uint TargetIntegrityLevel(IntPtr hwnd)
    {
        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return 0;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return IntegrityLevelOfProcess(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Reads the token's integrity level RID. 0 means "could not tell", which every caller treats
    /// as "assume it is fine" — a check that cannot answer must not start blocking pastes.
    /// </summary>
    internal static uint IntegrityLevelOfProcess(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token) || token == IntPtr.Zero)
        {
            return 0;
        }

        var buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size == 0)
            {
                return 0;
            }

            buffer = Marshal.AllocHGlobal((int)size);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
            {
                return 0;
            }

            // TOKEN_MANDATORY_LABEL is a SID_AND_ATTRIBUTES; the level lives in the SID's last
            // subauthority (0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system).
            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return count == 0 ? 0 : (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "integrity level read failed");
            return 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            CloseHandle(token);
        }
    }

    private void NotifyPasteFailed()
    {
        const string message = "Clip could not paste here. Press Ctrl+V manually.";
        ShowToast(message);
        UserNotificationRequested?.Invoke(message);
    }

    /// <summary>
    /// The elevated-target message. Both words are deliberate: "elevated" is what the user will
    /// read everywhere else, "administrator" is what they typed to get there. Nothing was blocked
    /// on the way in - the clipboard crosses integrity levels fine, and the text is already on it
    /// by the time this runs. The only thing UIPI stopped is Clip pressing the key, which is
    /// exactly why the user pressing it themselves works.
    /// </summary>
    private void NotifyPasteBlockedByElevation()
    {
        const string message = "Clip cannot paste into elevated (administrator) run apps \u2014 press Ctrl+V";
        ShowToast(message, TimeSpan.FromSeconds(4));
        UserNotificationRequested?.Invoke(message);
    }

    private string? ResolveOverrideHotkey(IntPtr hwnd, string actionKey)
    {
        if (hwnd == IntPtr.Zero) return null;
        var processName = TryGetProcessNameForWindow(hwnd);
        if (string.IsNullOrEmpty(processName)) return null;
        var match = _settings.AppOverrides.FirstOrDefault(o =>
            string.Equals(StripExeSuffix(o.AppName), processName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(o.Action, actionKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match?.Hotkey) ? null : match!.Hotkey;
    }

    private static string StripExeSuffix(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed;
    }

    private bool AutoAltVForClaudeCli(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var processName = TryGetProcessNameForWindow(hwnd);
        if (string.IsNullOrEmpty(processName)) return false;
        if (!_terminalHostProcesses.Any(t => string.Equals(t, processName, StringComparison.OrdinalIgnoreCase))) return false;
        return IsClaudeCliRunning();
    }

    private static string SendKeysFromGesture(string display)
    {
        var parts = display.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "^v";
        var prefix = new System.Text.StringBuilder();
        string keyToken = "v";
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase)) prefix.Append('^');
            else if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase)) prefix.Append('%');
            else if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase)) prefix.Append('+');
            else if (string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase)) { /* SendKeys can't emit Win cleanly; skip */ }
            else keyToken = MapKeyToken(token);
        }
        return prefix.Append(keyToken).ToString();
    }

    private static string MapKeyToken(string token)
    {
        if (token.Length == 1) return token.ToLowerInvariant();
        return token.ToUpperInvariant() switch
        {
            "ENTER" => "{ENTER}",
            "ESC" or "ESCAPE" => "{ESC}",
            "SPACE" => " ",
            "TAB" => "{TAB}",
            "BACKSPACE" => "{BACKSPACE}",
            "DELETE" or "DEL" => "{DEL}",
            "INSERT" or "INS" => "{INS}",
            "HOME" => "{HOME}",
            "END" => "{END}",
            "PAGEUP" or "PGUP" => "{PGUP}",
            "PAGEDOWN" or "PGDN" => "{PGDN}",
            "UP" => "{UP}",
            "DOWN" => "{DOWN}",
            "LEFT" => "{LEFT}",
            "RIGHT" => "{RIGHT}",
            var s when s.StartsWith("F") && int.TryParse(s.AsSpan(1), out _) => "{" + s + "}",
            _ => token.ToLowerInvariant(),
        };
    }

    private void SuspendOwnHotkeysForSyntheticPaste(Action send)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var hadOpen = _openHotkeyRegistered;
        var hadDebug = _debugLogHotkeyRegistered;
        var hadOverride = _openOverrideRegistered;
        if (hwnd != IntPtr.Zero)
        {
            if (hadOpen)
            {
                UnregisterHotKey(hwnd, OpenHotkeyId);
                _openHotkeyRegistered = false;
            }
            if (hadDebug)
            {
                UnregisterHotKey(hwnd, DebugLogHotkeyId);
                _debugLogHotkeyRegistered = false;
            }
            if (hadOverride)
            {
                UnregisterHotKey(hwnd, OpenOverrideHotkeyId);
                _openOverrideRegistered = false;
            }
        }

        try
        {
            send();
        }
        finally
        {
            if (hwnd != IntPtr.Zero && (hadOpen || hadDebug || hadOverride))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    EnsureHotkeyRegistered("post-paste");
                    ApplyForegroundOverride(GetForegroundWindow());
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    private static readonly string[] _terminalHostProcesses =
    {
        "Code",
        "Code - Insiders",
        "WindowsTerminal",
        "OpenConsole",
        "conhost",
        "powershell",
        "pwsh",
        "cmd",
        "wezterm-gui",
        "alacritty",
        "mintty",
        "cursor",
    };

    private static string? TryGetProcessNameForWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsClaudeCliRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.ProcessName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private void SetClipboard(ClipboardHistoryItem item, PasteFormatPreference pasteFormat)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            var payload = ClipboardPasteData.Create(item, pasteFormat);

            // Not WPF's Clipboard here: SetDataObject(copy: true) re-renders every format
            // inside OleFlushClipboard and answers any failure there with Environment.FailFast,
            // which killed the app on 2026-08-08. Win32ClipboardWriter hands Windows finished
            // buffers instead, so there is nothing left to re-render or fail-fast over.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (Win32ClipboardWriter.TrySetText(hwnd, payload.Text, payload.Html, payload.Rtf))
            {
                return;
            }

            ShellLog.Snapshot("clipboard win32 set failed; falling back to WPF without flush");
            var data = new System.Windows.DataObject();
            data.SetText(payload.Text, System.Windows.TextDataFormat.UnicodeText);
            if (payload.Html is not null)
            {
                data.SetText(payload.Html, System.Windows.TextDataFormat.Html);
            }

            if (payload.Rtf is not null)
            {
                data.SetText(payload.Rtf, System.Windows.TextDataFormat.Rtf);
            }

            // copy: false skips the flush, so the FailFast path cannot run; the data stays
            // valid because Clip is resident.
            System.Windows.Clipboard.SetDataObject(data, copy: false);
        }
        else if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
        {
            // Same reason as the text path above: WPF's Clipboard.SetImage renders the bitmap
            // inside DataObject and answers a failure with Environment.FailFast, which killed
            // Clip on a large photo (2026-08-18). Win32ClipboardWriter hands Windows finished
            // DIB buffers, so an image we cannot fit comes back as false and gets a toast.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!TrySetClipboardImage(hwnd, item.AssetPath))
            {
                ShellLog.Snapshot($"clipboard image set failed path={item.AssetPath}");
                ShowToast("That image is too big for the clipboard");
            }
        }
        else if (item.Kind == ClipboardItemKind.Files)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!Win32ClipboardWriter.TrySetFileDrop(hwnd, item.FilePaths))
            {
                ShellLog.Snapshot("clipboard file-drop set failed");
                ShowToast("Could not copy those files");
            }
        }
    }

    /// <summary>
    /// Decodes the asset to Bgra32 and hands the pixels to <see cref="Win32ClipboardWriter"/>.
    /// Everything that can go wrong with a huge photo — decode OOM, an oversize pixel buffer,
    /// a failed GlobalAlloc — comes back as false so the caller can say so and stay alive.
    /// </summary>
    private static bool TrySetClipboardImage(IntPtr hwnd, string path)
    {
        try
        {
            var bitmap = LoadBitmap(path);
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight * 4 > Win32ClipboardWriter.MaxImageBytes)
            {
                return false;
            }

            var source = bitmap.Format == PixelFormats.Bgra32
                ? (BitmapSource)bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = source.PixelWidth * 4;
            var pixels = new byte[(long)stride * source.PixelHeight];
            source.CopyPixels(pixels, stride, 0);
            return Win32ClipboardWriter.TrySetImage(hwnd, source.PixelWidth, source.PixelHeight, pixels);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or OverflowException or NotSupportedException or IOException or ArgumentException)
        {
            return false;
        }
    }

    private void EditText(ClipboardHistoryItem item) => EditText(item, isNewSnippet: false);

    /// <summary>
    /// A new snippet reuses this whole editor rather than growing an authoring UI of its own.
    /// <paramref name="isNewSnippet"/> rides along instead of a field so the flag cannot outlive
    /// the editor it belongs to and turn a later ordinary edit into an accidental insert.
    /// </summary>
    private void EditText(ClipboardHistoryItem item, bool isNewSnippet)
    {
        // A snippet is not in the store yet, so there is no fuller copy of it to hydrate.
        item = isNewSnippet ? item : FullTextItem(item);
        var watch = Stopwatch.StartNew();
        if (TryShowTextEditOverlay(item, watch, isNewSnippet))
        {
            return;
        }

        var editor = new TextEditWindow(TextPayload(item), (WpfBrush)FindResource("Bg"), (WpfBrush)FindResource("Text"), (WpfBrush)FindResource("Line"), (WpfBrush)FindResource("Surface"), (WpfBrush)FindResource("TextCursor"), (WpfBrush)FindResource("AccentSoft"), (WpfBrush)FindResource("Selected"), (WpfBrush)FindResource("SelectedBorder"), (WpfBrush)FindResource("TextSelection"))
        {
            Owner = this,
        };
        editor.ContentRendered += (_, _) => ShellLog.Info($"edit-text rendered elapsedMs={watch.ElapsedMilliseconds}");
        _suppressDeactivate = true;
        try
        {
            if (editor.ShowDialog() == true)
            {
                SaveTextEdit(item, editor.Value, isNewSnippet);
            }
        }
        finally
        {
            _suppressDeactivate = false;
            ShowPalette();
        }
    }

    private bool TryShowTextEditOverlay(ClipboardHistoryItem item, Stopwatch watch, bool isNewSnippet)
    {
        if (Shell.Child is not Grid root)
        {
            return false;
        }

        CloseInlineModal(showPalette: false);
        var background = (WpfBrush)FindResource("Bg");
        var foreground = (WpfBrush)FindResource("Text");
        var line = (WpfBrush)FindResource("Line");
        var surface = (WpfBrush)FindResource("Surface");
        var textCursor = (WpfBrush)FindResource("TextCursor");
        var accentSoft = (WpfBrush)FindResource("AccentSoft");
        var selected = (WpfBrush)FindResource("Selected");
        var selectedBorder = (WpfBrush)FindResource("SelectedBorder");

        var overlay = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(76, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
        };

        var panel = new Border
        {
            Width = 640,
            Height = 420,
            Background = background,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        var box = new WpfTextBox
        {
            Text = TextPayload(item),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FocusVisualStyle = null,
            SnapsToDevicePixels = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0),
            Padding = new Thickness(14),
            Background = WpfBrushes.Transparent,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            CaretBrush = textCursor,
            SelectionBrush = (WpfBrush)FindResource("TextSelection"),
        };
        // Display mode, matching the window: Ideal places glyphs at fractional pixels, which is
        // the classic "WPF text looks soft" cause at these 11-13px sizes.
        TextOptions.SetTextFormattingMode(box, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(box, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(box, TextHintingMode.Auto);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseInlineModal();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                CommitTextEditOverlay(item, box.Text, isNewSnippet);
                e.Handled = true;
            }
        };

        // No background of its own: the panel border already paints Bg, and a second Bg layer on
        // top of it doubles the glass tint into a square-cornered block inside the rounded card.
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(18, 14, 18, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = isNewSnippet ? "New Snippet" : "Edit Text",
            Foreground = foreground,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var trim = InlineModalButton("Trim", foreground, line, surface, accentSoft, selectedBorder, selected, primary: false);
        trim.Margin = new Thickness(0, 0, 8, 0);
        trim.Click += (_, _) => { box.Text = box.Text.Trim(); };
        Grid.SetColumn(trim, 1);
        header.Children.Add(trim);
        grid.Children.Add(header);

        var editorShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(18, 0, 18, 0),
            Child = box,
        };
        Grid.SetRow(editorShell, 1);
        grid.Children.Add(editorShell);

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(18, 14, 18, 18),
        };
        var cancel = InlineModalButton("Cancel", foreground, line, surface, accentSoft, selectedBorder, selected, primary: false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => CloseInlineModal();
        var save = InlineModalButton("Save", foreground, line, surface, accentSoft, selectedBorder, selected, primary: true);
        save.Click += (_, _) => CommitTextEditOverlay(item, box.Text, isNewSnippet);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        panel.Child = grid;
        overlay.Child = panel;
        // RootGrid children default to row 0 — the 53px search strip — and ClipToBounds
        // would slice the modal to a sliver. Span every row like the settings overlay does.
        Grid.SetRowSpan(overlay, 3);
        System.Windows.Controls.Panel.SetZIndex(overlay, 900);
        root.Children.Add(overlay);
        _inlineModalOverlay = overlay;
        _suppressDeactivate = true;
        root.UpdateLayout();
        ShellLog.Info($"edit-text rendered elapsedMs={watch.ElapsedMilliseconds} hosted=True");

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }), System.Windows.Threading.DispatcherPriority.Background);

        return true;
    }

    private void CommitTextEditOverlay(ClipboardHistoryItem item, string value, bool isNewSnippet)
    {
        CloseInlineModal(showPalette: false);
        SaveTextEdit(item, value, isNewSnippet);
        ShowPalette();
    }

    /// <summary>
    /// Writes an edited value back to history.
    ///
    /// A new snippet has no row until this point: creating it only on save is what keeps a
    /// cancelled or empty editor from leaving a blank item behind, and it means the abandon path
    /// needs no cleanup at all — there is nothing to clean up.
    /// </summary>
    private void SaveTextEdit(ClipboardHistoryItem item, string value, bool isNewSnippet)
    {
        if (isNewSnippet)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ShellLog.Info("new snippet discarded empty");
                return;
            }

            item.Text = value;
            item.Preview = ClipboardHistoryStore.PreviewText(value);
            var stored = _store.AddOrUpdate(item, EffectiveHistoryLimit());

            // AddOrUpdate keeps IsPinned but not the order, and typing text that already exists in
            // history returns that item instead — either way this is what actually pins the row.
            _store.SetPinned(stored.Id, true);
            LoadItems(selectFirst: false, reason: "new-snippet");
            SelectItem(_store.GetItem(stored.Id), "new-snippet");
            ShowToast("Snippet pinned");
            ShellLog.Info($"new snippet id={stored.Id} length={value.Length}");
            return;
        }

        _store.EditText(item.Id, value);
        item.Text = value;
        item.Preview = ClipboardHistoryStore.PreviewText(value);
        LoadItems(selectFirst: false, reason: "edit-text");
        SelectItem(_store.GetItem(item.Id), "edit-text");
    }

    /// <summary>
    /// Authors a pinned item from scratch. Pin plus Rename plus Edit Text already behave like a
    /// snippet store; the only thing missing was a way to start one that was not a copy.
    /// </summary>
    private void NewSnippet()
    {
        CloseActionMenus();
        var draft = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = string.Empty,
            IsPinned = true,
            SourceApplication = "Clip",
        };
        ShellLog.Info("new snippet started");
        EditText(draft, isNewSnippet: true);
    }

    private void RenameItem(ClipboardHistoryItem item)
    {
        var watch = Stopwatch.StartNew();
        if (TryShowRenameOverlay(item, watch))
        {
            return;
        }

        var editor = new RenameWindow(TitleFor(item), (WpfBrush)FindResource("Bg"), (WpfBrush)FindResource("Text"), (WpfBrush)FindResource("Muted"), (WpfBrush)FindResource("Line"), (WpfBrush)FindResource("Surface"), (WpfBrush)FindResource("AccentSoft"), (WpfBrush)FindResource("Selected"), (WpfBrush)FindResource("SelectedBorder"), (WpfBrush)FindResource("TextSelection"))
        {
            Owner = this,
        };
        editor.ContentRendered += (_, _) => ShellLog.Info($"rename rendered elapsedMs={watch.ElapsedMilliseconds}");
        _suppressDeactivate = true;
        try
        {
            if (editor.ShowDialog() == true)
            {
                _store.Rename(item.Id, editor.Value);
                var updated = _store.GetItem(item.Id);
                _selected = null;
                LoadItems(selectFirst: false, reason: "rename");
                SelectItem(updated, "rename");
                ShellLog.Info($"rename item id={item.Id}");
            }
        }
        finally
        {
            _suppressDeactivate = false;
            ShowPalette();
        }
    }

    private bool TryShowRenameOverlay(ClipboardHistoryItem item, Stopwatch watch)
    {
        if (Shell.Child is not Grid root)
        {
            return false;
        }

        CloseInlineModal(showPalette: false);
        var background = (WpfBrush)FindResource("Bg");
        var foreground = (WpfBrush)FindResource("Text");
        var muted = (WpfBrush)FindResource("Muted");
        var line = (WpfBrush)FindResource("Line");
        var surface = (WpfBrush)FindResource("Surface");
        var accentSoft = (WpfBrush)FindResource("AccentSoft");
        var selected = (WpfBrush)FindResource("Selected");
        var selectedBorder = (WpfBrush)FindResource("SelectedBorder");

        var overlay = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(76, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
        };
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay))
            {
                CloseInlineModal();
                e.Handled = true;
            }
        };

        var panel = new Border
        {
            Width = 420,
            Background = background,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        var box = new WpfTextBox
        {
            Text = TitleFor(item),
            FocusVisualStyle = null,
            Margin = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            Background = WpfBrushes.Transparent,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            SelectionBrush = (WpfBrush)FindResource("TextSelection"),
            MaxLength = 120,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitRenameOverlay(item, box.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseInlineModal();
                e.Handled = true;
            }
        };

        // Same rule as the palette header: the panel border already paints Bg, so painting it again
        // inside the margin stacks a second glass layer and shows up as an inset block.
        var grid = new Grid { Margin = new Thickness(18, 16, 18, 18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = "Rename",
            Foreground = foreground,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        var hint = new TextBlock
        {
            Text = "Leave blank to use the original title.",
            Foreground = muted,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        var boxShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = box,
        };
        Grid.SetRow(boxShell, 2);
        grid.Children.Add(boxShell);

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = InlineModalButton("Cancel", foreground, line, surface, accentSoft, selectedBorder, selected, primary: false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => CloseInlineModal();
        var save = InlineModalButton("Save", foreground, line, surface, accentSoft, selectedBorder, selected, primary: true);
        save.Click += (_, _) => CommitRenameOverlay(item, box.Text);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        panel.Child = grid;
        overlay.Child = panel;
        Grid.SetRowSpan(overlay, 3);
        System.Windows.Controls.Panel.SetZIndex(overlay, 900);
        root.Children.Add(overlay);
        _inlineModalOverlay = overlay;
        _suppressDeactivate = true;
        root.UpdateLayout();
        ShellLog.Info($"rename rendered elapsedMs={watch.ElapsedMilliseconds} hosted=True");

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);

        return true;
    }

    private void CommitRenameOverlay(ClipboardHistoryItem item, string value)
    {
        CloseInlineModal(showPalette: false);
        _store.Rename(item.Id, value);
        var updated = _store.GetItem(item.Id);
        _selected = null;
        LoadItems(selectFirst: false, reason: "rename");
        SelectItem(updated, "rename");
        ShellLog.Info($"rename item id={item.Id}");
        ShowPalette();
    }

    private void CloseInlineModal(bool showPalette = true)
    {
        if (_inlineModalOverlay?.Parent is System.Windows.Controls.Panel parent)
        {
            parent.Children.Remove(_inlineModalOverlay);
        }

        _inlineModalOverlay = null;
        _suppressDeactivate = false;
        if (showPalette)
        {
            ShowPalette();
            SearchBox.Focus();
        }
    }

    private static WpfButton InlineModalButton(string text, WpfBrush foreground, WpfBrush line, WpfBrush secondaryBackground, WpfBrush primaryBackground, WpfBrush primaryBorder, WpfBrush hoverBackground, bool primary)
    {
        var idleBackground = primary ? primaryBackground : secondaryBackground;
        var idleBorder = primary ? primaryBorder : line;
        var hoverBg = primary ? hoverBackground : primaryBackground;
        var button = new WpfButton
        {
            Content = text,
            Height = 32,
            MinWidth = primary ? 74 : 68,
            Padding = new Thickness(14, 0, 14, 0),
            Background = idleBackground,
            BorderBrush = idleBorder,
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
            Template = ClipControlTemplates.CenterButton,
        };
        button.MouseEnter += (_, _) => { button.Background = hoverBg; button.BorderBrush = primaryBorder; };
        button.MouseLeave += (_, _) => { button.Background = idleBackground; button.BorderBrush = idleBorder; };
        return button;
    }

    private void AppendText(ClipboardHistoryItem item)
    {
        item = FullTextItem(item);
        var existing = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
        var payload = TextPayload(item);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            System.Windows.Clipboard.SetText(existing + payload);
        }
    }

    private void OpenItem(ClipboardHistoryItem item)
    {
        item = FullTextItem(item);
        try
        {
            if (item.Kind == ClipboardItemKind.Link)
            {
                var target = ClipboardLinkDetector.TryNormalize(TextPayload(item), out var normalized) ? normalized : TextPayload(item);
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else
            {
                var path = item.Kind == ClipboardItemKind.Image ? item.AssetPath : item.FilePaths.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }

            ShellLog.Info($"open item id={item.Id}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open item failed id={item.Id}");
        }
    }

    private void OpenWith(ClipboardHistoryItem item)
    {
        var targetPath = item.Kind == ClipboardItemKind.Image ? item.AssetPath : item.FilePaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(targetPath) || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
        {
            ShellLog.Info($"open-with skipped missing target id={item.Id}");
            return;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            ShellLog.Info($"open-with opening path={targetPath}");
            if (TryShowOpenWithOverlay(targetPath, watch))
            {
                return;
            }

            var picker = new OpenWithWindow(
                targetPath,
                (WpfBrush)FindResource("Bg"),
                (WpfBrush)FindResource("Surface"),
                (WpfBrush)FindResource("Surface2"),
                (WpfBrush)FindResource("Surface3"),
                (WpfBrush)FindResource("Text"),
                (WpfBrush)FindResource("Muted"),
                (WpfBrush)FindResource("Line"),
                (WpfBrush)FindResource("Selected"),
                (WpfBrush)FindResource("AccentSoft"),
                (WpfBrush)FindResource("SelectedBorder"))
            {
                Owner = this,
            };
            picker.ContentRendered += (_, _) => ShellLog.Info($"open-with rendered elapsedMs={watch.ElapsedMilliseconds}");

            _suppressDeactivate = true;
            picker.Closed += (_, _) =>
            {
                try
                {
                    if (picker.SelectedApp is not null)
                    {
                        ShellLog.Info($"open-with launching app={picker.SelectedApp.Name} source={picker.SelectedApp.Source} elapsedMs={watch.ElapsedMilliseconds}");
                        WatcherAppLauncher.OpenWith(targetPath, picker.SelectedApp);
                    }

                    ShellLog.Info($"open-with completed path={targetPath} elapsedMs={watch.ElapsedMilliseconds} selected={picker.SelectedApp?.Name ?? "none"}");
                }
                catch (Exception ex)
                {
                    ShellLog.Error(ex, $"open-with launch failed path={targetPath}");
                    ShowToast("Open With failed. Log saved.");
                }
                finally
                {
                    _suppressDeactivate = false;
                    ShowPalette();
                }
            };
            picker.Show();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open-with failed path={targetPath}");
            _suppressDeactivate = false;
            ShowToast("Open With failed. Log saved.");
        }
    }

    private bool TryShowOpenWithOverlay(string targetPath, Stopwatch watch)
    {
        if (Shell.Child is not Grid root)
        {
            return false;
        }

        CloseInlineModal(showPalette: false);
        var background = (WpfBrush)FindResource("Bg");
        var foreground = (WpfBrush)FindResource("Text");
        var muted = (WpfBrush)FindResource("Muted");
        var line = (WpfBrush)FindResource("Line");
        var surface = (WpfBrush)FindResource("Surface");
        var surface2 = (WpfBrush)FindResource("Surface2");
        var accentSoft = (WpfBrush)FindResource("AccentSoft");
        var selected = (WpfBrush)FindResource("Selected");
        var selectedBorder = (WpfBrush)FindResource("SelectedBorder");
        var apps = new List<WatcherAppChoice>();

        var overlay = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(76, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
        };

        var panel = new Border
        {
            Width = 620,
            Height = 520,
            Background = background,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });

        var header = new Grid { Background = surface2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = $"Open with {Path.GetFileName(targetPath)}",
            Foreground = foreground,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var close = InlineModalButton("Close", foreground, line, surface2, accentSoft, selectedBorder, selected, primary: false);
        close.Margin = new Thickness(0, 0, 12, 0);
        close.Click += (_, _) => CloseInlineModal();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        shell.Children.Add(header);

        var search = new WpfTextBox
        {
            Background = WpfBrushes.Transparent,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var searchShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(16, 8, 16, 8),
            Padding = new Thickness(10, 0, 10, 0),
            Child = search,
        };
        Grid.SetRow(searchShell, 1);
        shell.Children.Add(searchShell);

        // Bg again would be a third layer over the panel's Bg over the Shell's Bg; the panel is the
        // card, this is just where the list lives inside it.
        var appHost = new Border
        {
            Margin = new Thickness(12, 0, 8, 0),
            Child = OpenWithOverlayRow("Loading apps...", "Use Browse if the app is not listed.", foreground, muted),
        };
        Grid.SetRow(appHost, 2);
        shell.Children.Add(appHost);

        var footer = new Grid { Background = surface2 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var status = new TextBlock
        {
            Text = "Loading apps...",
            Foreground = muted,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        footer.Children.Add(status);
        var browse = InlineModalButton("Browse...", foreground, line, surface2, accentSoft, selectedBorder, selected, primary: false);
        browse.Margin = new Thickness(0, 0, 12, 0);
        browse.Click += (_, _) => BrowseOpenWithOverlay(targetPath);
        Grid.SetColumn(browse, 1);
        footer.Children.Add(browse);
        Grid.SetRow(footer, 3);
        shell.Children.Add(footer);

        panel.Child = shell;
        overlay.Child = panel;
        Grid.SetRowSpan(overlay, 3);
        System.Windows.Controls.Panel.SetZIndex(overlay, 900);
        root.Children.Add(overlay);
        _inlineModalOverlay = overlay;
        _suppressDeactivate = true;
        root.UpdateLayout();
        ShellLog.Info($"open-with rendered elapsedMs={watch.ElapsedMilliseconds} hosted=True");

        WpfListBox? appList = null;
        search.TextChanged += (_, _) => Render();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseInlineModal();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Accept();
                e.Handled = true;
            }
        };
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            search.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            appList = new WpfListBox
            {
                Background = WpfBrushes.Transparent,
                Foreground = foreground,
                BorderThickness = new Thickness(0),
                ItemContainerStyle = PaletteListItemStyle((WpfBrush)FindResource("AccentSoft"), (WpfBrush)FindResource("Selected")),
            };
            appList.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            appList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            appList.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Accept();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    CloseInlineModal();
                    e.Handled = true;
                }
            };
            appList.MouseDoubleClick += (_, _) => Accept();
            appHost.Child = appList;
            Render();
            _ = LoadOpenWithOverlayAppsAsync(targetPath, loaded =>
            {
                apps = loaded;
                status.Text = $"{apps.Count} apps";
                Render();
            });
        }), System.Windows.Threading.DispatcherPriority.Background);

        return true;

        void Render()
        {
            if (appList is null)
            {
                return;
            }

            var query = search.Text.Trim();
            var visibleApps = apps
                .Where(app => string.IsNullOrWhiteSpace(query) ||
                    app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (app.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                    (app.AppUserModelId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
                .OrderByDescending(app => app.IsDefault)
                .ThenByDescending(app => app.IsRecent)
                .ThenBy(app => app.Name)
                .Take(80)
                .ToList();

            appList.Items.Clear();
            if (apps.Count == 0)
            {
                appList.Items.Add(new WpfListBoxItem
                {
                    Content = OpenWithOverlayRow("Loading apps...", "Use Browse if the app is not listed.", foreground, muted),
                    Foreground = muted,
                    IsEnabled = false,
                });
                return;
            }

            foreach (var app in visibleApps)
            {
                appList.Items.Add(new WpfListBoxItem
                {
                    Tag = app,
                    Content = OpenWithOverlayRow(app.Name, app.IsDefault ? "Default app" : app.Source, foreground, muted),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(4, 1, 4, 1),
                    Background = WpfBrushes.Transparent,
                    Foreground = foreground,
                });
            }

            if (appList.Items.Count > 0)
            {
                appList.SelectedIndex = 0;
            }
        }

        void Accept()
        {
            if (appList?.SelectedItem is not WpfListBoxItem { Tag: WatcherAppChoice app })
            {
                return;
            }

            LaunchOpenWithOverlay(targetPath, app);
        }
    }

    private async Task LoadOpenWithOverlayAppsAsync(string targetPath, Action<List<WatcherAppChoice>> apply)
    {
        var loadWatch = Stopwatch.StartNew();
        try
        {
            ShellLog.Info($"open-with async load started path={targetPath}");
            var apps = await Task.Run(() => WatcherAppDiscovery.GetApps(targetPath).ToList());
            apply(apps);
            ShellLog.Info($"open-with async load completed count={apps.Count} elapsedMs={loadWatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open-with async load failed elapsedMs={loadWatch.ElapsedMilliseconds}");
        }
    }

    private static StackPanel OpenWithOverlayRow(string title, string subtitle, WpfBrush foreground, WpfBrush muted)
    {
        var panel = new StackPanel { Orientation = WpfOrientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, Foreground = foreground, FontSize = 13, FontWeight = FontWeights.Medium });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        return panel;
    }

    private void LaunchOpenWithOverlay(string targetPath, WatcherAppChoice app)
    {
        try
        {
            CloseInlineModal(showPalette: false);
            WatcherAppLauncher.OpenWith(targetPath, app);
            ShellLog.Info($"open-with completed path={targetPath} selected={app.Name} hosted=True");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open-with launch failed path={targetPath}");
            ShowToast("Open With failed. Log saved.");
        }
        finally
        {
            ShowPalette();
        }
    }

    private void BrowseOpenWithOverlay(string targetPath)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Applications|*.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            LaunchOpenWithOverlay(targetPath, new WatcherAppChoice(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, "Browse"));
        }
    }

    private void ShowInFileExplorer(ClipboardHistoryItem item)
    {
        var path = ClipboardItemRevealTarget.GetPath(item);
        try
        {
            if (!FileExplorerReveal.TryReveal(path))
            {
                ShowToast("Path not found");
                return;
            }

            ShellLog.Info($"show in file explorer id={item.Id} path={path}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"show in file explorer failed id={item.Id} path={path}");
            ShowToast("Could not open File Explorer");
        }
    }

    private void ShareItem(ClipboardHistoryItem item)
    {
        item = FullTextItem(item);
        ClipboardSharePayload? payload = null;
        try
        {
            if (!WindowsShareService.IsSupported())
            {
                ShowToast("Sharing is not available on this PC.");
                return;
            }

            payload = ClipboardSharePayload.Create(item);
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowsShareService.ShowShareUI(
                hwnd,
                item,
                payload,
                ShareTitle(item),
                ShareDescription(item),
                ex => ShellLog.Error(ex, $"share data failed id={item.Id}"));
            ShellLog.Info($"share opened id={item.Id} files={payload.FilePaths.Count} temp={payload.HasTemporaryFiles}");
        }
        catch (Exception ex)
        {
            payload?.Cleanup();
            ShellLog.Error(ex, $"share failed id={item.Id}");
            ShowToast("Share failed. Log saved.");
        }
    }

    private void ShareWithBlip(ClipboardHistoryItem item)
    {
        item = FullTextItem(item);
        if (BlipShareLaunchPlan.IsRunningWithBrokenHandoff())
        {
            // Launching would only produce Blip's own "SingleInstance failure" dialog, which says
            // nothing a user can act on. See BlipShareLaunchPlan.IsRunningWithBrokenHandoff.
            ShellLog.Info($"blip share blocked id={item.Id} socket={BlipShareLaunchPlan.HandoffSocketPath()} missing=True");
            ShowToast("Blip can't receive shares until it's restarted.");
            return;
        }

        try
        {
            var payload = ClipboardSharePayload.Create(item);
            var plan = BlipShareLaunchPlan.Create(payload);
            var startInfo = new ProcessStartInfo
            {
                FileName = BlipShareLaunchPlan.ExecutableName,
                UseShellExecute = true,
                Arguments = string.Join(" ", plan.LaunchArguments.Select(QuoteProcessArgument)),
            };

            Process.Start(startInfo);
            ShellLog.Info($"blip opened id={item.Id} files={plan.FilePaths.Count} temp={payload.HasTemporaryFiles}");
            if (payload.HasTemporaryFiles)
            {
                ShowToast($"Blip opened. Temp file: {Path.GetDirectoryName(plan.FilePaths[0])}");
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"blip failed id={item.Id}");
            ShowToast("Blip failed. Log saved.");
        }
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string ShareTitle(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Image => "Clip image",
            ClipboardItemKind.Files => item.FilePaths.Count == 1 ? Path.GetFileName(item.FilePaths[0]) : "Clip files",
            ClipboardItemKind.Link => "Clip link",
            ClipboardItemKind.Color => "Clip color",
            _ => "Clip text",
        };
    }

    private static string ShareDescription(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Image => "Image from Clip",
            ClipboardItemKind.Files => item.FilePaths.Count == 1 ? item.FilePaths[0] : $"{item.FilePaths.Count} files from Clip",
            _ => "Text saved as a temporary file by Clip",
        };
    }

    private static void CopyPath(ClipboardHistoryItem item)
    {
        if (item.FilePaths.Count > 0)
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, item.FilePaths));
        }
    }

    private void SaveItem(ClipboardHistoryItem item)
    {
        item = FullTextItem(item);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = item.Kind == ClipboardItemKind.Image ? "clipboard.png" : "clipboard.txt",
            Filter = item.Kind == ClipboardItemKind.Image ? "PNG Image|*.png|All files|*.*" : "Text File|*.txt|All files|*.*",
        };
        _suppressDeactivate = true;
        try
        {
            if (dialog.ShowDialog(this) == true)
            {
                _store.SaveAsFile(item.Id, dialog.FileName);
                ShellLog.Info($"save item id={item.Id} path={dialog.FileName}");
            }
        }
        finally
        {
            _suppressDeactivate = false;
            ShowPalette();
        }
    }

    private void DeleteItem(ClipboardHistoryItem item)
    {
        // Remember the FULL item before the store touches it: the list row may be a summary
        // with truncated text, and Delete removes the asset file along with the entry, so the
        // buffer has to copy those bytes aside while they still exist.
        var full = _store.GetItem(item.Id) ?? item;
        _deleteUndo.Remember(full);
        if (_store.Delete(item.Id))
        {
            if (_selected?.Id == item.Id)
            {
                _selected = null;
            }

            LoadItems(selectFirst: true, reason: "delete");
            ShowToast("Deleted — Ctrl+Z to undo");
            ShellLog.Info($"delete item id={item.Id}");
        }
        else
        {
            // Nothing was deleted, so there is nothing to offer an undo for.
            _deleteUndo.Forget();
        }
    }

    private void RestoreDeletedItem()
    {
        var item = _deleteUndo.TakeRestored();
        if (item is null)
        {
            return;
        }

        // The store's normal add path: same dedupe, sidecar and trim rules as a fresh copy,
        // but with its original timestamps kept so it returns to where it was in the list.
        _store.AddOrUpdate(item, EffectiveHistoryLimit(), refreshCopiedAt: false);
        LoadItems(selectFirst: _selected is null, reason: "undo-delete");
        ShowToast("Restored");
        ShellLog.Info($"undo delete id={item.Id}");
    }

    private void ShowPlaceholder(ClipboardHistoryItem item, string text)
    {
        HidePreviews();
        // The audio mark is a solid, dense shape rather than an outlined document glyph, so it
        // reads much heavier at the same box size and needs to sit smaller.
        var audioMark = item.Kind == ClipboardItemKind.Files &&
            item.FilePaths.Count > 0 &&
            IsAudioFile(Path.GetExtension(item.FilePaths[0]).ToLowerInvariant());

        PlaceholderIcon.Width = audioMark ? 72 : 128;
        PlaceholderIcon.Height = audioMark ? 72 : 128;
        PlaceholderIcon.Source = IconFor(item, 240);
        PlaceholderText.Text = text;
        PlaceholderPreview.Visibility = Visibility.Visible;
    }

    private static Task<CoreWebView2Environment>? _webView2Environment;

    /// <summary>
    /// One environment per process. Creating a fresh one each time and handing it to
    /// <c>EnsureCoreWebView2Async</c> throws "already initialized with a different
    /// CoreWebView2Environment" on the second preview, which silently left the previous file's
    /// page on screen.
    /// </summary>
    private static Task<CoreWebView2Environment> CreateWebView2EnvironmentAsync()
    {
        Directory.CreateDirectory(ClipStoragePaths.WebView2UserDataFolderPath);

        // Document picture-in-picture is what lets the mini window carry Clip's own controls
        // instead of the browser's fixed ones, and it is off by default in WebView2.
        // Chromium backgrounds a window it believes nobody can see, so an off-screen harness looks
        // like it should always need the occlusion flags the jank harness passes. For timing it does
        // not: disabling occlusion left preview timings unchanged (580–1086ms either way) and made
        // the open measurably worse, because an un-throttled browser then competes for the UI
        // thread. The preview cost is real, not an artifact of measuring off screen. For capturing
        // a picture of the pane it is required, because a throttled browser has no frame to give.
        var arguments = "--enable-features=DocumentPictureInPictureAPI";
        if (CapturingPreview)
        {
            arguments += " --disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows";
        }

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = arguments,
        };

        return _webView2Environment ??= CoreWebView2Environment.CreateAsync(
            userDataFolder: ClipStoragePaths.WebView2UserDataFolderPath,
            options: options);
    }

    private Task? _webViewReady;

    /// <summary>
    /// Initializes the WebView2 once; later calls just wait for it to be ready. Single-flighted
    /// through a shared task: two previews racing the first initialization each subscribed the
    /// CoreWebView2 event handlers, and every player message then arrived twice.
    /// </summary>
    private Task EnsureWebViewReadyAsync(Microsoft.Web.WebView2.Wpf.WebView2 view)
    {
        if (_webViewReady is null || _webViewReady.IsFaulted || _webViewReady.IsCanceled)
        {
            _webViewReady = InitWebViewAsync(view);
        }

        return _webViewReady;
    }

    private async Task InitWebViewAsync(Microsoft.Web.WebView2.Wpf.WebView2 view)
    {
        if (view.CoreWebView2 is not null)
        {
            return;
        }

        await view.EnsureCoreWebView2Async(await CreateWebView2EnvironmentAsync());
        var core = view.CoreWebView2 ?? throw new InvalidOperationException("WebView2 initialization completed without a CoreWebView2");
        core.ContainsFullScreenElementChanged += OnWebViewFullScreenChanged;
        core.WebMessageReceived += OnPlayerMessage;

        // Chromium's default scrollbar is the fat stock bar regardless of any WPF styling, so
        // every page this pane ever loads gets the Raycast pill injected (6px, #717176,
        // pixel-measured from Raycast 2026-08-17). Covers code, workbook, media, html-file and
        // rich-text previews in one place; only the built-in PDF viewer draws its own.
        await core.AddScriptToExecuteOnDocumentCreatedAsync("""
            (() => {
                const add = () => {
                    const s = document.createElement('style');
                    s.textContent = '::-webkit-scrollbar{width:6px;height:6px}::-webkit-scrollbar-track{background:transparent}::-webkit-scrollbar-thumb{background:#717176;border-radius:3px}::-webkit-scrollbar-corner{background:transparent}';
                    (document.head || document.documentElement).appendChild(s);
                };
                if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', add); } else { add(); }
            })();
            """);
    }

    private MediaPipWindow? _pipWindow;
    private string? _pipSourcePath;
    private bool _pipSourceIsVideo;

    /// <summary>
    /// The player page asks the host to open picture-in-picture rather than doing it itself,
    /// because the browser's own mini window cannot carry Clip's controls.
    /// </summary>
    private void OnPlayerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var action = MediaPlayerMessage.ActionOf(e.TryGetWebMessageAsString());
        if (action.Name != "pip" || _pipSourcePath is null)
        {
            return;
        }

        OpenPictureInPicture(_pipSourcePath, _pipSourceIsVideo, action.Time);
    }

    private void OpenPictureInPicture(string path, bool isVideo, double startTime)
    {
        _pipWindow?.Close();

        // Put it on whichever monitor the palette is on, in that screen's own coordinates.
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = new Rect(
            screen.WorkingArea.Left / dpi.DpiScaleX,
            screen.WorkingArea.Top / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX,
            screen.WorkingArea.Height / dpi.DpiScaleY);

        OpenBrowserPictureInPicture(path, isVideo, startTime, work);
    }

    private void ResumeInPalette(double resumeAt)
    {
        _pipWindow = null;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowPalette();
            if (_selected is not null)
            {
                _pipResumeTime = resumeAt;
                RenderPreview(_selected);
            }
        }));
    }

    private void OpenBrowserPictureInPicture(string path, bool isVideo, double startTime, Rect work)
    {
        var window = new MediaPipWindow(
            path,
            isVideo,
            startTime,
            BrushHex("Surface"),
            BrushHex("Text"),
            work,
            CreateWebView2EnvironmentAsync);

        window.BackRequested += resumeAt =>
        {
            // Returning to Clip should pick up where the mini window left off.
            _pipWindow = null;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowPalette();
                if (_selected is not null)
                {
                    _pipResumeTime = resumeAt;
                    RenderPreview(_selected);
                }
            }));
        };

        window.Closed += (_, _) => _pipWindow = null;
        _pipWindow = window;
        window.Show();

        // The palette gets out of the way; the mini window is the point.
        ConcealPalette("pip");
        ShellLog.Info($"picture-in-picture opened path={path} at={startTime:0.##}");
    }

    private double _pipResumeTime;

    private WindowState _preFullScreenState;
    private WindowStyle _preFullScreenStyle;
    private double _preFullScreenWidth;
    private double _preFullScreenHeight;
    private double _preFullScreenLeft;
    private double _preFullScreenTop;
    private bool _isMediaFullScreen;

    /// <summary>
    /// Takes the whole monitor when a video goes fullscreen. Without this the palette stays its
    /// fixed 880x560, so "fullscreen" only filled that small window.
    /// </summary>
    private void OnWebViewFullScreenChanged(object? sender, object e)
    {
        if (sender is not CoreWebView2 core)
        {
            return;
        }

        if (core.ContainsFullScreenElement)
        {
            if (_isMediaFullScreen)
            {
                return;
            }

            _isMediaFullScreen = true;
            _preFullScreenState = WindowState;
            _preFullScreenStyle = WindowStyle;
            _preFullScreenWidth = Width;
            _preFullScreenHeight = Height;
            _preFullScreenLeft = Left;
            _preFullScreenTop = Top;

            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = screen.Bounds.Left / dpi.DpiScaleX;
            Top = screen.Bounds.Top / dpi.DpiScaleY;
            Width = screen.Bounds.Width / dpi.DpiScaleX;
            Height = screen.Bounds.Height / dpi.DpiScaleY;
            Shell.CornerRadius = new CornerRadius(0);
            UpdateShellClip();

            // Fullscreen means the video fills the screen, not the whole app blown up. Everything
            // except the preview surface is collapsed so only the player is left.
            SetChromeVisibleForFullScreen(false);
            ShellLog.Info("media fullscreen entered");
            return;
        }

        if (!_isMediaFullScreen)
        {
            return;
        }

        _isMediaFullScreen = false;
        WindowState = _preFullScreenState;
        WindowStyle = _preFullScreenStyle;
        Width = _preFullScreenWidth;
        Height = _preFullScreenHeight;
        Left = _preFullScreenLeft;
        Top = _preFullScreenTop;
        Shell.CornerRadius = new CornerRadius(ShellCornerRadius);
        UpdateShellClip();
        SetChromeVisibleForFullScreen(true);
        ShellLog.Info("media fullscreen exited");
    }

    /// <summary>
    /// Collapses or restores everything around the preview: the search row, the footer, the item
    /// list and the information panel.
    /// </summary>
    private void SetChromeVisibleForFullScreen(bool visible)
    {
        SearchRowDef.Height = visible ? new GridLength(53) : new GridLength(0);
        FooterRowDef.Height = visible ? new GridLength(34) : new GridLength(0);
        ListColumnDef.Width = visible ? new GridLength(320) : new GridLength(0);
        InfoRowDef.Height = visible ? new GridLength(180) : new GridLength(0);
        PreviewArea.Margin = visible ? new Thickness(24, 20, 24, 0) : new Thickness(0);
        PreviewHeaderRowDef.Height = visible ? new GridLength(28) : new GridLength(0);
        PreviewHeaderSpacerDef.Height = visible ? new GridLength(14) : new GridLength(0);
    }

    private FrameworkElement EnsureHtmlPreview()
    {
        if (_htmlPreview is not null)
        {
            return _htmlPreview;
        }

        var htmlPreview = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            Visibility = Visibility.Collapsed,
            // Surface, not Bg: the generated pages paint Surface, so a Bg-colored control
            // flashed an off-color rectangle for the frame before the page painted.
            DefaultBackgroundColor = ToDrawingColor((SolidColorBrush)FindResource("Surface")),
        };
        _htmlPreview = htmlPreview;
        _setHtmlPreviewBackground = color => htmlPreview.DefaultBackgroundColor = color;
        // Added last, so it sits above the placeholder in the visual tree — irrelevant while
        // collapsed, and as an HWND child it draws over WPF content whenever visible anyway.
        PreviewHost.Children.Add(_htmlPreview);
        return _htmlPreview;
    }

    private async Task ShowHtmlPreviewAsync(string path, int token)
    {
        var htmlPreview = (Microsoft.Web.WebView2.Wpf.WebView2)EnsureHtmlPreview();
        await EnsureWebViewReadyAsync(htmlPreview);
        if (token != _previewToken) return;
        await RevealWhenLoadedAsync(htmlPreview, token, () => htmlPreview.CoreWebView2.Navigate(new Uri(path).AbsoluteUri));
    }

    /// <summary>
    /// Opens a document in the WebView2's built-in viewer. Returns false when it could not be
    /// shown, so the caller keeps the existing rendered-image path rather than showing nothing.
    /// </summary>
    private async Task<bool> TryShowDocumentPreviewAsync(string path, int token)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var htmlPreview = (Microsoft.Web.WebView2.Wpf.WebView2)EnsureHtmlPreview();
            await EnsureWebViewReadyAsync(htmlPreview);
            if (token != _previewToken) return false;
            htmlPreview.ZoomFactor = 1.0;

            try
            {
                htmlPreview.CoreWebView2.ClearVirtualHostNameToFolderMapping(MediaVirtualHost);
            }
            catch
            {
                // Nothing mapped yet.
            }

            htmlPreview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                MediaVirtualHost,
                folder,
                CoreWebView2HostResourceAccessKind.Allow);

            return await RevealWhenLoadedAsync(htmlPreview, token, () => htmlPreview.CoreWebView2.Navigate(
                $"https://{MediaVirtualHost}/{Uri.EscapeDataString(Path.GetFileName(path))}"));
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"document preview failed path={path}");
            return false;
        }
    }

    /// <summary>
    /// Loads the document first and shows the pane only once it has arrived, so the loading
    /// placeholder stays up for as long as the wait lasts rather than the pane appearing empty.
    /// The placeholder is collapsed and the pane revealed in the same dispatcher frame, so no
    /// intermediate state ever paints.
    ///
    /// The completion handler only accepts the navigation this call started (matched by the
    /// NavigationId captured from NavigationStarting — the navigation issued here is always the
    /// last one issued, so the first NavigationStarting after attach is ours). A completion from
    /// a superseded earlier navigation must not reveal the pane: that is exactly how a Word
    /// preview used to flash blank or show under another file's name. The reveal also re-checks
    /// the preview token, because by the time the document arrives the user may have moved on —
    /// revealing then would cover the newer item's preview with a stale HWND pane that WPF
    /// cannot draw over.
    ///
    /// The browser suspends drawing while it is hidden but still loads, so waiting costs nothing.
    /// The timeout is there because a document that never finishes loading must not leave the pane
    /// hidden forever — showing a partly-drawn document beats showing none at all.
    /// </summary>
    private async Task<bool> RevealWhenLoadedAsync(Microsoft.Web.WebView2.Wpf.WebView2 view, int token, Action navigate)
    {
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;

        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            navigationId ??= e.NavigationId;
        }

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId is ulong id && e.NavigationId == id)
            {
                loaded.TrySetResult(e.IsSuccess);
            }
        }

        view.CoreWebView2.NavigationStarting += OnStarting;
        view.CoreWebView2.NavigationCompleted += OnCompleted;

        bool ok;
        try
        {
            navigate();
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            ok = done != loaded.Task || loaded.Task.Result;
        }
        finally
        {
            view.CoreWebView2.NavigationStarting -= OnStarting;
            view.CoreWebView2.NavigationCompleted -= OnCompleted;
        }

        if (!ok || token != _previewToken)
        {
            return false;
        }

        HidePreviews(pauseMedia: false);
        if (_settingsOverlay is not null)
        {
            // The page loaded fine, but revealing the browser now would put its HWND on top of the
            // settings overlay (e.g. a theme change inside settings re-renders the preview).
            // CloseHostedSettings restores visibility.
            _previewHiddenForSettings = true;
            BenchMarks.Mark("preview-ready");
            return true;
        }

        view.Visibility = Visibility.Visible;
        BenchMarks.Mark("preview-ready");
        return true;
    }

    private bool _warmingOfficePreviews;

    /// <summary>
    /// Exports the recent documents that have no cached preview yet, quietly, while the palette is
    /// open and the user is reading it.
    ///
    /// Driving Word or PowerPoint takes tens of seconds the first time, and doing it at the moment
    /// the row is clicked puts every second of that in front of the user. Doing it beforehand
    /// spends the same time where nobody is waiting on it, and a preview whose export already
    /// finished opens as fast as the cache can be read. One document at a time: several Office
    /// applications starting at once is slower than doing them in turn, and far more disruptive.
    /// </summary>
    private void WarmOfficePreviewsInBackground()
    {
        if (_warmingOfficePreviews)
        {
            return;
        }

        var pending = _allItems
            .Select(item => item.FilePaths is { Count: 1 } paths ? paths[0] : null)
            .Where(path => path is not null
                && IsPdfBackedOfficeFile(Path.GetExtension(path).ToLowerInvariant())
                && !ExcelWorkbookReader.CanRead(path)
                && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        _warmingOfficePreviews = true;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var path in pending)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var exported = WatcherStaticDocumentPreviewRenderer.TryExportDocumentPdfOnStaThread(path!);
                    ShellLog.Info(
                        $"preview warmed path={path} ok={exported is not null} elapsedMs={watch.ElapsedMilliseconds}");
                }
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "warming office previews failed");
            }
            finally
            {
                _warmingOfficePreviews = false;
            }
        });
    }

    private async Task ShowWorkbookPreviewAsync(IReadOnlyList<ExcelSheet> sheets, string path, int token)
    {
        var htmlPreview = (Microsoft.Web.WebView2.Wpf.WebView2)EnsureHtmlPreview();
        await EnsureWebViewReadyAsync(htmlPreview);
        if (token != _previewToken) return;

        // Building tens of thousands of HTML cells froze the palette when done on the UI thread.
        var surface = BrushHex("Surface");
        var text = BrushHex("Text");
        var html = await Task.Run(() => ExcelPreviewPage.Build(sheets, Path.GetFileName(path), surface, text));
        if (token != _previewToken) return;

        await RevealWhenLoadedAsync(htmlPreview, token, () => htmlPreview.CoreWebView2.NavigateToString(html));
    }

    private async Task ShowCodePreviewAsync(string path, int token)
    {
        var htmlPreview = (Microsoft.Web.WebView2.Wpf.WebView2)EnsureHtmlPreview();
        BenchMarks.Mark("code-view-created");
        await EnsureWebViewReadyAsync(htmlPreview);
        BenchMarks.Mark("code-webview-ready");
        if (token != _previewToken) return;

        // Build reads and highlights up to 400k characters — off the UI thread.
        var surface = BrushHex("Surface");
        var text = BrushHex("Text");
        var muted = BrushHex("Muted");
        var accent = BrushHex("Accent");
        var html = await Task.Run(() =>
        {
            BenchMarks.Mark("code-build-started");
            var built = CodePreviewPage.Build(path, surface, text, muted, accent);
            BenchMarks.Mark("code-build-done");
            return built;
        });
        BenchMarks.Mark("code-html-built");
        if (token != _previewToken) return;

        await RevealWhenLoadedAsync(htmlPreview, token, () => htmlPreview.CoreWebView2.NavigateToString(html));
    }

    /// <summary>
    /// Shows a video or audio file in a small player. The WebView2 that already backs HTML
    /// previews supplies real playback controls — scrubbing, volume, speed — so Clip does not need
    /// a media stack of its own.
    /// </summary>
    private async Task ShowMediaPreviewAsync(string path, bool isVideo, int token)
    {
        var htmlPreview = (Microsoft.Web.WebView2.Wpf.WebView2)EnsureHtmlPreview();
        await EnsureWebViewReadyAsync(htmlPreview);
        if (token != _previewToken) return;

        // A generated page has no file-system origin, so the media is served from a virtual host
        // mapped to the file's own folder rather than referenced as file://.
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        // Re-mapping a host that is already mapped throws, which would abort before navigating and
        // leave the previously selected file's player on screen. Always clear first.
        try
        {
            htmlPreview.CoreWebView2.ClearVirtualHostNameToFolderMapping(MediaVirtualHost);
        }
        catch
        {
            // Nothing was mapped yet.
        }

        htmlPreview.CoreWebView2.SetVirtualHostNameToFolderMapping(
            MediaVirtualHost,
            folder,
            CoreWebView2HostResourceAccessKind.Allow);

        // No page zoom. It was only ever there to shrink Chrome's own overflow menu, and the
        // controls are drawn by Clip now — zooming just skews the layout inside the pane.
        htmlPreview.ZoomFactor = 1.0;

        // Remembered so a picture-in-picture request from the page knows what to open.
        _pipSourcePath = path;
        _pipSourceIsVideo = isVideo;

        var resumeAt = _pipResumeTime;
        _pipResumeTime = 0;

        var mediaUrl = $"https://{MediaVirtualHost}/{Uri.EscapeDataString(Path.GetFileName(path))}";
        var html = MediaPreviewPage.Build(
            path,
            mediaUrl,
            isVideo,
            BrushHex("Surface"),
            BrushHex("Text"),
            detached: false,
            startTime: resumeAt);

        // Revealed only once the player page has loaded, like every other browser-backed
        // preview. Showing the pane before navigating flashed the previous page (or a blank
        // one) over the loading placeholder.
        await RevealWhenLoadedAsync(htmlPreview, token, () => htmlPreview.CoreWebView2.NavigateToString(html));
    }

    private const string MediaVirtualHost = "clip-media.local";
    private const double MediaZoomFactor = 0.7;

    private readonly System.Windows.Threading.DispatcherTimer _htmlPreviewIdleTimer = new()
    {
        Interval = TimeSpan.FromMinutes(3),
    };

    private void OnHtmlPreviewIdle(object? sender, EventArgs e)
    {
        _htmlPreviewIdleTimer.Stop();
        // Cloaked windows stay IsVisible, so "palette concealed" is _paletteOpen now, not IsVisible.
        if (!_paletteOpen)
        {
            DisposeHtmlPreview();
            ReleaseIdleImagePreviews();
            ShellLog.Info("html preview released after idle");
        }
    }

    /// <summary>
    /// Lets go of the decoded 900px previews once the palette has sat concealed for the same
    /// three minutes that tear the WebView2 down. A dozen screenshot-sized decodes pin 20-25MB
    /// while nobody is looking; the reveal path re-renders the selected preview anyway, and the
    /// neighbour prefetch refills the cache on the first arrow step. The 48px row-icon cache is
    /// deliberately kept — it is tiny and it is what makes a reopen paint instantly.
    /// </summary>
    private void ReleaseIdleImagePreviews()
    {
        lock (RasterImageCacheGate)
        {
            PreviewImageCache.Clear();
        }

        ImagePreview.Source = null;
        _currentPreviewImagePath = null;
    }

    // Tears down the WebView2 (and its Chromium processes) so nothing browser-related
    // lingers while the palette is hidden. Recreated lazily on the next HTML preview.
    private void DisposeHtmlPreview()
    {
        if (_htmlPreview is null)
        {
            return;
        }

        try
        {
            PreviewHost.Children.Remove(_htmlPreview);
            (_htmlPreview as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "html preview dispose failed");
        }
        finally
        {
            _htmlPreview = null;
            _setHtmlPreviewBackground = null;
            _webViewReady = null;
        }
    }

    /// <summary>
    /// Stops any playing media the moment the pane is hidden, without navigating. The old
    /// approach navigated to a blank page, but that blank navigation's completion raced the next
    /// preview's reveal and resurrected an empty pane over it — the collapsed WebView2 never
    /// shows stale content anyway, because the reveal path only makes it visible after its own
    /// navigation completes for the currently selected item.
    /// </summary>
    private void PauseHtmlPreviewMedia()
    {
        try
        {
            _ = (_htmlPreview as Microsoft.Web.WebView2.Wpf.WebView2)?.CoreWebView2?
                .ExecuteScriptAsync("document.querySelectorAll('video,audio').forEach(m => m.pause())");
        }
        catch
        {
            // Nothing loaded yet, or the browser is mid-teardown; either way nothing is playing.
        }
    }

    /// <summary>
    /// pauseMedia is false only when called from RevealWhenLoadedAsync, which is about to show
    /// the page it just loaded — pausing there would stop the very player being revealed.
    /// </summary>
    private void HidePreviews(bool pauseMedia = true)
    {
        CloseExpandedImage();
        TextPreview.Visibility = Visibility.Collapsed;
        ImagePreview.Visibility = Visibility.Collapsed;
        ExpandImageButton.Visibility = Visibility.Collapsed;
        if (_htmlPreview is not null)
        {
            _htmlPreview.Visibility = Visibility.Collapsed;
            if (pauseMedia)
            {
                PauseHtmlPreviewMedia();
            }
        }

        PlaceholderPreview.Visibility = Visibility.Collapsed;
        ColorPreview.Visibility = Visibility.Collapsed;
        TextPreview.Text = string.Empty;
        ImagePreview.Source = null;
        _currentPreviewImagePath = null;
        _currentPreviewPdfPath = null;
    }

    private void OnExpandImageClick(object sender, RoutedEventArgs e)
    {
        var source = BestExpandedImageSource();
        if (source is null)
        {
            return;
        }

        CloseActionMenus();
        ExpandedImage.Source = source;
        SetExpandedImageNaturalSize(source);
        ExpandWindowForImage();
        ExpandedBackdrop.Source = CaptureShellBackdrop();
        ExpandedImageOverlay.Visibility = Visibility.Visible;
        ExpandedImageOverlay.UpdateLayout();
        ExpandedImageOverlay.Focus();
        ResetExpandedImageView();
        ShellLog.Info($"image expanded size={ExpandedImage.Width:0}x{ExpandedImage.Height:0}");
        e.Handled = true;
    }

    private ImageSource? BestExpandedImageSource()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_currentPreviewImagePath) && File.Exists(_currentPreviewImagePath))
            {
                return LoadBitmap(_currentPreviewImagePath);
            }

            if (!string.IsNullOrWhiteSpace(_currentPreviewPdfPath) && File.Exists(_currentPreviewPdfPath) &&
                WatcherPdfPreviewRenderer.TryRenderFirstPage(_currentPreviewPdfPath, out var pdfImage, 300))
            {
                using (pdfImage)
                {
                    return BitmapFromDrawingImage(pdfImage);
                }
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "best expanded image load failed");
        }

        return ImagePreview.Source;
    }

    private void SetExpandedImageNaturalSize(ImageSource source)
    {
        if (source is BitmapSource bitmap)
        {
            var dpiX = bitmap.DpiX > 0 ? bitmap.DpiX : 96;
            var dpiY = bitmap.DpiY > 0 ? bitmap.DpiY : 96;
            _expandedImageNaturalWidth = Math.Max(1, bitmap.PixelWidth * 96.0 / dpiX);
            _expandedImageNaturalHeight = Math.Max(1, bitmap.PixelHeight * 96.0 / dpiY);
            ExpandedImage.Width = _expandedImageNaturalWidth;
            ExpandedImage.Height = _expandedImageNaturalHeight;
            return;
        }

        _expandedImageNaturalWidth = Math.Max(1, double.IsNaN(source.Width) || source.Width <= 0 ? ActualWidth : source.Width);
        _expandedImageNaturalHeight = Math.Max(1, double.IsNaN(source.Height) || source.Height <= 0 ? ActualHeight : source.Height);
        ExpandedImage.Width = _expandedImageNaturalWidth;
        ExpandedImage.Height = _expandedImageNaturalHeight;
    }

    private void ExpandWindowForImage()
    {
        if (!_expandedWindowResized)
        {
            _expandedRestoreBounds = new Rect(Left, Top, Width, Height);
            _expandedRestoreCornerRadius = Shell.CornerRadius;
            _expandedWindowResized = true;
        }

        var screen = Forms.Screen.FromPoint(Forms.Control.MousePosition).Bounds;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = bottomRight.X - topLeft.X;
        Height = bottomRight.Y - topLeft.Y;
        Shell.CornerRadius = new CornerRadius(0);
        UpdateShellClip();
        UpdateLayout();
    }

    private void RestoreWindowAfterImage()
    {
        if (!_expandedWindowResized)
        {
            return;
        }

        Left = _expandedRestoreBounds.Left;
        Top = _expandedRestoreBounds.Top;
        Width = _expandedRestoreBounds.Width;
        Height = _expandedRestoreBounds.Height;
        Shell.CornerRadius = _expandedRestoreCornerRadius;
        UpdateShellClip();
        _expandedWindowResized = false;
        UpdateLayout();
    }

    private ImageSource? CaptureShellBackdrop()
    {
        try
        {
            var width = Math.Max(1, (int)Math.Round(ActualWidth));
            var height = Math.Max(1, (int)Math.Round(ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Shell);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "expanded backdrop capture failed");
            return null;
        }
    }

    private static string? ClipboardTextOrNull(System.Windows.TextDataFormat format)
    {
        try
        {
            return System.Windows.Clipboard.ContainsText(format) ? System.Windows.Clipboard.GetText(format) : null;
        }
        catch
        {
            return null;
        }
    }

    private void ResetExpandedImageView()
    {
        ExpandedImageViewport.UpdateLayout();
        var viewportWidth = Math.Max(1, ExpandedImageViewport.ActualWidth);
        var viewportHeight = Math.Max(1, ExpandedImageViewport.ActualHeight);
        var fitWidth = Math.Max(1, viewportWidth - 48);
        var fitHeight = Math.Max(1, viewportHeight - 48);
        var imageWidth = Math.Max(1, _expandedImageNaturalWidth);
        var imageHeight = Math.Max(1, _expandedImageNaturalHeight);
        var fitScale = Math.Min(fitWidth / imageWidth, fitHeight / imageHeight);
        _expandedImageZoom = Math.Clamp(fitScale < 1 ? fitScale : 1.0, 0.05, 32.0);
        var scaledWidth = imageWidth * _expandedImageZoom;
        var scaledHeight = imageHeight * _expandedImageZoom;
        SetExpandedImageBounds(
            (viewportWidth - scaledWidth) / 2,
            (viewportHeight - scaledHeight) / 2,
            scaledWidth,
            scaledHeight);
    }

    private void CloseExpandedImage()
    {
        if (ExpandedImageOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        _expandedImagePanning = false;
        _expandedImageDownOnImage = false;
        ExpandedImageOverlay.ReleaseMouseCapture();
        ExpandedImageOverlay.Visibility = Visibility.Collapsed;
        ExpandedImage.Source = null;
        ExpandedBackdrop.Source = null;
        RestoreWindowAfterImage();
        ShellLog.Info("image expanded closed");
    }

    private void OnExpandedOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _expandedImageDownOnImage = IsPointOverExpandedImage(e.GetPosition(ExpandedImageViewport));
        if (!_expandedImageDownOnImage)
        {
            CloseExpandedImage();
            e.Handled = true;
            return;
        }

        _expandedImagePanning = true;
        _expandedImageLastPoint = e.GetPosition(ExpandedImageOverlay);
        _expandedImageDownPoint = _expandedImageLastPoint;
        _expandedImageMoved = false;
        ExpandedImageOverlay.CaptureMouse();
        ExpandedImageOverlay.Cursor = System.Windows.Input.Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnExpandedOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var releasedOffImage = !IsPointOverExpandedImage(e.GetPosition(ExpandedImageViewport));
        StopExpandedImagePan();
        if (!_expandedImageMoved && !_expandedImageDownOnImage && releasedOffImage)
        {
            CloseExpandedImage();
        }

        _expandedImageDownOnImage = false;
        e.Handled = true;
    }

    private void OnExpandedOverlayMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopExpandedImagePan();
        }
    }

    private void OnExpandedOverlayMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_expandedImagePanning || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(ExpandedImageOverlay);
        if (!_expandedImageMoved && Distance(point, _expandedImageDownPoint) > 2)
        {
            _expandedImageMoved = true;
        }

        PanExpandedImage(point.X - _expandedImageLastPoint.X, point.Y - _expandedImageLastPoint.Y);
        _expandedImageLastPoint = point;
        e.Handled = true;
    }

    private void OnExpandedOverlayMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomExpandedImage(Math.Pow(1.0018, e.Delta), e.GetPosition(ExpandedImageViewport));
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            PanExpandedImage(-e.Delta, 0);
        }
        else
        {
            PanExpandedImage(0, e.Delta);
        }

        e.Handled = true;
    }

    private void OnExpandedOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ExpandedImageOverlay.Visibility == Visibility.Visible && ExpandedImage.Source is not null)
        {
            ClampExpandedImage();
        }
    }

    private void StopExpandedImagePan()
    {
        _expandedImagePanning = false;
        ExpandedImageOverlay.ReleaseMouseCapture();
        ExpandedImageOverlay.Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void PanExpandedImage(double deltaX, double deltaY)
    {
        Canvas.SetLeft(ExpandedImage, ExpandedImageLeft() + deltaX);
        Canvas.SetTop(ExpandedImage, ExpandedImageTop() + deltaY);
        ClampExpandedImage();
    }

    private void ZoomExpandedImage(double factor, System.Windows.Point center)
    {
        var oldZoom = _expandedImageZoom;
        var newZoom = Math.Clamp(oldZoom * factor, 0.02, 128.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            return;
        }

        var imageX = (center.X - ExpandedImageLeft()) / oldZoom;
        var imageY = (center.Y - ExpandedImageTop()) / oldZoom;
        _expandedImageZoom = newZoom;
        ExpandedImage.Width = _expandedImageNaturalWidth * newZoom;
        ExpandedImage.Height = _expandedImageNaturalHeight * newZoom;
        Canvas.SetLeft(ExpandedImage, center.X - imageX * newZoom);
        Canvas.SetTop(ExpandedImage, center.Y - imageY * newZoom);
        ClampExpandedImage();
    }

    private void ClampExpandedImage()
    {
        var viewportWidth = Math.Max(1, ExpandedImageViewport.ActualWidth);
        var viewportHeight = Math.Max(1, ExpandedImageViewport.ActualHeight);
        var scaledWidth = Math.Max(1, ExpandedImage.Width);
        var scaledHeight = Math.Max(1, ExpandedImage.Height);

        if (scaledWidth <= viewportWidth)
        {
            Canvas.SetLeft(ExpandedImage, (viewportWidth - scaledWidth) / 2);
        }
        else
        {
            Canvas.SetLeft(ExpandedImage, Math.Clamp(ExpandedImageLeft(), viewportWidth - scaledWidth, 0));
        }

        if (scaledHeight <= viewportHeight)
        {
            Canvas.SetTop(ExpandedImage, (viewportHeight - scaledHeight) / 2);
        }
        else
        {
            Canvas.SetTop(ExpandedImage, Math.Clamp(ExpandedImageTop(), viewportHeight - scaledHeight, 0));
        }
    }

    private bool IsPointOverExpandedImage(System.Windows.Point point)
    {
        var left = ExpandedImageLeft();
        var top = ExpandedImageTop();
        var right = left + Math.Max(1, ExpandedImage.Width);
        var bottom = top + Math.Max(1, ExpandedImage.Height);
        return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
    }

    private void SetExpandedImageBounds(double left, double top, double width, double height)
    {
        ExpandedImage.Width = Math.Max(1, width);
        ExpandedImage.Height = Math.Max(1, height);
        Canvas.SetLeft(ExpandedImage, left);
        Canvas.SetTop(ExpandedImage, top);
    }

    private double ExpandedImageLeft()
    {
        var left = Canvas.GetLeft(ExpandedImage);
        return double.IsNaN(left) ? 0 : left;
    }

    private double ExpandedImageTop()
    {
        var top = Canvas.GetTop(ExpandedImage);
        return double.IsNaN(top) ? 0 : top;
    }

    private System.Windows.Point MousePointInExpandedViewport()
    {
        return ExpandedImageViewport.PointFromScreen(new System.Windows.Point(Forms.Control.MousePosition.X, Forms.Control.MousePosition.Y));
    }

    private static short WheelDelta(IntPtr wParam)
    {
        return unchecked((short)(((long)wParam >> 16) & 0xffff));
    }

    private static double Distance(System.Windows.Point a, System.Windows.Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void PositionOnMouseScreen(bool log = true)
    {
        // Center the palette on whichever monitor the mouse is on, using raw Win32 screen pixels
        // for BOTH the monitor work area and the window size. Staying in one coordinate space makes
        // it work across monitors with different display scaling (DPI). The previous WPF DIP-transform
        // math used the window's current monitor scaling for a DIFFERENT target monitor, so on a
        // differently-scaled second screen the window landed off-screen and never appeared.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var windowRect))
        {
            return;
        }

        var work = info.Work;
        var workWidth = work.Right - work.Left;
        var workHeight = work.Bottom - work.Top;

        // Size the window for the TARGET monitor rather than measuring whatever it happens to be
        // right now. Since the app went Per-Monitor-V2 DPI aware, moving between monitors of
        // different scale makes Windows rescale the window, and GetWindowRect raced that: the log
        // showed the same monitor centring against win=1200x780 on one open and win=800x520 on the
        // next, so half the time it centred the unscaled size and the palette landed low and right.
        // Width/Height are DIPs and never race; the target monitor's scale is a property of the
        // monitor. Placing and sizing in one call means there is no in-between frame to rescale.
        // Re-assert the design size before placing. The palette has one size and should never
        // appear larger or smaller, but the log caught it opening at 800x520, 1200x780 and once
        // 1800x1170 DIPs — always the previous physical size adopted as the new logical one, each
        // trip through a differently-scaled monitor inflating it again by that monitor's scale.
        // WPF mishandles WM_DPICHANGED for a layered window, and this window is layered for the
        // acrylic. Since the size is a constant there is nothing to preserve: set it back. The
        // media-fullscreen and expanded-image modes own the size while they are on, so they are
        // left alone.
        if (!_isMediaFullScreen && !_expandedWindowResized)
        {
            Width = PaletteDesignWidth;
            Height = PaletteDesignHeight;
        }

        var scale = MonitorScale(monitor);
        var (x, y, windowWidth, windowHeight) = CenteredPlacement(
            work.Left,
            work.Top,
            workWidth,
            workHeight,
            Width,
            Height,
            scale,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);
        // NoSize is load-bearing. Passing a size here looks harmless and is not: WPF reads the new
        // physical size back into Width/Height as DIPs, so on a 150% monitor an 800-DIP palette set
        // to 800 physical becomes 533 DIPs, and every open shrinks it again. The size is only ever
        // computed to place the window; Windows applies the real one when it rescales for the
        // target monitor.
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate);
        if (log)
        {
            ShellLog.Info($"position(win32) cursor={cursor.X},{cursor.Y} work={work.Left},{work.Top} {workWidth}x{workHeight} scale={scale:0.##} win={windowWidth}x{windowHeight} -> {x},{y}");
        }
    }

    /// <summary>
    /// Where and how big the palette goes to sit centred on one monitor's work area, all in that
    /// monitor's physical pixels.
    ///
    /// The size comes from the DIP size times the target monitor's scale, never from measuring the
    /// window, because the measurement races Per-Monitor-V2 rescaling — that race is what put the
    /// palette off-centre on roughly every other open. <paramref name="measuredWidth"/> and
    /// <paramref name="measuredHeight"/> are only the fallback for before the first layout, when
    /// Width/Height are still NaN.
    ///
    /// The returned size is what the window is ABOUT to be once Windows rescales it for that
    /// monitor; it is there to place the window, not to resize it. Actually applying it would feed
    /// the physical size back into WPF's DIP Width/Height and shrink the palette on every open.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) CenteredPlacement(
        int workLeft,
        int workTop,
        int workWidth,
        int workHeight,
        double dipWidth,
        double dipHeight,
        double scale,
        int measuredWidth,
        int measuredHeight)
    {
        var width = double.IsNaN(dipWidth) || dipWidth <= 0 ? measuredWidth : (int)Math.Round(dipWidth * scale);
        var height = double.IsNaN(dipHeight) || dipHeight <= 0 ? measuredHeight : (int)Math.Round(dipHeight * scale);

        // A palette wider than the work area is pinned to the top-left corner rather than hung off
        // the left edge, which is what a negative half-difference would do.
        var x = workLeft + Math.Max(0, (workWidth - width) / 2);
        var y = workTop + Math.Max(0, (workHeight - height) / 2);
        return (x, y, width, height);
    }

    /// <summary>
    /// The target monitor's scale factor (1.5 at 150%), read from the monitor itself rather than
    /// from this window — the window may still be on the old monitor, at the old scale, which is
    /// the whole reason the centring used to be wrong. Falls back to 1.0 on the pre-8.1 machines
    /// where GetDpiForMonitor is missing, which is also the only place it is genuinely always 96.
    /// </summary>
    private static double MonitorScale(IntPtr monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, MonitorDpiEffective, out var dpiX, out _) == 0 && dpiX > 0
                ? dpiX / 96.0
                : 1.0;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "monitor dpi lookup failed");
            return 1.0;
        }
    }

    private static void WarmMouseScreenCache()
    {
        _ = WorkingAreaForMouse(Forms.Control.MousePosition);
    }

    private static System.Drawing.Rectangle WorkingAreaForMouse(System.Drawing.Point mouse)
    {
        if (_hasCachedMouseScreenWorkingArea && _cachedMouseScreenWorkingArea.Contains(mouse))
        {
            return _cachedMouseScreenWorkingArea;
        }

        _cachedMouseScreenWorkingArea = Forms.Screen.FromPoint(mouse).WorkingArea;
        _hasCachedMouseScreenWorkingArea = true;
        return _cachedMouseScreenWorkingArea;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }
    private void OnAllFilterClick(object sender, RoutedEventArgs e) => SetFilter("all");
    private void OnTextFilterClick(object sender, RoutedEventArgs e) => SetFilter("text");
    private void OnImageFilterClick(object sender, RoutedEventArgs e) => SetFilter("images");
    private void OnLinksFilterClick(object sender, RoutedEventArgs e) => SetFilter("links");
    private void OnColorFilterClick(object sender, RoutedEventArgs e) => SetFilter("colors");
    private void OnFilesFilterClick(object sender, RoutedEventArgs e) => SetFilter("files");
    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (_selected.Kind == ClipboardItemKind.Text) EditText(_selected);
        else OpenItem(_selected);
    }

    private void OnNewSnippetClick(object sender, RoutedEventArgs e) => NewSnippet();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsInternal(showPaletteOnClose: true);

    public void OpenSettingsFromTray() => OpenSettingsInternal(showPaletteOnClose: false);

    private void OpenSettingsForDebug()
    {
        DebugOpenSettings = false;
        OpenSettingsInternal(showPaletteOnClose: false);
    }

    public void PasteLatestFromTray()
    {
        var item = LatestClipboardItem();
        if (item is null)
        {
            ShellLog.Info("tray paste latest skipped — no items");
            return;
        }

        var foreground = GetForegroundWindow();
        var own = new WindowInteropHelper(this).Handle;
        if (foreground != IntPtr.Zero && foreground != own)
        {
            CaptureReturnFocus(foreground);
        }

        var previous = _selected;
        _selected = item;
        try
        {
            PasteSelected();
            ShellLog.Info($"tray paste latest id={item.Id}");
        }
        finally
        {
            _selected = previous;
        }
    }

    private ClipboardHistoryItem? LatestClipboardItem()
    {
        var items = _allItems;
        if (items.Count == 0 || !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            items = _store.QueryItemSummaries();
            _allItems = items;
        }

        return items.OrderByDescending(i => i.LastCopiedAt).FirstOrDefault();
    }

    private void OpenSettingsInternal(bool showPaletteOnClose)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            ShellLog.Info($"settings opening showPaletteOnClose={showPaletteOnClose}");
            SettingsWindow? settings = null;
            Border? overlay = null;
            if (Shell.Child is Grid)
            {
                TryTakePrewarmedHostedSettings(out settings, out overlay);
            }

            settings ??= CreateSettingsWindow();

            if (TryShowHostedSettings(settings, showPaletteOnClose, watch, overlay))
            {
                return;
            }

            _suppressDeactivate = true;
            settings.ContentRendered += (_, _) => ShellLog.Info($"settings rendered elapsedMs={watch.ElapsedMilliseconds} showPaletteOnClose={showPaletteOnClose}");
            settings.Closed += (_, _) => CloseStandaloneSettings(showPaletteOnClose);
            settings.Show();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings failed");
            _suppressDeactivate = false;
            ShowToast("Settings failed. Log saved.");
        }
    }

    private SettingsWindow CreateSettingsWindow() => new(_settings, _lastUpdateStatus, ApplyTheme, RefreshClipboardManagerTextTheme, ApplyAppIcon, ApplyRunAtStartup, ApplyHistoryLimit, ApplyMaxItemSize, ApplyUpdateSettings, CheckForUpdatesFromSettings, InstallUpdateAsync, OpenDataFolder, OpenDebugLog, ClearHistory, ExportHistory, RestoreHistory, ChangeClipboardFolder, ResetClipboardFolder, ApplyHotkeys, ApplyPrivacy, ApplyDefaultPasteFormat, ApplyExtractTextFromImages, ApplySourceAppInList, ResetAllSettings, CurrentSettingsPalette)
    {
        Owner = this,
    };

    private bool TryTakePrewarmedHostedSettings(out SettingsWindow? settings, out Border? overlay)
    {
        settings = null;
        overlay = null;
        if (!_prewarmedSettingsReady || _prewarmedSettings is null || _prewarmedSettingsOverlay is null)
        {
            return false;
        }

        settings = _prewarmedSettings;
        overlay = _prewarmedSettingsOverlay;
        _prewarmedSettings = null;
        _prewarmedSettingsOverlay = null;
        _prewarmedSettingsReady = false;
        ShellLog.Info("settings using prewarmed panel");
        return true;
    }

    private bool TryShowHostedSettings(SettingsWindow settings, bool showPaletteOnClose, Stopwatch watch, Border? preparedOverlay = null)
    {
        if (Shell.Child is not Grid host)
        {
            return false;
        }

        CloseHostedSettings(logClose: false);

        var overlay = preparedOverlay ?? CreateHostedSettingsOverlay(settings);
        overlay.Opacity = 1;
        overlay.IsHitTestVisible = true;

        _hostedSettings = settings;
        _settingsOverlay = overlay;
        _settingsOverlayKeepPaletteOnClose = showPaletteOnClose;
        _suppressDeactivate = true;

        if (_htmlPreview is { Visibility: Visibility.Visible } preview)
        {
            preview.Visibility = Visibility.Collapsed;
            PauseHtmlPreviewMedia();
            _previewHiddenForSettings = true;
        }

        if (!ReferenceEquals(overlay.Parent, host))
        {
            host.Children.Add(overlay);
        }

        if (!IsVisible || Opacity == 0 || !IsHitTestVisible)
        {
            ShowPalette(loadItems: false);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShellLog.Info($"settings rendered elapsedMs={watch.ElapsedMilliseconds} showPaletteOnClose={showPaletteOnClose} hosted=True");
            _ = Dispatcher.BeginInvoke(new Action(() => overlay.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }), System.Windows.Threading.DispatcherPriority.Render);
        return true;
    }

    private Border CreateHostedSettingsOverlay(SettingsWindow settings)
    {
        var content = settings.DetachForHost(CloseHostedSettings);
        content.Width = 720;
        content.Height = 500;
        content.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;

        var overlay = new Border
        {
            Background = WpfBrushes.Transparent,
            Child = content,
            Focusable = true,
        };
        Grid.SetRowSpan(overlay, 4);
        System.Windows.Controls.Panel.SetZIndex(overlay, 1000);
        overlay.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseHostedSettings();
                e.Handled = true;
            }
        };
        return overlay;
    }

    private void CloseHostedSettings() => CloseHostedSettings(logClose: true);

    private void CloseHostedSettings(bool logClose)
    {
        var hadOverlay = _settingsOverlay is not null;
        if (_settingsOverlay is not null && Shell.Child is Grid host)
        {
            host.Children.Remove(_settingsOverlay);
        }

        _settingsOverlay = null;
        _hostedSettings = null;
        var keepPalette = _settingsOverlayKeepPaletteOnClose;
        _settingsOverlayKeepPaletteOnClose = false;

        if (!hadOverlay)
        {
            return;
        }

        if (_previewHiddenForSettings)
        {
            _previewHiddenForSettings = false;
            if (_htmlPreview is not null)
            {
                _htmlPreview.Visibility = Visibility.Visible;
            }
        }

        _suppressDeactivate = false;

        if (logClose)
        {
            ShellLog.Info("settings closed");
        }

        if (!keepPalette && !_isClosing)
        {
            ConcealPalette("settings-closed");
        }

        if (!_isClosing)
        {
            PrewarmHostedSettingsSoon();
        }
    }

    private void CloseStandaloneSettings(bool showPaletteOnClose)
    {
        _suppressDeactivate = false;
        ShellLog.Info("settings closed");
        if (showPaletteOnClose && !_isClosing)
        {
            ShowPalette();
        }
    }

    private void ApplyAppIcon(AppIconPreference preference) => ApplyAppIcon(preference, save: true);

    private SettingsPalette CurrentSettingsPalette() => new(
        (WpfBrush)FindResource("Bg"),
        (WpfBrush)FindResource("Surface"),
        (WpfBrush)FindResource("Surface2"),
        (WpfBrush)FindResource("Surface3"),
        (WpfBrush)FindResource("Text"),
        (WpfBrush)FindResource("Muted"),
        (WpfBrush)FindResource("Line"),
        (WpfBrush)FindResource("Line2"),
        (WpfBrush)FindResource("Accent"),
        (WpfBrush)FindResource("AccentSoft"),
        (WpfBrush)FindResource("Selected"),
        (WpfBrush)FindResource("SelectedBorder"));

    private void ApplyAppIcon(AppIconPreference preference, bool save)
    {
        _settings.AppIcon = preference;
        ApplyWindowTitleIcon(preference);
        var iconPath = AppIconPath(preference);

        if (save)
        {
            _settings.Save();
            AppIconChanged?.Invoke(preference);
            UpdateInstalledShortcutIcons(iconPath);
            ShowToast($"Icon set to {preference}");
        }

        ShellLog.Info($"app icon applied preference={preference} path={iconPath}");
    }

    private void ApplyRunAtStartup(bool enabled)
    {
        try
        {
            StartupRegistration.SetEnabled(enabled);
            var value = StartupRegistration.CurrentValue() ?? "none";
            ShellLog.Info($"startup preference changed enabled={enabled} value={value}");
            ShowToast(enabled ? "Startup enabled" : "Startup disabled");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"startup preference failed enabled={enabled}");
            ShowToast("Startup setting failed. Log saved.");
        }
    }

    private void ApplyHistoryLimit(int? limit)
    {
        _settings.HistoryLimit = limit;
        _settings.Save();
        var removed = _store.ApplyHistoryLimit(EffectiveHistoryLimit());
        _allItems = _store.QueryItemSummaries();
        _historySummariesPreloaded = true;
        if (_selected is not null && _allItems.All(item => item.Id != _selected.Id))
        {
            _selected = null;
        }

        RenderItems("history-limit");
        SelectItem(FilteredItems().FirstOrDefault(), "history-limit");
        ShellLog.Info($"history limit changed limit={HistoryLimitLabel(limit)} removed={removed}");
        ShowToast($"History limit set to {HistoryLimitLabel(limit)}");
    }

    private void ApplyMaxItemSize(long? maxBytes)
    {
        _settings.MaxItemSizeBytes = maxBytes;
        _settings.Save();
        ShellLog.Info($"max item size changed limit={ClipItemSizeLimit.MaxItemSizeLabel(maxBytes)}");
        ShowToast($"Max item size set to {ClipItemSizeLimit.MaxItemSizeLabel(maxBytes)}");
    }

    private void ApplyUpdateSettings(bool checkOnStartup, bool autoInstall)
    {
        _settings.CheckForUpdatesOnStartup = checkOnStartup;
        _settings.InstallUpdatesAutomatically = autoInstall;
        _settings.Save();
        ApplyUpdateCheckSchedule();
        ShellLog.Info($"update settings changed checkOnStartup={checkOnStartup} autoInstall={autoInstall}");
        ShowToast("Update settings saved");
    }

    private void ApplyUpdateCheckSchedule()
    {
        if (_settings.CheckForUpdatesOnStartup)
        {
            _updateCheckTimer.Start();
            ShellLog.Info($"update check schedule active interval={_updateCheckTimer.Interval}");
        }
        else
        {
            _startupUpdateCheckTimer.Stop();
            _updateCheckTimer.Stop();
            ShellLog.Info("update check schedule stopped");
        }
    }

    private void CheckForUpdatesFromSettings(Action<ClipUpdateStatus> updateStatus)
    {
        _ = CheckForUpdatesAsync(showToastWhenCurrent: true, updateStatus);
    }

    private async Task CheckForUpdatesAsync(bool showToastWhenCurrent, Action<ClipUpdateStatus>? updateStatus = null, bool promptIfAvailable = false, bool nativeNotify = false)
    {
        if (_updateCheckInProgress)
        {
            ShellLog.Info("update check skipped already running");
            return;
        }

        _updateCheckInProgress = true;
        try
        {
            if (nativeNotify)
            {
                UpdateNotification?.Invoke("Checking for updates…");
            }

            ShellLog.Info("update check started");
            var status = await _updates.CheckAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                _lastUpdateStatus = status;
                updateStatus?.Invoke(status);
                ShellLog.Info($"update check completed state={status.State} current={status.CurrentVersion} latest={status.LatestVersion ?? "none"} download={status.DownloadUrl ?? "none"}");

                if (status.State == "Update available")
                {
                    if (nativeNotify) { UpdateNotification?.Invoke(status.Message); } else { ShowToast(status.Message); }
                    if (promptIfAvailable)
                    {
                        PromptForKnownUpdate();
                    }
                }
                else if (showToastWhenCurrent)
                {
                    if (nativeNotify) { UpdateNotification?.Invoke(status.Message); } else { ShowToast(status.Message); }
                }
            });
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private void PromptForKnownUpdate()
    {
        if (!IsUpdateAvailable(_lastUpdateStatus))
        {
            return;
        }

        var version = _lastUpdateStatus.LatestVersion ?? "latest";
        if (string.Equals(_promptedUpdateVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Opacity == 0 || !IsHitTestVisible)
        {
            ShowPalette();
            return;
        }

        _promptedUpdateVersion = version;
        var result = System.Windows.MessageBox.Show(
            this,
            $"Clip {version} is available. Install it now?",
            "Update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            _ = InstallUpdateAsync(_lastUpdateStatus);
        }
    }

    private static bool IsUpdateAvailable(ClipUpdateStatus status) =>
        status.State == "Update available" && !string.IsNullOrWhiteSpace(status.DownloadUrl);

    private async Task InstallUpdateAsync(ClipUpdateStatus status)
    {
        try
        {
            var path = await _updates.DownloadUpdateAsync(status);
            if (path is null)
            {
                ShellLog.Info("update install skipped missing download asset");
                ShowToast("Update found, but no installer is attached");
                return;
            }

            ShellLog.Info($"update installer downloaded path={path}");
            var shouldExit = ClipUpdateService.LaunchInstaller(path, AppContext.BaseDirectory, Environment.ProcessId);
            ShowToast("Update installer opened");
            if (shouldExit)
            {
                System.Windows.Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "update install failed");
            ShowToast("Update install failed. Log saved.");
        }
    }

    private void ClearHistory(bool includePinned)
    {
        var removed = _store.ClearHistory(includePinned);
        if (_selected is not null && _store.GetItem(_selected.Id) is null)
        {
            _selected = null;
        }

        _allItems = _store.QueryItemSummaries();
        _historySummariesPreloaded = true;
        RenderItems(includePinned ? "clear-all-history" : "clear-unpinned-history");
        SelectItem(FilteredItems().FirstOrDefault(), includePinned ? "clear-all-history" : "clear-unpinned-history");
        ShellLog.Info($"history cleared includePinned={includePinned} removed={removed}");
        ShowToast(removed == 1 ? "1 item cleared" : $"{removed} items cleared");
    }

    /// <summary>
    /// Zips the clipboard folder — whatever it currently is, since it can be relocated — to the
    /// path the user picked. This is the only way out of Clip for a pinned item or a snippet:
    /// nothing else in the app can reproduce them once %LocalAppData%\Clip is gone.
    /// </summary>
    private void ExportHistory(string zipPath)
    {
        try
        {
            var exported = ClipboardHistoryBackup.Export(_store.ContentRootPath, zipPath);
            ShellLog.Info($"history exported items={exported} path={zipPath}");
            ShowToast(exported == 1 ? "Exported 1 item" : $"Exported {exported} items");
        }
        catch (InvalidOperationException ex)
        {
            // "Nothing saved yet" is a fact about the store, not a fault; say it plainly.
            ShellLog.Info($"history export skipped reason={ex.Message}");
            ShowToast("Nothing to export yet");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"history export failed path={zipPath}");
            ShowToast("Export failed. Log saved.");
        }
    }

    private void RestoreHistory(string zipPath)
    {
        try
        {
            var restored = ClipboardHistoryBackup.Restore(zipPath, _store.ContentRootPath);
            // The store still holds the pre-restore items in memory, and the next save would put
            // them straight back over the folder that was just replaced.
            _store.ReloadFromDisk();
            _selected = null;
            _allItems = _store.QueryItemSummaries();
            _historySummariesPreloaded = true;
            RenderItems("restore-history");
            SelectItem(FilteredItems().FirstOrDefault(), "restore-history");
            ShellLog.Info($"history restored items={restored} path={zipPath}");
            ShowToast(restored == 1 ? "Restored 1 item" : $"Restored {restored} items");
        }
        catch (InvalidDataException ex)
        {
            ShellLog.Info($"history restore refused path={zipPath} reason={ex.Message}");
            ShowToast("Not a Clip export");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"history restore failed path={zipPath}");
            ShowToast("Restore failed. Log saved.");
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.ContentRootPath);
            Process.Start(new ProcessStartInfo(_store.ContentRootPath) { UseShellExecute = true });
            ShellLog.Info($"clipboard folder opened path={_store.ContentRootPath}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"clipboard folder open failed path={_store.ContentRootPath}");
            ShowToast("Clipboard folder failed. Log saved.");
        }
    }

    private void OpenDebugLog()
    {
        try
        {
            WriteDebugSnapshot("settings-about");
            Process.Start(new ProcessStartInfo(ShellLog.Path) { UseShellExecute = true });
            ShellLog.Info($"debug log opened path={ShellLog.Path}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "debug log open failed");
            ShowToast("Debug log failed. Log saved.");
        }
    }

    private void ChangeClipboardFolder(string folderPath)
    {
        _settings.ClipboardFolderPath = folderPath;
        _settings.Save();
        _store.SetContentRootPath(_settings.EffectiveClipboardFolderPath());
        ResetHistorySummaryCache();
        ShellLog.Info($"clipboard folder changed path={_store.ContentRootPath}");
        ShowToast("Clipboard folder updated");
    }

    private void ResetClipboardFolder()
    {
        _settings.ClipboardFolderPath = null;
        _settings.Save();
        _store.SetContentRootPath(_settings.EffectiveClipboardFolderPath());
        ResetHistorySummaryCache();
        ShellLog.Info($"clipboard folder reset path={_store.ContentRootPath}");
        ShowToast("Clipboard folder reset");
    }

    private void ResetHistorySummaryCache()
    {
        ClearRecentFirstPaintPreload();
        _allItems = [];
        _selected = null;
        _historySummariesPreloaded = false;
        _itemsDirtySinceRender = true;
        _historyPreloadTimer.Stop();
    }

    private int EffectiveHistoryLimit()
    {
        return _settings.HistoryLimit is null ? int.MaxValue : Math.Max(0, _settings.HistoryLimit.Value);
    }

    private static string HistoryLimitLabel(int? limit) => limit is null ? "Unlimited" : limit.Value.ToString();

    private void ApplyHotkeys(ClipHotkeySettings hotkeys)
    {
        hotkeys.Normalize();
        _settings.Hotkeys = hotkeys;
        _settings.Save();
        ReRegisterHotkeys("settings");
        UpdateFooterHotkeyHints();
        ShellLog.Info($"hotkeys changed open={_settings.Hotkeys.OpenClip} debug={_settings.Hotkeys.SaveDebugLog}");
        ShowToast("Hotkeys updated");
    }

    /// <summary>
    /// The footer keycaps advertise the palette's hotkeys, so they must follow rebinding
    /// rather than hardcode the defaults ("Enter Paste" is a lie once Paste is Ctrl+Enter).
    /// </summary>
    private void UpdateFooterHotkeyHints()
    {
        // Only the two paste gestures are advertised. Copy, Actions, Pin and Shortcuts used to have
        // caps here too and were removed on request: they are the ones you either already know or
        // find in the Ctrl+Q list, and six hints made the bar read as a wall of chrome.
        SetFooterHint(PasteHintPanel, PasteHintKey, _settings.Hotkeys.PasteSelected);
        SetFooterHint(PasteStayHintPanel, PasteStayHintKey, PasteAndStayGesture(_settings.Hotkeys.PasteSelected) ?? string.Empty);
    }

    private static void SetFooterHint(StackPanel panel, TextBlock keycap, string gesture)
    {
        // An unbound hotkey has nothing to advertise; hide the hint rather than show an empty cap.
        panel.Visibility = string.IsNullOrWhiteSpace(gesture) ? Visibility.Collapsed : Visibility.Visible;
        keycap.Text = gesture;
    }

    private void ApplyPrivacy(ClipPrivacySettings privacy)
    {
        privacy.Normalize();
        _settings.Privacy = privacy;
        _settings.Save();
        ShellLog.Info($"privacy changed excludedApps={privacy.ExcludedApps.Count}");
        ShowToast("Privacy settings updated");
    }

    private void ApplySourceAppInList(bool enabled)
    {
        _settings.ShowSourceAppInList = enabled;
        _settings.Save();
        QueueLoadItems(selectFirst: false, "source-app-in-list-changed");
        ShowToast(enabled ? "Source app shown" : "Source app hidden");
    }

    private void ApplyExtractTextFromImages(bool enabled)
    {
        _settings.ExtractTextFromImages = enabled;
        _settings.Save();
        ShellLog.Info($"extract text from images set enabled={enabled} engineAvailable={OcrTextExtractor.IsAvailable}");

        if (!enabled)
        {
            ShowToast("Image text search off");
            return;
        }

        // Turning it on should make the history you already have searchable, not just future
        // copies, so backfill anything that has never been scanned.
        var queued = 0;
        foreach (var item in _store.QueryItemSummaries())
        {
            if (item.Kind == ClipboardItemKind.Image && item.OcrText is null)
            {
                QueueOcrIfEnabled(_store.GetItem(item.Id) ?? item);
                queued++;
            }
        }

        ShowToast(queued > 0 ? $"Reading text in {queued} images" : "Image text search on");
    }

    private void ApplyDefaultPasteFormat(PasteFormatPreference preference)
    {
        _settings.DefaultPasteFormat = preference;
        _settings.Save();
        ShellLog.Info($"default paste format changed format={preference}");
        ShowToast($"Paste format set to {PasteFormatLabel(preference)}");
    }

    private void ResetAllSettings()
    {
        _settings.ResetToDefaults();
        _settings.Save();
        ApplyRunAtStartup(StartupRegistration.DefaultEnabled);
        ApplyTheme(_settings.Theme, save: false);
        ApplyAppIcon(_settings.AppIcon, save: false);
        _store.SetContentRootPath(_settings.EffectiveClipboardFolderPath());
        ReRegisterHotkeys("settings-reset");
        UpdateFooterHotkeyHints();
        var removed = _store.ApplyHistoryLimit(EffectiveHistoryLimit());
        _allItems = _store.QueryItemSummaries();
        _historySummariesPreloaded = true;
        RenderItems("settings-reset");
        SelectItem(FilteredItems().FirstOrDefault(), "settings-reset");
        ShellLog.Info($"settings reset all removed={removed}");
        ShowToast("Settings reset to defaults");
    }

    private static string PasteFormatLabel(PasteFormatPreference preference)
    {
        return preference switch
        {
            PasteFormatPreference.OriginalFormatting => "Original formatting",
            _ => "Plain text",
        };
    }

    private void ReRegisterHotkeys(string reason)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_openHotkeyRegistered)
        {
            UnregisterHotKey(hwnd, OpenHotkeyId);
            _openHotkeyRegistered = false;
        }
        _openHotkeyUnavailable = false;
        // A rebind is a fresh start: if the new gesture also conflicts, that deserves its own balloon.
        _openHotkeyConflictNotified = false;

        if (_debugLogHotkeyRegistered)
        {
            UnregisterHotKey(hwnd, DebugLogHotkeyId);
            _debugLogHotkeyRegistered = false;
        }
        _debugLogHotkeyUnavailable = false;

        EnsureHotkeyRegistered(reason);
    }

    private void ApplyTheme(ClipThemePreference preference) => ApplyTheme(preference, save: true);

    private void ApplyTheme(ClipThemePreference preference, bool save)
    {
        ClearPrewarmedHostedSettings();

        // The generated preview pages bake the theme's colours in, so a page rendered under the old
        // theme must not be reused after a switch.
        _previewItemId = null;
        _previewSourceStamp = null;

        _settings.Theme = preference;
        var useDark = preference switch
        {
            ClipThemePreference.Light => false,
            ClipThemePreference.Dark => true,
            _ => IsWindowsDarkMode(),
        };

        // Backdrop first: SetBrush below alpha-blends the zone brushes only when the acrylic
        // actually took, so a failed apply degrades to the plain opaque palette instead of a
        // see-through window. It also needs the new Bg before Bg is set, hence the local.
        var bgHex = useDark ? "#1A1A1A" : "#F7F7F7";
        ApplyBackdropPreference(bgHex);

        SetBrush("Bg", bgHex);
        SetBrush("Surface", useDark ? "#212121" : "#FFFFFF");
        SetBrush("Surface2", useDark ? "#272727" : "#EDEDED");
        SetBrush("Surface3", useDark ? "#323232" : "#DCDCDC");
        SetBrush("Line", useDark ? "#494949" : "#B8B8B8");
        SetBrush("Line2", useDark ? "#5A5A5A" : "#989898");
        SetBrush("Text", useDark ? "#F1F1F1" : "#1A1A1A");
        // Muted carries the 11-12px labels (INFORMATION, the footer captions, the search
        // placeholder) and it is the first thing to go thin once there is a real blurred desktop
        // under it instead of a flat sheet. Raised here rather than by thickening the glass:
        // Muted is opaque and independent of the tint, so this buys legibility without buying back
        // the opacity the whole change exists to remove.
        SetBrush("Muted", useDark ? "#A3A3A3" : "#585858");
        SetBrush("Muted2", useDark ? "#BBBBBB" : "#474747");
        SetBrush("Muted3", useDark ? "#777777" : "#6A6A6A");
        // Raycast palette: fixed brand red (#FF6363) used sparingly, never the Windows accent.
        SetBrush("Accent", useDark ? "#FF6363" : "#D64545");
        SetBrush("TextCursor", useDark ? "#FF6363" : "#D64545");
        // Selection/hover chrome is deliberately neutral (no accent tint): accent-colored fills and
        // 1px accent borders everywhere read as a "glow". The accent survives only in Accent
        // (focus ring, toggles), TextCursor, and TextSelection.
        SetBrush("AccentSoft", useDark ? "#2D2D2D" : "#E7E7E7");
        SetBrush("Selected", useDark ? "#373737" : "#D9D9D9");
        SetBrush("SelectedBorder", useDark ? "#525252" : "#ACACAC");
        SetBrush("TextSelection", useDark ? "#FF6363" : "#D64545");
        SetBrush("Danger", useDark ? "#D56B5D" : "#B94A3D");
        // The window itself never paints: it is layered, so an opaque window background would
        // square off the Shell's rounded corners whether the glass is on or not. The Shell is the
        // silhouette. With the glass on it paints nothing at all — the acrylic tint IS the sheet
        // now, and a second Bg sheet on top of it is exactly what made the old look read as solid.
        Shell.Background = _backdropActive ? WpfBrushes.Transparent : (WpfBrush)FindResource("Bg");
        _setHtmlPreviewBackground?.Invoke(ToDrawingColor((SolidColorBrush)FindResource("Surface")));

        // Browser-backed previews bake theme colors into their generated pages, so a theme
        // change has to rebuild the visible preview rather than leave it in the old palette.
        if (_selected is not null && IsVisible)
        {
            RenderPreview(_selected);
        }

        TextPreview.Foreground = (WpfBrush)FindResource("Text");
        TextPreview.CaretBrush = (WpfBrush)FindResource("TextCursor");
        // No focus ring on search (Raycast has none): the palette autofocuses its only text
        // field on open, so a ring announces nothing.
        if (SearchShell is not null)
        {
            SearchShell.BorderBrush = (WpfBrush)FindResource("Line2");
        }

        // The action menus live in Popups, which are separate HWNDs floating over the palette: no
        // acrylic under them, and whatever they cover is list rows and preview text. At the zone
        // alpha you would read that text straight through the menu, so menus opt out of the glass.
        var menuSurface = PaletteBackdrop.Opaque((WpfBrush)FindResource("Surface"));
        if (ActionMenuBorder is not null) { ActionMenuBorder.Background = menuSurface; }
        if (ShareSubmenuBorder is not null) { ShareSubmenuBorder.Background = menuSurface; }
        if (TitleText is not null) { TitleText.Foreground = (WpfBrush)FindResource("Text"); }
        if (SubTitleText is not null) { SubTitleText.Foreground = (WpfBrush)FindResource("Muted"); }
        if (save)
        {
            Dispatcher.BeginInvoke(RefreshChromeIconsIfReady, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        else if (_chromeIconsReady)
        {
            RefreshChromeIcons();
        }

        ShellLog.Info($"theme applied preference={preference} dark={useDark}");

        if (save)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _settings.Save();
                if (_selected is not null)
                {
                    RenderInfo(_selected);
                    RenderPreview(_selected);
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    private void ApplyWindowTitleIcon(AppIconPreference preference)
    {
        // The palette has no title bar, so the chosen icon lands on the window itself
        // (alt-tab and taskbar) rather than on an in-window header.
        Icon = RenderAppTileIcon(preference);
        _appHeaderIconReady = true;
        ShellLog.Info($"window icon applied icon={preference}");
    }

    private void EnsureAppHeaderIcon()
    {
        if (_appHeaderIconReady)
        {
            return;
        }

        ApplyWindowTitleIcon(_settings.AppIcon);
    }

    private void RefreshChromeIcons()
    {
        SearchGlyphIcon.Source = RenderChromeIcon(ChromeIconKind.Search, "Muted");
        SettingsIcon.Source = RenderChromeIcon(ChromeIconKind.Settings, "Muted2");
        NewSnippetIcon.Source = RenderChromeIcon(ChromeIconKind.Plus, "Muted2");
        DateDropIcon.Source = RenderChromeIcon(ChromeIconKind.ChevronDown, _kindFilter == "all" ? "Text" : "Muted2");
        FileDropIcon.Source = RenderChromeIcon(ChromeIconKind.ChevronDown, _kindFilter == "files" ? "Text" : "Muted2");
        MediaDropIcon.Source = RenderChromeIcon(ChromeIconKind.ChevronDown, IsMediaFilter(_kindFilter) ? "Text" : "Muted2");
        ExpandImageIcon.Source = RenderChromeIcon(ChromeIconKind.Expand, "Muted2");
        _chromeIconsReady = true;
    }

    private void EnsureChromeIcons()
    {
        if (_chromeIconsReady)
        {
            return;
        }

        RefreshChromeIcons();
    }

    private void RefreshChromeIconsIfReady()
    {
        if (_chromeIconsReady)
        {
            RefreshChromeIcons();
        }
    }

    private enum ChromeIconKind
    {
        Settings,
        ChevronDown,
        Expand,
        Search,
        Plus,
    }

    private enum ItemVectorIconKind
    {
        Text,
        Link,
        Email,
        Folder,
        Image,
        File,
    }

    private ImageSource RenderChromeIcon(ChromeIconKind kind, string colorKey)
    {
        var color = ((SolidColorBrush)FindResource(colorKey)).Color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new WpfPen(brush, kind == ChromeIconKind.Settings ? 1.8 : 2.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(WpfBrushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));

        switch (kind)
        {
            case ChromeIconKind.Settings:
                drawing.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new System.Windows.Point(12, 12), 4.2, 4.2)));
                for (var i = 0; i < 8; i++)
                {
                    var angle = (Math.PI / 4) * i;
                    drawing.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(
                        new System.Windows.Point(12 + Math.Cos(angle) * 7.1, 12 + Math.Sin(angle) * 7.1),
                        new System.Windows.Point(12 + Math.Cos(angle) * 9.6, 12 + Math.Sin(angle) * 9.6))));
                }
                break;

            case ChromeIconKind.ChevronDown:
                drawing.Children.Add(new GeometryDrawing(null, pen, PolylineGeometry(
                    new System.Windows.Point(6.5, 9),
                    new System.Windows.Point(12, 14.5),
                    new System.Windows.Point(17.5, 9))));
                break;

            case ChromeIconKind.Search:
                // Lens sized so the icon reads at the 14px it is shown at, matching the weight
                // of the '⌕' glyph it replaced.
                drawing.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new System.Windows.Point(10.5, 10.5), 5.8, 5.8)));
                AddLine(drawing, pen, 14.9, 14.9, 19.5, 19.5);
                break;

            case ChromeIconKind.Plus:
                AddLine(drawing, pen, 12, 6, 12, 18);
                AddLine(drawing, pen, 6, 12, 18, 12);
                break;

            case ChromeIconKind.Expand:
                AddLine(drawing, pen, 5, 5, 10, 5);
                AddLine(drawing, pen, 5, 5, 5, 10);
                AddLine(drawing, pen, 5, 5, 10, 10);
                AddLine(drawing, pen, 19, 5, 14, 5);
                AddLine(drawing, pen, 19, 5, 19, 10);
                AddLine(drawing, pen, 19, 5, 14, 10);
                AddLine(drawing, pen, 5, 19, 10, 19);
                AddLine(drawing, pen, 5, 19, 5, 14);
                AddLine(drawing, pen, 5, 19, 10, 14);
                AddLine(drawing, pen, 19, 19, 14, 19);
                AddLine(drawing, pen, 19, 19, 19, 14);
                AddLine(drawing, pen, 19, 19, 14, 14);
                break;
        }

        drawing.Freeze();
        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    internal static ImageSource RenderAppTileIcon(AppIconPreference preference)
    {
        var cacheKey = $"app-tile|{preference}";
        lock (SvgCacheGate)
        {
            if (SvgImageCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var dark = preference == AppIconPreference.Dark;
        var background = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0x21, 0x1F, 0x1C)
            : System.Windows.Media.Color.FromRgb(0xF4, 0xF0, 0xE6));
        var strokeBrush = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0xF4, 0xF0, 0xE6)
            : System.Windows.Media.Color.FromRgb(0x1A, 0x18, 0x16));
        background.Freeze();
        strokeBrush.Freeze();

        var pen = new WpfPen(strokeBrush, 5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(background, null, new RectangleGeometry(new Rect(0, 0, 72, 72), 16.2, 16.2)));
        drawing.Children.Add(new GeometryDrawing(null, pen, PaperclipIconGeometry()));
        drawing.Freeze();

        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();

        lock (SvgCacheGate)
        {
            SvgImageCache[cacheKey] = image;
        }

        return image;
    }

    private static StreamGeometry PaperclipIconGeometry()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new System.Windows.Point(48, 16), isFilled: false, isClosed: false);
            context.LineTo(new System.Windows.Point(48, 50), isStroked: true, isSmoothJoin: true);
            context.ArcTo(new System.Windows.Point(32, 50), new System.Windows.Size(8, 8), rotationAngle: 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            context.LineTo(new System.Windows.Point(32, 24), isStroked: true, isSmoothJoin: true);
            context.ArcTo(new System.Windows.Point(40, 24), new System.Windows.Size(4, 4), rotationAngle: 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            context.LineTo(new System.Windows.Point(40, 46), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private ImageSource RenderItemVectorIcon(ItemVectorIconKind kind, int size)
    {
        var color = BrushHex("Muted2");
        var cacheKey = $"item-vector|{kind}|{size}|{color}";
        lock (SvgCacheGate)
        {
            if (SvgImageCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var stroke = new SolidColorBrush(((SolidColorBrush)FindResource("Muted2")).Color);
        stroke.Freeze();
        var fill = new SolidColorBrush(((SolidColorBrush)FindResource("Muted2")).Color) { Opacity = 0.16 };
        fill.Freeze();
        var pen = new WpfPen(stroke, 1.8)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(WpfBrushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));

        switch (kind)
        {
            case ItemVectorIconKind.Text:
                AddDocumentOutline(drawing, pen, fill);
                AddLine(drawing, pen, 8.5, 12, 16.5, 12);
                AddLine(drawing, pen, 8.5, 15, 17, 15);
                AddLine(drawing, pen, 8.5, 18, 14.5, 18);
                break;

            case ItemVectorIconKind.Link:
                drawing.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(3.8, 8.3, 9.4, 7.4), 3.7, 3.7)));
                drawing.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(10.8, 8.3, 9.4, 7.4), 3.7, 3.7)));
                AddLine(drawing, pen, 9.2, 12, 14.8, 12);
                break;

            case ItemVectorIconKind.Email:
                // Stroked @ symbol on the same 24x24 grid the other item icons use.
                drawing.Children.Add(new GeometryDrawing(
                    null,
                    pen,
                    Geometry.Parse("M16 20.064A9 9 0 1 1 21 12v1.5a2.5 2.5 0 0 1-5 0V8m0 4a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z")));
                break;

            case ItemVectorIconKind.Folder:
                drawing.Children.Add(new GeometryDrawing(fill, pen, PolygonGeometry(
                    new System.Windows.Point(3.5, 7.5),
                    new System.Windows.Point(8.8, 7.5),
                    new System.Windows.Point(11, 9.7),
                    new System.Windows.Point(20.5, 9.7),
                    new System.Windows.Point(20.5, 18.5),
                    new System.Windows.Point(3.5, 18.5))));
                break;

            case ItemVectorIconKind.Image:
                drawing.Children.Add(new GeometryDrawing(fill, pen, new RectangleGeometry(new Rect(4.5, 5.5, 15, 13), 2.8, 2.8)));
                drawing.Children.Add(new GeometryDrawing(stroke, null, new EllipseGeometry(new System.Windows.Point(14.8, 9.2), 1.25, 1.25)));
                drawing.Children.Add(new GeometryDrawing(null, pen, PolylineGeometry(
                    new System.Windows.Point(6.8, 16),
                    new System.Windows.Point(10.2, 12.6),
                    new System.Windows.Point(12.7, 15.1),
                    new System.Windows.Point(14.2, 13.6),
                    new System.Windows.Point(17.4, 16.8))));
                break;

            case ItemVectorIconKind.File:
                AddDocumentOutline(drawing, pen, fill);
                AddLine(drawing, pen, 8.5, 14, 16, 14);
                AddLine(drawing, pen, 8.5, 17, 14, 17);
                break;
        }

        drawing.Freeze();
        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();

        lock (SvgCacheGate)
        {
            SvgImageCache[cacheKey] = image;
        }

        return image;
    }

    private static void AddDocumentOutline(DrawingGroup drawing, WpfPen pen, WpfBrush fill)
    {
        drawing.Children.Add(new GeometryDrawing(fill, pen, PolygonGeometry(
            new System.Windows.Point(6.5, 3.8),
            new System.Windows.Point(14.7, 3.8),
            new System.Windows.Point(18.5, 7.6),
            new System.Windows.Point(18.5, 20.2),
            new System.Windows.Point(6.5, 20.2))));
        AddLine(drawing, pen, 14.7, 3.8, 14.7, 8);
        AddLine(drawing, pen, 14.7, 8, 18.5, 8);
    }

    private static void AddLine(DrawingGroup drawing, WpfPen pen, double x1, double y1, double x2, double y2)
    {
        drawing.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new System.Windows.Point(x1, y1), new System.Windows.Point(x2, y2))));
    }

    private static StreamGeometry PolylineGeometry(params System.Windows.Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i < points.Length; i++)
            {
                context.LineTo(points[i], isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry PolygonGeometry(params System.Windows.Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            for (var i = 1; i < points.Length; i++)
            {
                context.LineTo(points[i], isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void SetBrush(string key, string hex)
    {
        // Single choke point for the glass look: every themed brush passes through here, so the
        // translucent variant is one transform instead of a second palette.
        if (_backdropActive)
        {
            hex = PaletteBackdrop.GlassHex(key, hex);
        }

        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        if (Resources[key] is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                Resources[key] = new SolidColorBrush(color);
                return;
            }

            brush.Color = color;
        }
    }

    private string BrushHex(string key)
    {
        if (Resources[key] is SolidColorBrush brush)
        {
            return $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
        }

        return "#F2EFE9";
    }

    internal static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }

    internal static bool IsLightBackground(WpfBrush brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return false;
        }

        var color = solid.Color;
        var brightness = (color.R * 0.299) + (color.G * 0.587) + (color.B * 0.114);
        return brightness > 150;
    }

    // Flattened to opaque because both callers feed WebView2's DefaultBackgroundColor, which
    // accepts only fully transparent or fully opaque — the acrylic theme's semi-transparent
    // Surface (alpha 0xCC) made the setter throw ArgumentException, which faulted the WebView2
    // init and then crashed every palette open that touched CoreWebView2. The pages the pane
    // loads paint their own opaque Surface anyway, so nothing is lost visually.
    private static System.Drawing.Color ToDrawingColor(SolidColorBrush brush) =>
        System.Drawing.Color.FromArgb(255, brush.Color.R, brush.Color.G, brush.Color.B);

    internal static ClipThemePreference NextThemeTogglePreference(ClipThemePreference current, bool systemIsDark)
    {
        var currentlyDark = current switch
        {
            ClipThemePreference.Dark => true,
            ClipThemePreference.Light => false,
            _ => systemIsDark,
        };

        return currentlyDark ? ClipThemePreference.Light : ClipThemePreference.Dark;
    }

    private static void UpdateInstalledShortcutIcons(string iconPath)
    {
        try
        {
            var shortcuts = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Clip.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Clip.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Clip", "Clip.lnk"),
            };

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (var shortcutPath in shortcuts.Where(File.Exists))
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.IconLocation = iconPath;
                shortcut.Save();
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "shortcut icon update failed");
        }
    }

    private void OnListWheel(object sender, MouseWheelEventArgs e)
    {
        ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset - e.Delta);
        AppendDeferredRowsIfNeeded();
        e.Handled = true;
    }

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The + sits inline with the TODAY header, floating over the scroller. At the top there is
        // nothing under it; scrolled, rows would slide beneath it, so it gets out of the way.
        NewSnippetButton.Visibility = ListScroll.VerticalOffset > 4 ? Visibility.Hidden : Visibility.Visible;

        // Never append straight from the layout pass that raised this: appending re-lays out, which
        // raises it again, and the list renders itself in one cascade nothing else can interrupt.
        QueueDeferredAppend();
    }

    private void OnDateDropClick(object sender, RoutedEventArgs e)
    {
        var actions = new[] { ("All", "all"), ("Today", "today"), ("Yesterday", "yesterday"), ("This week", "week"), ("This month", "month"), ("This year", "year"), ("Older", "older") }
            .Select(pair => new MenuAction(pair.Item1, () =>
            {
                _dateFilter = pair.Item2;
                var visibleItems = RenderItems("date-filter");
                SelectItem(visibleItems.FirstOrDefault(), "date-filter");
            }));
        ShowStyledMenu(actions, AllFilterShell);
    }

    private void OnMediaDropClick(object sender, RoutedEventArgs e)
    {
        var actions = new[]
        {
            new MenuAction("All media", () => SetFilter("images")),
            new MenuAction("Images", () => SetFilter("media-images")),
            new MenuAction("Videos", () => SetFilter("media-videos")),
            new MenuAction("Audio", () => SetFilter("media-audio")),
        };
        ShowStyledMenu(actions, MediaFilterShell);
    }

    private void OnFileDropClick(object sender, RoutedEventArgs e)
    {
        var keys = _allItems.SelectMany(i => i.FilePaths).Select(FileKindKey).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new[] { "all", "folder", "pdf", "excel", "visio", "html", "image", "text", "word", "powerpoint" }
            .Concat(keys.OrderBy(k => k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => k == "all" || keys.Contains(k));
        var actions = ordered.Select(key => new MenuAction(LabelForFileKey(key), () =>
        {
            _kindFilter = "files";
            _fileFilter = key;
            var visibleItems = RenderItems("file-filter");
            SelectItem(visibleItems.FirstOrDefault(), "file-filter");
        }));
        ShowStyledMenu(actions, FilesFilterShell);
    }

    /// <summary>Where a press on the top or bottom bar started, while it is still undecided.</summary>
    private System.Windows.Point? _chromeDragOrigin;

    /// <summary>
    /// Arms a possible window drag from the top or bottom bar. Deliberately does NOT handle the
    /// event: this is the tunnel pass, so the press still reaches whatever was under it and a
    /// filter chip or a footer button behaves exactly as before. Only once the pointer travels past
    /// the system drag threshold does <see cref="OnChromeMouseMove"/> convert it into a window
    /// drag, which is the same click-versus-drag rule a real title bar uses — so the bars are
    /// draggable everywhere without any button losing its click.
    /// </summary>
    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _chromeDragOrigin = null;
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var armed = ShouldArmChromeDrag(
            e.GetPosition(RootGrid).Y,
            SearchRowDef.ActualHeight,
            RootGrid.ActualHeight,
            FooterRowDef.ActualHeight,
            IsWithin(e.OriginalSource as DependencyObject, SearchShell),
            SearchBox.Text.Length);

        if (armed)
        {
            _chromeDragOrigin = e.GetPosition(this);
        }
    }

    /// <summary>
    /// Whether a press should be allowed to become a window drag. The top and bottom bars are grab
    /// handles and everything between them is content; the one carve-out is the search field, where
    /// a press is a text selection whenever there is text to select. With the field empty there is
    /// nothing to select, so it behaves like the rest of the bar.
    /// </summary>
    internal static bool ShouldArmChromeDrag(
        double y,
        double headerHeight,
        double rootHeight,
        double footerHeight,
        bool overSearchField,
        int searchTextLength)
    {
        if (overSearchField && searchTextLength > 0)
        {
            return false;
        }

        if (rootHeight <= 0)
        {
            // Before the first layout every ActualHeight is 0, and "below rootHeight - footerHeight"
            // would then be true everywhere — the whole window would read as a grab handle.
            return y <= headerHeight;
        }

        return y <= headerHeight || y >= rootHeight - footerHeight;
    }

    private void OnChromeMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_chromeDragOrigin is not { } origin)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _chromeDragOrigin = null;
            return;
        }

        var moved = e.GetPosition(this) - origin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _chromeDragOrigin = null;
        BeginWindowDrag();
    }

    private void OnChromeMouseUp(object sender, MouseButtonEventArgs e) => _chromeDragOrigin = null;

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Hands the drag to the OS (WM_NCLBUTTONDOWN with HTCAPTION) instead of calling DragMove().
    /// DragMove throws if the button is already up by the time it runs — a real race on a flick of
    /// the wrist — and this window is ShowActivated=False and topmost, which is the shape DragMove
    /// is least reliable on. The non-client loop is what a title bar uses, so snapping and the
    /// modifier behaviours come for free.
    /// </summary>
    private void BeginWindowDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Two different captures to drop. WPF's, so the button the drag started on does not
            // stay stuck in its pressed visual and never fires a Click on the button-up it will
            // now never see; and the OS's, without which the non-client loop refuses to start.
            Mouse.Capture(null);
            _ = ReleaseCapture();
            _ = SendMessage(hwnd, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "palette drag failed");
        }
    }

    /// <summary>
    /// Keys that must act on the list while the search box keeps focus. They are handled on the
    /// tunnel pass because the TextBox consumes arrows, Delete and Ctrl+Z on the bubble pass, so
    /// OnWindowKeyDown never sees them. Everything consumed here sets Handled, which is what
    /// keeps an arrow from also moving the caret.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ActionMenuPopup.IsOpen && HandleActionMenuKey(e))
        {
            return;
        }

        // Everything below acts on the list, and only while the search box owns the keyboard.
        // When focus is anywhere else — the inline edit overlay, a rename box, the text preview —
        // these keys belong to that control.
        if (!SearchBox.IsKeyboardFocused)
        {
            return;
        }

        // Ctrl+1..9 pastes the Nth row without arrowing to it first.
        if (Keyboard.Modifiers == ModifierKeys.Control && DigitFromKey(e.Key) is int digit)
        {
            if (PaletteSelection.DigitPick(VisibleOrder(LastRenderedVisibleItems()), digit) is { } picked)
            {
                SelectItem(picked, reason: "digit-paste");
                PasteSelected();
                ShellLog.Info($"digit paste index={digit}");
            }

            e.Handled = true;
            return;
        }

        // Palette-level undo outranks the text box's own undo only while there is actually a
        // deleted item to bring back; otherwise Ctrl+Z keeps meaning "undo my typing".
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && _deleteUndo.HasItem)
        {
            RestoreDeletedItem();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        // Delete only acts on the list when the search box has nothing for it to edit; with a
        // query typed, the key keeps its forward-delete meaning.
        if (e.Key == Key.Delete && string.IsNullOrEmpty(SearchBox.Text) &&
            MatchesHotkey(e, _settings.Hotkeys.DeleteSelected))
        {
            if (_selected is not null)
            {
                DeleteItem(_selected);
            }

            e.Handled = true;
            return;
        }

        // Home/End likewise belong to the caret while there is text to edit.
        if (e.Key is Key.Home or Key.End && !string.IsNullOrEmpty(SearchBox.Text))
        {
            return;
        }

        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            MoveSelection(e.Key);
            e.Handled = true;
        }
    }

    private static int? DigitFromKey(Key key)
    {
        if (key >= Key.D1 && key <= Key.D9)
        {
            return key - Key.D0;
        }

        return key >= Key.NumPad1 && key <= Key.NumPad9 ? key - Key.NumPad0 : null;
    }

    private void MoveSelection(Key key)
    {
        var order = VisibleOrder(LastRenderedVisibleItems());
        if (order.Count == 0)
        {
            return;
        }

        var delta = key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.PageUp => -PaletteSelection.PageStep,
            Key.PageDown => PaletteSelection.PageStep,
            Key.Home => -order.Count,
            _ => order.Count,
        };

        var next = PaletteSelection.Step(order, _selected?.Id, delta);
        if (next is null || next.Id == _selected?.Id)
        {
            return;
        }

        SelectItem(next, reason: "keyboard");
        ScrollRowIntoView(next.Id);
    }

    private void ScrollRowIntoView(string id)
    {
        // End (and a long PageDown) can land on an item whose row is still in the deferred
        // batches; pull the remaining batches in before asking WPF to scroll to it. Each forced
        // append either advances the index or clears the entries, so this terminates.
        while (!_rows.ContainsKey(id) && _deferredRenderIndex < _deferredRenderEntries.Count)
        {
            AppendDeferredRowsIfNeeded(force: true);
        }

        if (_rows.TryGetValue(id, out var row))
        {
            row.BringIntoView();
        }
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (MatchesHotkey(e, _settings.Hotkeys.SaveDebugLog))
        {
            WriteDebugSnapshot("keyboard");
            e.Handled = true;
        }
        else if (MatchesHotkey(e, "Ctrl+Shift+V"))
        {
            PasteSelected(PasteFormatPreference.PlainText);
            e.Handled = true;
        }
        else if (PasteAndStayGesture(_settings.Hotkeys.PasteSelected) is { } pasteAndStay && MatchesHotkey(e, pasteAndStay))
        {
            PasteSelected(null, keepPaletteOpen: true);
            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.PasteSelected))
        {
            PasteSelected();
            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.CopySelected))
        {
            CopySelected();
            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.PinSelected))
        {
            if (_selected is not null)
            {
                TogglePin(_selected);
                ShellLog.Info($"hotkey pin id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.OpenActions))
        {
            if (_selected is not null)
            {
                // Opened from the keyboard, the menu belongs against the selected row — the
                // mouse could be anywhere on screen, and MousePoint would follow it there.
                // Highlighting the first row means Enter works immediately.
                ScrollRowIntoView(_selected.Id);
                ShowActionMenu(_selected, _rows.GetValueOrDefault(_selected.Id));
                HighlightMenuRow(0);
                ShellLog.Info($"hotkey actions id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowHotkeyHelp();
            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.OpenSelected))
        {
            if (_selected is not null && _selected.Kind is (ClipboardItemKind.Link or ClipboardItemKind.Files or ClipboardItemKind.Image))
            {
                OpenItem(_selected);
                ShellLog.Info($"hotkey open id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.EditSelected))
        {
            if (_selected is not null && _selected.Kind is (ClipboardItemKind.Text or ClipboardItemKind.Link))
            {
                EditText(_selected);
                ShellLog.Info($"hotkey edit id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.DeleteSelected))
        {
            if (_selected is not null)
            {
                DeleteItem(_selected);
            }

            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.CloseClip))
        {
            if (ExpandedImageOverlay.Visibility == Visibility.Visible)
            {
                CloseExpandedImage();
                e.Handled = true;
                return;
            }

            if (ActionMenuPopup.IsOpen)
            {
                ActionMenuPopup.IsOpen = false;
                e.Handled = true;
                return;
            }

            ConcealPalette("escape");
            e.Handled = true;
        }
    }

    /// <summary>
    /// The gesture for "paste but leave the palette up": the paste hotkey with Shift added, so
    /// Enter pastes and Shift+Enter pastes and stays. Deriving it beats adding a tenth setting —
    /// it follows a rebind for free (paste on Ctrl+Enter makes this Ctrl+Shift+Enter) and there is
    /// no new binding for the user to collide with the others.
    ///
    /// Null when paste is unbound, or already uses Shift: there is no Shift left to add, and
    /// silently binding something else would be worse than not offering it.
    /// </summary>
    internal static string? PasteAndStayGesture(string? pasteGesture)
    {
        if (!ClipHotkeyGesture.TryParse(pasteGesture, out var gesture) ||
            gesture.WpfModifiers.HasFlag(ModifierKeys.Shift))
        {
            return null;
        }

        return ClipHotkeyGesture.Format(gesture.WpfModifiers | ModifierKeys.Shift, gesture.WpfKey);
    }

    private static bool MatchesHotkey(System.Windows.Input.KeyEventArgs e, string configured)
    {
        return ClipHotkeyGesture.TryParse(configured, out var gesture)
            && e.Key == gesture.WpfKey
            && Keyboard.Modifiers == gesture.WpfModifiers;
    }

    private void ShowHotkeyHelp()
    {
        try
        {
            ShellLog.Info("hotkey help opening");
            var help = new HotkeyHelpWindow(
                _settings.Hotkeys,
                (WpfBrush)FindResource("Bg"),
                (WpfBrush)FindResource("Surface"),
                (WpfBrush)FindResource("Surface2"),
                (WpfBrush)FindResource("Text"),
                (WpfBrush)FindResource("Muted"),
                (WpfBrush)FindResource("Line"),
                (WpfBrush)FindResource("AccentSoft"),
                (WpfBrush)FindResource("SelectedBorder"))
            {
                Owner = this,
            };
            _suppressDeactivate = true;
            help.Closed += (_, _) =>
            {
                _suppressDeactivate = false;
                ShellLog.Info("hotkey help closed");
                ShowPalette();
            };
            help.Show();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "hotkey help failed");
            _suppressDeactivate = false;
            ShowToast("Hotkey help failed. Log saved.");
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (ExpandedImageOverlay.Visibility == Visibility.Visible)
        {
            CloseExpandedImage();
            Activate();
            ShellLog.Info("image expanded closed on deactivate");
            return;
        }

        if (KeepOpenForDebug || _suppressDeactivate || ActionMenuPopup.IsOpen || IsContextMenuOpen(this))
        {
            ShellLog.Info($"deactivate suppressed debug={KeepOpenForDebug}");
            return;
        }

        // Do NOT auto-hide on focus loss. In remote/RDP/RustDesk sessions the palette frequently
        // can't hold foreground, so concealing here made it flash open and vanish (looked like
        // "Alt+V doesn't work"). The palette still dismisses via Escape, a click outside it
        // (HideIfMousePressedOutsidePalette), or after a paste.
        ShellLog.Info("deactivate ignored (palette stays until escape/outside-click/paste)");
    }

    private static bool IsContextMenuOpen(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ContextMenu { IsOpen: true })
            {
                return true;
            }

            if (IsContextMenuOpen(child))
            {
                return true;
            }
        }

        return false;
    }

    private void ShowToast(string message) => ShowToast(message, null);

    /// <summary>
    /// 2.4 seconds suits "Copied" and is not enough for a sentence that asks the user to do
    /// something, so a message that explains itself can ask for longer.
    /// </summary>
    private void ShowToast(string message, TimeSpan? duration)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = duration ?? DefaultToastDuration;
        _toastTimer.Start();
    }

    private void UpdateFilterVisuals()
    {
        SetFilterVisual(AllButton, AllFilterShell, _kindFilter == "all");
        SetFilterVisual(TextButton, null, _kindFilter == "text");
        SetFilterVisual(ImageButton, MediaFilterShell, IsMediaFilter(_kindFilter));
        SetFilterVisual(LinksButton, null, _kindFilter == "links");
        SetFilterVisual(ColorButton, null, _kindFilter == "colors");
        SetFilterVisual(FilesButton, FilesFilterShell, _kindFilter == "files");
        // The split-pill shell paints the selected fill; the halves stay transparent so the pill
        // always reads as one control (OriginUI-style button-with-dropdown).
        DateDropButton.Foreground = _kindFilter == "all" ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        FileDropButton.Foreground = _kindFilter == "files" ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        MediaDropButton.Foreground = IsMediaFilter(_kindFilter) ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        RefreshChromeIconsIfReady();
    }

    private static bool IsMediaFilter(string filter) =>
        filter is "images" or "media-images" or "media-videos" or "media-audio";

    private void SetFilterVisual(WpfButton button, Border? shell, bool selected)
    {
        // Selection is a brighter outline and brighter ink -- contrast, not colour and not a fill.
        // The grey block this started as sat heavily in the bar and filled the chevron half of the
        // split pills; the accent outline that replaced it put a saturated red in a row that is
        // otherwise all greys. Lifting the same neutral the labels use says "this one is on"
        // without introducing either.
        var selectedOutline = (WpfBrush)FindResource("Muted2");
        button.Foreground = selected ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        if (shell is not null)
        {
            // Split pill: the shell carries the whole selected look so the button half and the
            // chevron half can never drift apart visually. Unselected pills keep a neutral
            // outline so the button+dropdown always reads as one bounded control.
            shell.Background = WpfBrushes.Transparent;
            shell.BorderBrush = selected ? selectedOutline : (WpfBrush)FindResource("Line2");
            button.Background = WpfBrushes.Transparent;
            button.BorderBrush = WpfBrushes.Transparent;
        }
        else
        {
            button.Background = WpfBrushes.Transparent;
            button.BorderBrush = selected ? selectedOutline : WpfBrushes.Transparent;
        }
    }

    private static string TitleFor(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CustomTitle))
        {
            return item.CustomTitle;
        }

        if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1)
        {
            return Path.GetFileName(item.FilePaths[0]);
        }

        return item.Preview;
    }

    private static string SubtitleFor(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Files)
        {
            if (item.FilePaths.Count == 1)
            {
                var path = item.FilePaths[0];
                if (Directory.Exists(path))
                {
                    return "Folder";
                }

                var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                return string.IsNullOrWhiteSpace(ext) ? "File" : $"{ext} file";
            }

            return $"{item.FilePaths.Count} files";
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            return item.ImageWidth is not null && item.ImageHeight is not null ? "Screenshot" : "Image";
        }

        return DisplaySourceName(item.SourceApplication) is { Length: > 0 } source && source != "Unknown"
            ? source
            : item.Kind.ToString();
    }

    private static string HeaderSubtitleFor(ClipboardHistoryItem item)
    {
        var source = SourceDisplayName(item);
        return $"Copied from {source} · {MetaFor(item)}";
    }

    private static string SourceDisplayName(ClipboardHistoryItem item)
    {
        return DisplaySourceName(item.SourceApplication);
    }

    private static string DisplaySourceName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Unknown";
        }

        return source.ToLowerInvariant() switch
        {
            "olk" or "outlook" => "Outlook",
            "code" => "VS Code",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "explorer" => "File Explorer",
            "windowsterminal" => "Windows Terminal",
            "wt" => "Windows Terminal",
            "powershell" => "PowerShell",
            "pwsh" => "PowerShell",
            "cmd" => "Command Prompt",
            "winword" => "Word",
            "excel" => "Excel",
            "powerpnt" => "PowerPoint",
            "onenote" => "OneNote",
            "teams" => "Teams",
            "slack" => "Slack",
            "discord" => "Discord",
            "spotify" => "Spotify",
            "notion" => "Notion",
            "obsidian" => "Obsidian",
            _ => source,
        };
    }

    private static string MetaFor(ClipboardHistoryItem item)
    {
        var copied = item.LastCopiedAt.LocalDateTime;
        var today = DateTime.Today;
        if (copied.Date == today)
        {
            return copied.ToString("h:mm tt");
        }

        if (copied.Date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        if (copied >= today.AddDays(-7))
        {
            return copied.ToString("ddd");
        }

        return copied.ToString("M/d/yy");
    }

    /// <summary>
    /// The whole text, not the row summary.
    ///
    /// Long items are stored in an asset file with only a truncated <c>Text</c> on the row, and
    /// list loads do not hydrate it — so <see cref="TextPayload"/> fell through to
    /// <c>item.Preview</c>, which is one line capped at 120 characters with a literal "..." on the
    /// end. That is what the preview pane was showing, ellipsis and all, under a pane with room
    /// for far more. The pane's TextBox already wraps and scrolls; it was only ever given the
    /// summary. Same hydration the paste path does through ClipboardItemForPasteFormat.
    /// </summary>
    private string FullTextPayload(ClipboardHistoryItem item) =>
        TextPayload(NeedsFullText(item) ? _store.GetItem(item.Id) ?? item : item);

    private static string TextPayload(ClipboardHistoryItem item) => item.Text ?? item.Preview ?? string.Empty;
    private static bool TryNormalizeColorText(string? text, string? source, out string hex)
    {
        hex = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var match = Regex.Match(trimmed, @"^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");
        if (!match.Success)
        {
            return false;
        }

        var sourceLooksLikeColorPicker = source?.Contains("ColorPicker", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Contains("PowerToys", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Equals("Clip", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Equals("Clip.Shell", StringComparison.OrdinalIgnoreCase) == true;

        if (!trimmed.StartsWith('#') && !sourceLooksLikeColorPicker)
        {
            return false;
        }

        var value = match.Groups[1].Value;
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(ch => $"{ch}{ch}"));
        }

        hex = "#" + value.ToUpperInvariant();
        return true;
    }

    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static int CountWords(string text) => Regex.Matches(text, @"\b[\w']+\b").Count;
    private static long? SizeOf(string? path) => File.Exists(path) ? new FileInfo(path).Length : null;
    private static string FormatBytes(long? bytes) => bytes is null ? "" : bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / 1024d / 1024d:0.#} MB";
    private static string ContentType(ClipboardHistoryItem item) => item.Kind == ClipboardItemKind.Link ? "Link" : item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1 && Directory.Exists(item.FilePaths[0]) ? "Folder" : item.Kind.ToString();

    private static string DateKey(ClipboardHistoryItem item)
    {
        return DateKey(item, DateTime.Today);
    }

    private static string DateKey(ClipboardHistoryItem item, DateTime today)
    {
        var copied = item.LastCopiedAt.LocalDateTime.Date;
        if (copied == today) return "today";
        if (copied == today.AddDays(-1)) return "yesterday";
        if (copied >= today.AddDays(-7)) return "week";
        if (copied.Year == today.Year && copied.Month == today.Month) return "month";
        return copied.Year == today.Year ? "year" : "older";
    }

    private static void SortByLastCopied(List<ClipboardHistoryItem> items)
    {
        items.Sort((left, right) => right.LastCopiedAt.CompareTo(left.LastCopiedAt));
    }

    private static string FileKindKey(string path)
    {
        if (Directory.Exists(path)) return "folder";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".xls" or ".xlsx" or ".xlsm" => "excel",
            ".vsd" or ".vsdx" => "visio",
            ".html" or ".htm" => "html",
            ".doc" or ".docx" => "word",
            ".ppt" or ".pptx" => "powerpoint",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "image",
            ".txt" or ".log" or ".md" or ".json" or ".xml" or ".css" or ".js" or ".ts" or ".cs" or ".bat" or ".ps1" => "text",
            // Extensionless files used to key on "", which rendered a blank row in the File menu.
            "" => "other",
            _ => ext.TrimStart('.'),
        };
    }

    private static string LabelForFileKey(string key) => key switch
    {
        "all" => "All",
        "folder" => "Folders",
        "pdf" => "PDF",
        "excel" => "Excel",
        "visio" => "Visio",
        "html" => "HTML",
        "word" => "Word",
        "powerpoint" => "PowerPoint",
        "other" => "Other",
        _ => key.ToUpperInvariant(),
    };

    private static bool IsImageFile(string ext) => ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp";
    private static bool IsVideoFile(string ext) => ext is ".mp4" or ".m4v" or ".webm" or ".mov" or ".mkv" or ".avi" or ".ogv" or ".wmv";
    private static bool IsAudioFile(string ext) => ext is ".mp3" or ".m4a" or ".wav" or ".ogg" or ".oga" or ".flac" or ".aac" or ".wma";
    private static bool IsMediaFile(string ext) => IsImageFile(ext) || IsVideoFile(ext) || IsAudioFile(ext);
    private static bool IsHtmlFile(string ext) => ext is ".html" or ".htm";
    private static bool IsOfficeOrVisio(string ext) => ext is ".doc" or ".docx" or ".xls" or ".xlsx" or ".xlsm" or ".ppt" or ".pptx" or ".vsd" or ".vsdx";
    /// <summary>The Office formats that preview as their exported PDF rather than as a picture.</summary>
    private static bool IsPdfBackedOfficeFile(string ext) =>
        ext is ".doc" or ".docx" or ".xls" or ".xlsx" or ".xlsm" or ".ppt" or ".pptx";
    private static bool IsTextFile(string ext) => ext is ".txt" or ".log" or ".md" or ".csv" or ".json" or ".xml" or ".css" or ".js" or ".ts" or ".cs" or ".bat" or ".cmd" or ".ps1" or ".py" or ".html" or ".htm";

    /// <summary>
    /// The icon for a row or the preview header. Image and video items want a thumbnail of their
    /// actual content, and producing one can mean a disk decode or a shell frame extraction —
    /// work that must not run on the UI thread mid-open. Callers that pass
    /// <paramref name="onRicher"/> get the cheap vector glyph back immediately on a cache miss
    /// and the rich thumbnail delivered on the dispatcher once a worker has it; callers without
    /// the callback keep the old synchronous behavior.
    /// </summary>
    private ImageSource IconFor(ClipboardHistoryItem item, int size, bool preferRichPreview = true, Action<ImageSource>? onRicher = null)
    {
        try
        {
            if (item.Kind == ClipboardItemKind.Color)
            {
                return RenderColorSwatch(TextPayload(item), size);
            }

            if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
            {
                return preferRichPreview
                    ? RowThumbnailOrGlyph(item.AssetPath, size, onRicher)
                    : RenderItemVectorIcon(ItemVectorIconKind.Image, size);
            }

            if (item.Kind == ClipboardItemKind.Image)
            {
                return RenderItemVectorIcon(ItemVectorIconKind.Image, size);
            }

            if (item.Kind == ClipboardItemKind.Link)
            {
                var payload = TextPayload(item);

                // Emails are stored as links but are not websites — an @ reads instantly, where a
                // domain monogram just showed the first letter of the mail host.
                if (ClipboardLinkDetector.IsEmail(payload))
                {
                    return RenderItemVectorIcon(ItemVectorIconKind.Email, size);
                }

                // The monogram is the placeholder until the site's real icon arrives, and the
                // fallback when a site has no usable icon.
                return DomainMonogram.For(payload, size)
                    ?? RenderItemVectorIcon(ItemVectorIconKind.Link, size);
            }

            // Copied text gets its own mark, distinct from the document glyph used for text files.
            if (item.Kind == ClipboardItemKind.Text) return RenderSvg("file-icon-plaintext.svg", size);
            if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1)
            {
                var path = item.FilePaths[0];
                if (Directory.Exists(path)) return RenderItemVectorIcon(ItemVectorIconKind.Folder, size);
                if (!preferRichPreview)
                {
                    return IsImageFile(Path.GetExtension(path).ToLowerInvariant())
                        ? RenderItemVectorIcon(ItemVectorIconKind.Image, size)
                        : RenderItemVectorIcon(ItemVectorIconKind.File, size);
                }

                if (File.Exists(path) && IsImageFile(Path.GetExtension(path).ToLowerInvariant()))
                {
                    return RowThumbnailOrGlyph(path, size, onRicher);
                }

                // A video should show a frame from itself, the way File Explorer does, rather than
                // a generic film glyph. Falls back to the type icon when Windows has no thumbnail.
                if (IsVideoFile(Path.GetExtension(path).ToLowerInvariant()))
                {
                    var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                    if (SourceAppIcons.TryGetCachedThumbnail(path, size, dpiScale, out var frame))
                    {
                        if (frame is not null)
                        {
                            return frame;
                        }

                        // A cached null means Windows has no thumbnail for this file; the type
                        // icon below is the answer and asking again will not change it.
                    }
                    else if (onRicher is not null)
                    {
                        // Cold extraction can spend hundreds of milliseconds pulling a frame out
                        // of a video — far too long for a row being built during the open. The
                        // STA worker extracts it and the caller swaps it in over the type icon.
                        SourceAppIcons.ThumbnailAsync(path, size, dpiScale, resolved =>
                        {
                            if (resolved is not null)
                            {
                                Dispatcher.BeginInvoke(() => onRicher(resolved), System.Windows.Threading.DispatcherPriority.Background);
                            }
                        });
                    }
                    else
                    {
                        var extracted = SourceAppIcons.Thumbnail(path, size, dpiScale);
                        if (extracted is not null)
                        {
                            return extracted;
                        }
                    }
                }

                return RenderFileSvg(path, size);
            }

            return RenderItemVectorIcon(ItemVectorIconKind.File, size);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"icon failed id={item.Id}");
            return BitmapFromDrawingImage(System.Drawing.SystemIcons.Application.ToBitmap());
        }
    }

    /// <summary>
    /// A row-sized thumbnail for an image file, without decoding on the UI thread. Already in the
    /// cache: back immediately. Not yet, and the caller supplied <paramref name="onRicher"/>: the
    /// vector glyph now, the thumbnail via the dispatcher once a worker has decoded it. No
    /// callback: the old synchronous decode — those callers sit off the open path, where a
    /// two-frame flash of glyph would cost more than the wait it hides.
    /// </summary>
    private ImageSource RowThumbnailOrGlyph(string path, int size, Action<ImageSource>? onRicher)
    {
        if (TryGetCachedRaster(RasterCacheKey("bitmap", path, RowIconDecodePixels), out var cached))
        {
            return cached;
        }

        if (onRicher is null)
        {
            return LoadCachedBitmap(path, RowIconDecodePixels);
        }

        _ = Task.Run(() =>
        {
            try
            {
                // LoadBitmap freezes what it returns, so decoding off-thread is safe.
                var bitmap = LoadCachedBitmap(path, RowIconDecodePixels);
                Dispatcher.BeginInvoke(() => onRicher(bitmap), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch
            {
                // A thumbnail that will not decode leaves the glyph up, which is the fallback
                // this kind of item renders anyway.
            }
        });

        return RenderItemVectorIcon(ItemVectorIconKind.Image, size);
    }

    private ImageSource RenderFileSvg(string path, int size)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        // Keyed by the theme's icon color: the rasterized SVGs bake it in, and a theme-free key
        // kept serving the old theme's rendering after a switch.
        var cacheKey = $"file-icon|{ext}|{size}|{BrushHex("Muted2")}";
        if (TryGetCachedRaster(cacheKey, out var cached))
        {
            return cached;
        }

        ImageSource source;
        if (ShouldUseWindowsFileIcon(ext))
        {
            // The old path asked the shell for its "large" icon, which is 32x32, and the
            // placeholder then drew it at 128 — a six-fold blowup. Go through the same resolver
            // the source-app icons use, which requests a real size and stays crisp.
            var crisp = SourceAppIcons.Resolve(null, path, size, VisualTreeHelper.GetDpi(this).DpiScaleX);
            if (crisp is not null)
            {
                return RememberRaster(cacheKey, crisp);
            }

            var windowsIcon = WatcherShellIconReader.TryGetIcon(path, large: size >= 48);
            if (windowsIcon is not null)
            {
                using (windowsIcon)
                {
                    source = BitmapFromDrawingImage(windowsIcon);
                    return RememberRaster(cacheKey, source);
                }
            }
        }

        // Every audio format shares one mark rather than a different glyph per container.
        var name = IsAudioFile("." + ext)
            ? "file-icon-audio.svg"
            : string.IsNullOrWhiteSpace(ext) ? "file-60.svg" : $"file-icon-{ext}.svg";
        source = File.Exists(AssetIconPath(name)) ? RenderSvg(name, size) : RenderItemVectorIcon(ItemVectorIconKind.File, size);
        return RememberRaster(cacheKey, source);
    }

    private static bool ShouldUseWindowsFileIcon(string ext)
    {
        return ext is "doc" or "docx" or "xls" or "xlsx" or "xlsm" or "ppt" or "pptx" or "vsd" or "vsdx" or "pdf";
    }

    private ImageSource RenderSvg(string fileName, int size, double scaleX = 1.0, string? color = null)
    {
        var actualColor = color ?? BrushHex("Muted2");
        var cacheKey = $"{fileName}|{size}|{scaleX:0.###}|{actualColor}";
        lock (SvgCacheGate)
        {
            if (SvgImageCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var renderWidth = Math.Max(1, (int)Math.Round(size * scaleX));
        using var bitmap = new System.Drawing.Bitmap(Math.Max(size, renderWidth), size);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        var svg = ThemeSvg(ReadSvgText(fileName), actualColor);
        var document = SvgDocument.FromSvg<SvgDocument>(svg);
        document.Width = renderWidth;
        document.Height = size;
        using var rendered = document.Draw(renderWidth, size);
        graphics.DrawImage(rendered, (bitmap.Width - renderWidth) / 2, 0, renderWidth, size);
        var source = BitmapFromDrawingImage(bitmap);
        lock (SvgCacheGate)
        {
            SvgImageCache[cacheKey] = source;
        }

        return source;
    }

    private static WpfBrush BrushFromHex(string hex)
    {
        try
        {
            return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return WpfBrushes.Transparent;
        }
    }

    private static ImageSource RenderColorSwatch(string hex, int size)
    {
        var cacheKey = $"color|{hex}|{size}";
        if (TryGetCachedRaster(cacheKey, out var cached))
        {
            return cached;
        }

        var swatchSize = Math.Max(18, size);
        using var bitmap = new System.Drawing.Bitmap(swatchSize, swatchSize);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);

        var color = System.Drawing.ColorTranslator.FromHtml(hex);
        using var fill = new System.Drawing.SolidBrush(color);
        using var border = new System.Drawing.Pen(System.Drawing.Color.FromArgb(190, 244, 238, 231), Math.Max(1, swatchSize / 18));
        var inset = Math.Max(2, swatchSize / 12);
        graphics.FillEllipse(fill, inset, inset, swatchSize - inset * 2, swatchSize - inset * 2);
        graphics.DrawEllipse(border, inset, inset, swatchSize - inset * 2, swatchSize - inset * 2);
        return RememberRaster(cacheKey, BitmapFromDrawingImage(bitmap));
    }

    private static string ThemeSvg(string svg, string color)
    {
        return Regex.Replace(svg, @"#[0-9a-fA-F]{3,8}|rgb\([^)]+\)|black|#000", color, RegexOptions.IgnoreCase);
    }

    private static string ReadSvgText(string fileName)
    {
        lock (SvgCacheGate)
        {
            if (SvgTextCache.TryGetValue(fileName, out var cached))
            {
                return cached;
            }

            var svg = File.ReadAllText(AssetIconPath(fileName));
            SvgTextCache[fileName] = svg;
            return svg;
        }
    }

    private static string AssetIconPath(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "icons", fileName);
        if (File.Exists(path)) return path;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "icons", fileName));
    }

    internal static string AppIconPath(AppIconPreference preference)
    {
        var fileName = preference == AppIconPreference.Dark ? "clip-tile-dark.ico" : "clip-tile-light.ico";
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "app-icons", fileName);
        if (File.Exists(path)) return path;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "app-icons", fileName));
    }

    private static ImageSource LoadCachedBitmap(string path, int decodePixels)
    {
        if (!ShouldCacheBitmap(decodePixels))
        {
            return LoadBitmap(path, decodePixels);
        }

        var cacheKey = RasterCacheKey("bitmap", path, decodePixels);
        if (TryGetCachedRaster(cacheKey, decodePixels, out var cached))
        {
            return cached;
        }

        return RememberRaster(cacheKey, decodePixels, LoadBitmap(path, decodePixels));
    }

    // Row icons and preview images are both worth keeping, but not in the same quantity: a 48px row
    // icon is a few KB and there are hundreds of rows, while a 900px preview is a megabyte or two
    // and only the handful you have just looked at matter. Previews used not to be cached at all,
    // which is why every single look at an image decoded it from disk again and needed a "Loading
    // preview..." card over a blown-up row thumbnail to hide the wait.
    private static bool ShouldCacheBitmap(int decodePixels) =>
        decodePixels <= RowIconDecodePixels || decodePixels <= PreviewImageDecodePixels;

    private static bool IsPreviewSized(int decodePixels) => decodePixels > RowIconDecodePixels;

    private static string RasterCacheKey(string prefix, string path, int size)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            var stamp = info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "missing";
            return $"{prefix}|{fullPath}|{size}|{stamp}";
        }
        catch
        {
            return $"{prefix}|{path}|{size}";
        }
    }

    private static bool TryGetCachedRaster(string cacheKey, out ImageSource source) =>
        TryGetCachedRaster(cacheKey, RowIconDecodePixels, out source);

    private static bool TryGetCachedRaster(string cacheKey, int decodePixels, out ImageSource source)
    {
        lock (RasterImageCacheGate)
        {
            var cache = IsPreviewSized(decodePixels) ? PreviewImageCache : RasterImageCache;
            return cache.TryGet(cacheKey, out source);
        }
    }

    private static ImageSource RememberRaster(string cacheKey, ImageSource source) =>
        RememberRaster(cacheKey, RowIconDecodePixels, source);

    private static ImageSource RememberRaster(string cacheKey, int decodePixels, ImageSource source)
    {
        lock (RasterImageCacheGate)
        {
            // Remember evicts only its least recently used entry when full. This used to clear
            // the whole cache instead, which erased the neighbour prefetch mid-arrow-run.
            var cache = IsPreviewSized(decodePixels) ? PreviewImageCache : RasterImageCache;
            cache.Remember(cacheKey, source);
        }

        return source;
    }

    /// <summary>
    /// The decoded preview for a file if one is already in hand, without touching the disk.
    ///
    /// This is what lets the pane switch to an image with no intermediate state at all: when the
    /// answer is yes, the caller assigns it and the next frame is the picture. Only when the answer
    /// is no does anything asynchronous need to happen.
    /// </summary>
    private static bool TryGetDecodedPreview(string path, out ImageSource source) =>
        TryGetCachedRaster(RasterCacheKey("bitmap", path, PreviewImageDecodePixels), PreviewImageDecodePixels, out source);

    private static BitmapImage LoadBitmap(string path, int? decodePixels = null)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixels is > 0)
        {
            bitmap.DecodePixelWidth = decodePixels.Value;
        }

        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal static BitmapSource BitmapFromDrawingImage(DrawingImage image)
    {
        using var bitmap = new System.Drawing.Bitmap(image);
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    /// <summary>
    /// Identifies the app an item was copied from.
    ///
    /// Windows records which window actually called SetClipboardData, and that is the honest
    /// answer. The foreground window is only a guess: take a screenshot with a capture tool while
    /// a browser is on screen and the foreground says "Chrome" even though Chrome had nothing to
    /// do with it. The owner is not always available — an app that has since closed, or one using
    /// delayed rendering, leaves it empty — so the foreground window stays as the fallback.
    /// </summary>
    private static (string? Name, string? Path, string? Aumid) ForegroundSource()
    {
        var owner = DescribeWindowSource(GetClipboardOwner());
        if (owner.Name is not null && !IsOwnProcessName(owner.Name))
        {
            return owner;
        }

        var foreground = DescribeWindowSource(GetForegroundWindow());
        return foreground.Name is not null ? foreground : ("Unknown", null, null);
    }

    private static bool IsOwnProcessName(string name) =>
        name.Equals("Clip", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Clip.Shell", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Clip.Watcher", StringComparison.OrdinalIgnoreCase);

    private static (string? Name, string? Path, string? Aumid) DescribeWindowSource(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return (null, null, null);
        }

        // Read the AUMID from the window rather than the process: packaged apps are often hosted
        // by ApplicationFrameHost, so the process path would name the host, not the real app.
        var aumid = WindowAppUserModelId(hwnd);

        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return (null, null, aumid);
            }

            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            var name = !string.IsNullOrWhiteSpace(path) ? System.IO.Path.GetFileNameWithoutExtension(path) : process.ProcessName;
            return (DisplaySourceName(name), path, aumid);
        }
        catch
        {
            return (null, null, aumid);
        }
    }

    private static string? WindowAppUserModelId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        object? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store is not IPropertyStore properties)
            {
                return null;
            }

            var key = PkeyAppUserModelId;
            if (properties.GetValue(ref key, out var value) != 0)
            {
                return null;
            }

            try
            {
                // VT_LPWSTR
                return value.vt == 31 && value.data != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.data)
                    : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store is not null && Marshal.IsComObject(store))
            {
                Marshal.ReleaseComObject(store);
            }
        }
    }

    private static IntPtr FocusedChildWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var thread = GetWindowThreadProcessId(hwnd, out _);
        if (thread == 0)
        {
            return IntPtr.Zero;
        }

        var info = new GuiThreadInfo { CbSize = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.HwndFocus : IntPtr.Zero;
    }

    private static AutomationElement? FocusedAutomationElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch
        {
            return null;
        }
    }

    private static bool SetAutomationFocus(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            element.SetFocus();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? AutomationValue(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value;
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool CanVerifyPasteTarget(AutomationElement? element, string? expectedText)
    {
        if (element is null || string.IsNullOrEmpty(expectedText))
        {
            return false;
        }

        try
        {
            return element.Current.ControlType == ControlType.Edit &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly;
        }
        catch
        {
            return false;
        }
    }

    internal static bool PasteLooksApplied(string? before, string? after, string? expectedText)
    {
        if (after is null || string.IsNullOrEmpty(expectedText))
        {
            return false;
        }

        if (string.Equals(after, expectedText, StringComparison.Ordinal))
        {
            return true;
        }

        return after.Contains(expectedText, StringComparison.Ordinal);
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var length = Math.Max(GetWindowTextLength(hwnd), 0);
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            return title.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string WindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var className = new StringBuilder(256);
            return GetClassName(hwnd, className, className.Capacity) > 0 ? className.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsFileExplorerWindowClass(string? className)
    {
        return string.Equals(className, "CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "ExploreWClass", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileExplorerSearchTarget(IntPtr hwnd, AutomationElement? element)
    {
        if (element is null || hwnd == IntPtr.Zero)
        {
            return false;
        }

        var processName = TryGetProcessNameForWindow(hwnd);
        if (!string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var current = element.Current;
            if (current.ControlType != ControlType.Edit)
            {
                return false;
            }

            var name = current.Name ?? string.Empty;
            var automationId = current.AutomationId ?? string.Empty;
            return name.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
                automationId.Contains("Search", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldShowPaletteWithoutActivation(IntPtr hwnd, AutomationElement? element)
    {
        if (hwnd == IntPtr.Zero || element is null)
        {
            return false;
        }

        var processName = TryGetProcessNameForWindow(hwnd);
        var windowTitle = WindowTitle(hwnd);
        try
        {
            var current = element.Current;
            return IsFocusSensitiveWebEdit(processName, current.ControlType, current.NativeWindowHandle, current.Name, windowTitle);
        }
        catch
        {
            return false;
        }
    }

    private static bool CouldNeedNoActivatePalette(IntPtr hwnd)
    {
        return CouldNeedNoActivatePalette(hwnd, WindowTitle(hwnd));
    }

    private static bool CouldNeedNoActivatePalette(IntPtr hwnd, string windowTitle)
    {
        if (!windowTitle.Contains("Google Earth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var processName = TryGetProcessNameForWindow(hwnd);
        if (!string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool ShouldCommitPasteWithEnter(IntPtr hwnd, AutomationElement? element)
    {
        if (hwnd == IntPtr.Zero || element is null)
        {
            return false;
        }

        var processName = TryGetProcessNameForWindow(hwnd);
        var windowTitle = WindowTitle(hwnd);
        try
        {
            var current = element.Current;
            return IsGoogleEarthSearchElement(processName, current.ControlType, current.NativeWindowHandle, current.Name, windowTitle);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsFocusSensitiveWebEdit(string? processName, ControlType controlType, int nativeWindowHandle, string? name)
        => IsGoogleEarthSearchElement(processName, controlType, nativeWindowHandle, name, "Google Earth");

    internal static bool IsFocusSensitiveWebEdit(string? processName, ControlType controlType, int nativeWindowHandle, string? name, string? windowTitle)
        => IsGoogleEarthSearchElement(processName, controlType, nativeWindowHandle, name, windowTitle);

    internal static bool IsGoogleEarthSearchElement(string? processName, ControlType controlType, int nativeWindowHandle, string? name, string? windowTitle = null)
    {
        if (!string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (nativeWindowHandle != 0)
        {
            return false;
        }

        if (controlType != ControlType.Edit && controlType != ControlType.Group)
        {
            return false;
        }

        var elementName = name ?? string.Empty;
        return elementName.Contains("Search Google Earth", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(elementName, "Search", StringComparison.OrdinalIgnoreCase) &&
                (windowTitle ?? string.Empty).Contains("Google Earth", StringComparison.OrdinalIgnoreCase)) ||
            elementName.Contains("flt-text-editing", StringComparison.OrdinalIgnoreCase) ||
            elementName.Contains("transparentTextEditing", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyNoActivatePaletteStyle(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, WindowLongExStyle).ToInt64();
        var nextStyle = enabled
            ? style | WindowExNoActivate
            : style & ~WindowExNoActivate;
        if (nextStyle == style)
        {
            return;
        }

        SetWindowLongPtr(hwnd, WindowLongExStyle, new IntPtr(nextStyle));
        ShellLog.Info($"palette no-activate style enabled={enabled}");
    }

    private static string SafeLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int CbSize;
        public int Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public NativeRectangle CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int InputKeyboard = 1;
    private const int WmMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;
    private const int WindowLongExStyle = -20;
    private const long WindowExNoActivate = 0x08000000L;
    private const int ShowWindowShow = 5;
    private const int ShowWindowRestore = 9;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyMenu = 0x12;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyLeftWindows = 0x5B;
    private const ushort VirtualKeyRightWindows = 0x5C;
    private const uint MapVirtualKeyToScanCode = 0;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const ushort VirtualKeyEnter = 0x0D;
    private const ushort VirtualKeyV = 0x56;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint length, out uint returnLength);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    private const int MonitorDpiEffective = 0;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Must stay equal to the Shell border's CornerRadius in XAML — a test asserts it, because the
    /// constant and the literal live in different files.
    ///
    /// 8, because DWMWCP_ROUND is what clips this window and 8 is the radius it uses. That holds
    /// even though the palette is layered for the acrylic: DWM's corner clip was measured to apply
    /// to a layered window, and it is the only thing that can round the blur, which the compositor
    /// paints across the whole window rect no matter what WPF drew. So the Shell's radius is not
    /// the silhouette — it only decides where its own border stroke sits, and a radius wider than
    /// DWM's would hang that stroke inside the clip as a second, mismatched arc.
    /// </summary>
    /// <summary>
    /// The palette's one size, in DIPs, matching Width/Height on the Window in XAML. Kept as
    /// constants because PositionOnMouseScreen re-asserts them on every open — see the comment
    /// there for the DPI drift that made that necessary.
    /// </summary>
    internal const double PaletteDesignWidth = 800;
    internal const double PaletteDesignHeight = 520;

    internal const double ShellCornerRadius = 8;

    /// <summary>
    /// The rounded clip the Shell needs in addition to ClipToBounds. Border.ClipToBounds clips to
    /// the bounds *rectangle*, not to the CornerRadius, so any child that paints its own background
    /// paints square into all four corners. Today DWM's window clip hides that, which makes it a
    /// trap rather than a visible bug — the moment a child extends past the window it would show.
    /// Returns null when the size is not renderable yet: an empty clip blanks the whole palette.
    /// </summary>
    internal static RectangleGeometry? ShellClipGeometry(double width, double height, double radius)
    {
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
        {
            return null;
        }

        // A radius past half the shorter side has no meaning as a corner; Border clamps the same way.
        var corner = Math.Clamp(radius, 0, Math.Min(width, height) / 2);
        var clip = new RectangleGeometry(new Rect(0, 0, width, height), corner, corner);
        clip.Freeze();
        return clip;
    }

    /// <summary>
    /// Re-cuts the Shell's rounded clip. Must run after anything that changes the Shell's size or
    /// its CornerRadius — the fullscreen and expanded-image paths flatten the radius to 0, and a
    /// clip left rounded there would shave the corners off a full-screen video.
    /// </summary>
    private void UpdateShellClip()
    {
        Shell.Clip = ShellClipGeometry(Shell.ActualWidth, Shell.ActualHeight, Shell.CornerRadius.TopLeft);
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e) => UpdateShellClip();

    internal static void ApplyRoundedWindowCorners(IntPtr hwnd)
    {
        try
        {
            var preference = 2;
            var result = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            ShellLog.Info($"rounded corners applied result={result}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "rounded corners failed");
        }
    }

    // Replaces the stock ListBoxItem template: the default one paints hover/selection with the
    // Windows system highlight blue, which is the only place the OS palette could still leak in.
    internal static Style PaletteListItemStyle(WpfBrush hoverFill, WpfBrush selectedFill)
    {
        var root = new FrameworkElementFactory(typeof(Border), "Bd");
        root.SetValue(Border.BackgroundProperty, WpfBrushes.Transparent);
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        root.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(presenter);

        var template = new ControlTemplate(typeof(WpfListBoxItem)) { VisualTree = root };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverFill, "Bd"));
        template.Triggers.Add(hover);
        // Added after hover so selection wins while the pointer is over the selected row.
        var selected = new Trigger { Property = WpfListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, selectedFill, "Bd"));
        template.Triggers.Add(selected);

        var style = new Style(typeof(WpfListBoxItem));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        return style;
    }
}

internal sealed class WpfWindowHandle(IntPtr handle) : Forms.IWin32Window
{
    public IntPtr Handle { get; } = handle;
}

internal static class ClipControlTemplates
{
    private static readonly Lazy<ControlTemplate> PaddedButtonCache = new(() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border x:Name="Root"
          Background="{TemplateBinding Background}"
          BorderBrush="{TemplateBinding BorderBrush}"
          BorderThickness="{TemplateBinding BorderThickness}"
          CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center"
                      VerticalAlignment="Center"
                      Margin="{TemplateBinding Padding}"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Root" Property="Opacity" Value="0.85"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""));

    private static readonly Lazy<ControlTemplate> CenterButtonCache = new(() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border x:Name="Root"
          Background="{TemplateBinding Background}"
          BorderBrush="{TemplateBinding BorderBrush}"
          BorderThickness="{TemplateBinding BorderThickness}"
          CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Root" Property="Opacity" Value="0.85"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""));

    public static ControlTemplate PaddedButton => PaddedButtonCache.Value;

    public static ControlTemplate CenterButton => CenterButtonCache.Value;
}

internal sealed class HotkeyHelpWindow : Window
{
    public HotkeyHelpWindow(ClipHotkeySettings hotkeys, WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush text, WpfBrush muted, WpfBrush line, WpfBrush accentSoft, WpfBrush selectedBorder)
    {
        Title = "Clip Hotkeys";
        Width = 430;
        Height = 482;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // Opaque: this window is its own HWND with no acrylic behind it, so the glass alpha
        // would only wash the color out against whatever the compositor clears the frame to.
        Background = PaletteBackdrop.Opaque(bg);
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control))
            {
                Close();
                e.Handled = true;
            }
        };

        var root = new Border
        {
            Background = bg,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Child = shell;

        var header = new Grid { Background = surface2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = "Hotkeys",
            Foreground = text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
        });
        var close = new WpfButton
        {
            Content = "Close",
            Foreground = muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            FocusVisualStyle = null,
            Template = ClipControlTemplates.PaddedButton,
        };
        close.MouseEnter += (_, _) => { close.Background = accentSoft; close.BorderBrush = selectedBorder; close.Foreground = text; };
        close.MouseLeave += (_, _) => { close.Background = WpfBrushes.Transparent; close.BorderBrush = WpfBrushes.Transparent; close.Foreground = muted; };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var headerBorder = new Border
        {
            Child = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        shell.Children.Add(headerBorder);

        var rows = new StackPanel { Margin = new Thickness(22, 18, 22, 22) };
        foreach (var (key, action) in new[]
        {
            (hotkeys.OpenClip, "Open Clip"),
            (hotkeys.PasteSelected, "Paste selected item"),
            // Derived from the paste binding rather than stored, so it is listed next to it
            // instead of in the rebindable Shortcuts settings page.
            (MainWindow.PasteAndStayGesture(hotkeys.PasteSelected) ?? string.Empty, "Paste and keep Clip open on the next item"),
            (hotkeys.CopySelected, "Copy selected item"),
            (hotkeys.PinSelected, "Pin or unpin selected item"),
            (hotkeys.OpenActions, "Open actions"),
            (hotkeys.OpenSelected, "Open selected link, file, or image"),
            (hotkeys.EditSelected, "Edit selected text"),
            (hotkeys.SaveDebugLog, "Save debug log snapshot"),
            (hotkeys.DeleteSelected, "Delete selected item"),
            (hotkeys.CloseClip, "Close Clip, close a document preview, or escape modals"),
        })
        {
            // An unbound action has no key to show, and an empty cap next to a description reads
            // as a rendering fault rather than as "you turned this one off".
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            rows.Children.Add(HotkeyRow(key, action, surface, text, muted, line));
        }

        Grid.SetRow(rows, 1);
        shell.Children.Add(rows);
        Content = root;
    }

    private static Grid HotkeyRow(string key, string action, WpfBrush surface, WpfBrush text, WpfBrush muted, WpfBrush line)
    {
        var row = new Grid { MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var box = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = key, Foreground = text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
        };
        row.Children.Add(box);
        var label = new TextBlock { Text = action, Foreground = muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }
}

internal sealed class OpenWithWindow : Window
{
    private static readonly Dictionary<string, List<WatcherAppChoice>> AppCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();
    private static readonly string PersistedCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip", "open-with-app-cache.json");
    private readonly string _targetPath;
    private readonly WpfBrush _bg;
    private readonly WpfBrush _surface;
    private readonly WpfBrush _surface2;
    private readonly WpfBrush _surface3;
    private readonly WpfBrush _text;
    private readonly WpfBrush _muted;
    private readonly WpfBrush _line;
    private readonly WpfBrush _selected;
    private readonly WpfBrush _accentSoft;
    private readonly WpfBrush _selectedBorder;
    private readonly WpfTextBox _search = new();
    private readonly WpfListBox _apps = new();
    private readonly TextBlock _status = new();
    private List<WatcherAppChoice> _allApps = [];

    public OpenWithWindow(string targetPath, WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush surface3, WpfBrush text, WpfBrush muted, WpfBrush line, WpfBrush selected, WpfBrush accentSoft, WpfBrush selectedBorder)
    {
        _targetPath = targetPath;
        _bg = bg;
        _surface = surface;
        _surface2 = surface2;
        _surface3 = surface3;
        _text = text;
        _muted = muted;
        _line = line;
        _selected = selected;
        _accentSoft = accentSoft;
        _selectedBorder = selectedBorder;

        Title = "Open With";
        Width = 620;
        Height = 520;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = PaletteBackdrop.Opaque(bg);
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        KeyDown += OnKeyDown;

        var root = new Border
        {
            Background = bg,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        root.Child = shell;

        var header = new Grid { Background = surface2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = $"Open with {Path.GetFileName(targetPath)}",
            Foreground = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var close = PlainButton("Close");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        shell.Children.Add(header);

        var searchShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(16, 8, 16, 8),
            Padding = new Thickness(10, 0, 10, 0),
        };
        _search.Background = WpfBrushes.Transparent;
        _search.Foreground = text;
        _search.BorderThickness = new Thickness(0);
        _search.FontSize = 13;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => RenderApps();
        searchShell.Child = _search;
        Grid.SetRow(searchShell, 1);
        shell.Children.Add(searchShell);

        _apps.Background = bg;
        _apps.Foreground = text;
        _apps.BorderThickness = new Thickness(0);
        _apps.Margin = new Thickness(12, 0, 8, 0);
        _apps.MouseDoubleClick += (_, _) => AcceptSelection();
        _apps.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _apps.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _apps.ItemContainerStyle = MainWindow.PaletteListItemStyle(_accentSoft, _selected);
        Grid.SetRow(_apps, 2);
        shell.Children.Add(_apps);

        var footer = new Grid { Background = surface2, Margin = new Thickness(0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = muted;
        _status.FontSize = 12;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(16, 0, 0, 0);
        footer.Children.Add(_status);
        var browse = PlainButton("Browse...");
        browse.Margin = new Thickness(0, 0, 12, 0);
        browse.Click += (_, _) => BrowseForApp();
        Grid.SetColumn(browse, 1);
        footer.Children.Add(browse);
        Grid.SetRow(footer, 3);
        shell.Children.Add(footer);

        Content = root;
        Loaded += (_, _) =>
        {
            _search.Focus();
            _status.Text = "Loading apps...";
            RenderApps();
            _ = Dispatcher.BeginInvoke(new Action(() => _ = LoadAppsAfterFirstPaintAsync()), System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
    }

    public WatcherAppChoice? SelectedApp { get; private set; }

    private async Task LoadAppsAfterFirstPaintAsync()
    {
        LoadPersistedCache();
        if (TryGetCachedApps(_targetPath, out var cached))
        {
            _allApps = cached;
            _status.Text = $"{_allApps.Count} apps";
            RenderApps();
        }

        await LoadAppsAsync();
    }

    private WpfButton PlainButton(string label)
    {
        var button = new WpfButton
        {
            Content = label,
            Foreground = _muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            FocusVisualStyle = null,
            Template = ClipControlTemplates.PaddedButton,
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = _accentSoft;
            button.BorderBrush = _selectedBorder;
            button.Foreground = _text;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = WpfBrushes.Transparent;
            button.BorderBrush = WpfBrushes.Transparent;
            button.Foreground = _muted;
        };
        return button;
    }

    private async Task LoadAppsAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            ShellLog.Info($"open-with async load started path={_targetPath}");
            _allApps = await Task.Run(() => WatcherAppDiscovery.GetApps(_targetPath).ToList());
            lock (CacheGate)
            {
                AppCache[CacheKey(_targetPath)] = _allApps;
            }

            SavePersistedCache();
            _status.Text = $"{_allApps.Count} apps";
            ShellLog.Info($"open-with async load completed count={_allApps.Count} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            _allApps = [];
            _status.Text = "App list failed. Use Browse.";
            ShellLog.Error(ex, $"open-with async load failed elapsedMs={watch.ElapsedMilliseconds}");
        }

        RenderApps();
    }

    private void RenderApps()
    {
        var query = _search.Text.Trim();
        var apps = _allApps
            .Where(app => string.IsNullOrWhiteSpace(query) ||
                app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (app.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                (app.AppUserModelId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(app => app.IsDefault)
            .ThenByDescending(app => app.IsRecent)
            .ThenBy(app => app.Name)
            .Take(80)
            .ToList();

        _apps.Items.Clear();
        if (_allApps.Count == 0)
        {
            _apps.Items.Add(new WpfListBoxItem
            {
                Content = RowContent(null, "Loading apps...", "You can still close this window."),
                Foreground = _muted,
                IsEnabled = false,
            });
            return;
        }

        foreach (var app in apps)
        {
            var item = new WpfListBoxItem
            {
                Tag = app,
                Content = RowContent(IconForApp(app), app.Name, app.IsDefault ? "Default app" : app.Source),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(4, 1, 4, 1),
                Background = WpfBrushes.Transparent,
                Foreground = _text,
            };
            _apps.Items.Add(item);
        }

        if (_apps.Items.Count > 0)
        {
            _apps.SelectedIndex = 0;
        }
    }

    private StackPanel RowContent(ImageSource? icon, string title, string subtitle)
    {
        var outer = new StackPanel { Orientation = WpfOrientation.Horizontal };
        outer.Children.Add(new WpfImage
        {
            Source = icon,
            Width = 26,
            Height = 26,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var panel = new StackPanel { Orientation = WpfOrientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, Foreground = _text, FontSize = 13, FontWeight = FontWeights.Medium });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = _muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        outer.Children.Add(panel);
        return outer;
    }

    private static bool TryGetCachedApps(string targetPath, out List<WatcherAppChoice> apps)
    {
        lock (CacheGate)
        {
            if (AppCache.TryGetValue(CacheKey(targetPath), out var exact))
            {
                apps = exact;
                return true;
            }

            if (AppCache.TryGetValue(".txt", out var warm))
            {
                apps = warm;
                return true;
            }
        }

        apps = [];
        return false;
    }

    private static string CacheKey(string targetPath) => Directory.Exists(targetPath) ? "<folder>" : Path.GetExtension(targetPath).ToLowerInvariant();

    private static void LoadPersistedCache()
    {
        lock (CacheGate)
        {
            if (AppCache.Count > 0 || !File.Exists(PersistedCachePath))
            {
                return;
            }

            try
            {
                var cache = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<CachedAppChoice>>>(File.ReadAllText(PersistedCachePath)) ?? [];
                foreach (var (key, apps) in cache)
                {
                    AppCache[key] = apps
                        .Select(app => new WatcherAppChoice(app.Name, app.ExecutablePath, app.Source, app.IsDefault, app.IsRecent, app.AppUserModelId))
                        .ToList();
                }

                ShellLog.Info($"open-with persisted cache loaded keys={AppCache.Count}");
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "open-with persisted cache load failed");
            }
        }
    }

    private static void SavePersistedCache()
    {
        lock (CacheGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PersistedCachePath)!);
                var cache = AppCache.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value
                        .Select(app => new CachedAppChoice(app.Name, app.ExecutablePath, app.Source, app.IsDefault, app.IsRecent, app.AppUserModelId))
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(PersistedCachePath, System.Text.Json.JsonSerializer.Serialize(cache));
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "open-with persisted cache save failed");
            }
        }
    }

    private sealed record CachedAppChoice(string Name, string? ExecutablePath, string Source, bool IsDefault, bool IsRecent, string? AppUserModelId);

    private ImageSource? IconForApp(WatcherAppChoice app)
    {
        var key = app.AppUserModelId ?? app.ExecutablePath ?? app.Name;
        lock (CacheGate)
        {
            if (IconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        try
        {
            DrawingImage? image = null;
            if (app.IsDefault)
            {
                image = WatcherShellIconReader.TryGetIcon(_targetPath, large: false);
            }
            else if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                image = WatcherShellIconReader.TryGetIcon($"shell:AppsFolder\\{app.AppUserModelId}", large: false) ??
                    WatcherPackageLogoLookup.TryGetIcon(app.AppUserModelId) ??
                    WatcherStartMenuIconLookup.TryGetIcon(app.Name);
            }
            else if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(app.ExecutablePath);
                image = icon?.ToBitmap();
            }

            image ??= WatcherStartMenuIconLookup.TryGetIcon(app.Name) ?? System.Drawing.SystemIcons.Application.ToBitmap();
            var source = MainWindow.BitmapFromDrawingImage(image);
            image.Dispose();
            lock (CacheGate)
            {
                IconCache[key] = source;
            }

            return source;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open-with icon failed app={app.Name}");
            return null;
        }
    }

    private void AcceptSelection()
    {
        if (_apps.SelectedItem is not WpfListBoxItem { Tag: WatcherAppChoice app })
        {
            return;
        }

        SelectedApp = app;
        Close();
    }

    private void BrowseForApp()
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Applications|*.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        SelectedApp = new WatcherAppChoice(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, "Browse");
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            AcceptSelection();
            e.Handled = true;
        }
    }
}

internal sealed class ExcludedAppPickerWindow : Window
{
    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object IconCacheGate = new();
    private readonly WpfBrush _text;
    private readonly WpfBrush _muted;
    private readonly WpfBrush _surface;
    private readonly WpfBrush _line;
    private readonly WpfBrush _accentSoft;
    private readonly WpfBrush _selectedBorder;
    private readonly WpfTextBox _search = new();
    private readonly WpfListBox _apps = new();
    private readonly TextBlock _status = new();
    private List<WatcherAppChoice> _allApps = [];

    public ExcludedAppPickerWindow(WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush surface3, WpfBrush text, WpfBrush muted, WpfBrush line, WpfBrush selected, WpfBrush accentSoft, WpfBrush selectedBorder)
    {
        _text = text;
        _muted = muted;
        _surface = surface;
        _line = line;
        _accentSoft = accentSoft;
        _selectedBorder = selectedBorder;

        Title = "Choose Excluded App";
        Width = 620;
        Height = 520;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = PaletteBackdrop.Opaque(bg);
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        KeyDown += OnKeyDown;

        var root = new Border
        {
            Background = bg,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        root.Child = shell;

        var header = new Grid { Background = surface2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = "Choose app to exclude",
            Foreground = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0),
        });
        var close = PlainButton("Close");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        shell.Children.Add(header);

        var searchShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(16, 8, 16, 8),
            Padding = new Thickness(10, 0, 10, 0),
        };
        _search.Background = WpfBrushes.Transparent;
        _search.Foreground = text;
        _search.BorderThickness = new Thickness(0);
        _search.FontSize = 13;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => RenderApps();
        searchShell.Child = _search;
        Grid.SetRow(searchShell, 1);
        shell.Children.Add(searchShell);

        _apps.Background = bg;
        _apps.Foreground = text;
        _apps.BorderThickness = new Thickness(0);
        _apps.Margin = new Thickness(12, 0, 8, 0);
        _apps.MouseDoubleClick += (_, _) => AcceptSelection();
        _apps.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _apps.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _apps.ItemContainerStyle = MainWindow.PaletteListItemStyle(accentSoft, selected);
        Grid.SetRow(_apps, 2);
        shell.Children.Add(_apps);

        var footer = new Grid { Background = surface2 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = muted;
        _status.FontSize = 12;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(16, 0, 0, 0);
        footer.Children.Add(_status);
        var browse = PlainButton("Browse...");
        browse.Margin = new Thickness(0, 0, 12, 0);
        browse.Click += (_, _) => BrowseForApp();
        Grid.SetColumn(browse, 1);
        footer.Children.Add(browse);
        Grid.SetRow(footer, 3);
        shell.Children.Add(footer);

        Content = root;
        Loaded += async (_, _) =>
        {
            _search.Focus();
            await LoadAppsAsync();
        };
    }

    public WatcherAppChoice? SelectedApp { get; private set; }

    private WpfButton PlainButton(string label)
    {
        var button = new WpfButton
        {
            Content = label,
            Foreground = _muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            FocusVisualStyle = null,
            Template = ClipControlTemplates.PaddedButton,
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = _accentSoft;
            button.BorderBrush = _selectedBorder;
            button.Foreground = _text;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = WpfBrushes.Transparent;
            button.BorderBrush = WpfBrushes.Transparent;
            button.Foreground = _muted;
        };
        return button;
    }

    private async Task LoadAppsAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            _status.Text = "Loading apps...";
            var target = Path.Combine(Path.GetTempPath(), "clip-privacy-app-picker.txt");
            _allApps = await Task.Run(() => WatcherAppDiscovery.GetApps(target).Where(app => !string.IsNullOrWhiteSpace(app.ExecutablePath)).ToList());
            _status.Text = $"{_allApps.Count} apps";
            ShellLog.Info($"privacy app picker loaded count={_allApps.Count} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            _allApps = [];
            _status.Text = "App list failed. Use Browse.";
            ShellLog.Error(ex, "privacy app picker load failed");
        }

        RenderApps();
    }

    private void RenderApps()
    {
        var query = _search.Text.Trim();
        var apps = _allApps
            .Where(app => string.IsNullOrWhiteSpace(query) ||
                app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (app.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                (app.AppUserModelId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(app => app.IsRecent)
            .ThenBy(app => app.Name)
            .Take(80)
            .ToList();

        _apps.Items.Clear();
        if (_allApps.Count == 0)
        {
            _apps.Items.Add(new WpfListBoxItem
            {
                Content = RowContent(null, "Loading apps...", "Use Browse if the app is not listed."),
                Foreground = _muted,
                IsEnabled = false,
            });
            return;
        }

        foreach (var app in apps)
        {
            var item = new WpfListBoxItem
            {
                Tag = app,
                Content = RowContent(IconForApp(app), app.Name, app.ExecutablePath ?? app.Source),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(4, 1, 4, 1),
                Background = WpfBrushes.Transparent,
                Foreground = _text,
            };
            _apps.Items.Add(item);
        }

        if (_apps.Items.Count > 0)
        {
            _apps.SelectedIndex = 0;
        }
    }

    private StackPanel RowContent(ImageSource? icon, string title, string subtitle)
    {
        var outer = new StackPanel { Orientation = WpfOrientation.Horizontal };
        outer.Children.Add(new WpfImage
        {
            Source = icon,
            Width = 26,
            Height = 26,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var panel = new StackPanel { Orientation = WpfOrientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, Foreground = _text, FontSize = 13, FontWeight = FontWeights.Medium });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = _muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        outer.Children.Add(panel);
        return outer;
    }

    private ImageSource? IconForApp(WatcherAppChoice app)
    {
        var key = app.AppUserModelId ?? app.ExecutablePath ?? app.Name;
        lock (IconCacheGate)
        {
            if (IconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        try
        {
            DrawingImage? image = null;
            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                image = WatcherShellIconReader.TryGetIcon($"shell:AppsFolder\\{app.AppUserModelId}", large: false) ??
                    WatcherPackageLogoLookup.TryGetIcon(app.AppUserModelId) ??
                    WatcherStartMenuIconLookup.TryGetIcon(app.Name);
            }
            else if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(app.ExecutablePath);
                image = icon?.ToBitmap();
            }

            image ??= WatcherStartMenuIconLookup.TryGetIcon(app.Name) ?? System.Drawing.SystemIcons.Application.ToBitmap();
            var source = MainWindow.BitmapFromDrawingImage(image);
            image.Dispose();
            lock (IconCacheGate)
            {
                IconCache[key] = source;
            }

            return source;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"privacy app picker icon failed app={app.Name}");
            return null;
        }
    }

    private void AcceptSelection()
    {
        if (_apps.SelectedItem is not WpfListBoxItem { Tag: WatcherAppChoice app })
        {
            return;
        }

        SelectedApp = app;
        DialogResult = true;
    }

    private void BrowseForApp()
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Choose an app to exclude",
            Filter = "Applications|*.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        SelectedApp = new WatcherAppChoice(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, "Browse");
        DialogResult = true;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            AcceptSelection();
            e.Handled = true;
        }
    }
}

internal sealed record SettingsPalette(WpfBrush Bg, WpfBrush Surface, WpfBrush Surface2, WpfBrush Surface3, WpfBrush Text, WpfBrush Muted, WpfBrush Line, WpfBrush Line2, WpfBrush Accent, WpfBrush AccentSoft, WpfBrush Selected, WpfBrush SelectedBorder);

internal sealed class SettingsWindow : Window
{
    private const string DropdownIconTag = "SettingsDropdownIcon";
    private static readonly ConcurrentDictionary<string, ImageSource> DropdownChevronIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<ControlTemplate> TransparentButtonTemplateCache = new(() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border x:Name="Root"
          Background="{TemplateBinding Background}"
          BorderBrush="{TemplateBinding BorderBrush}"
          BorderThickness="{TemplateBinding BorderThickness}"
          CornerRadius="5">
    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                      Margin="{TemplateBinding Padding}"/>
  </Border>
</ControlTemplate>
"""));
    private static readonly Lazy<ControlTemplate> SubtleSettingsButtonTemplateCache = new(() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border x:Name="Root"
          Background="{TemplateBinding Background}"
          BorderBrush="{TemplateBinding BorderBrush}"
          BorderThickness="{TemplateBinding BorderThickness}"
          CornerRadius="6">
    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                      Margin="{TemplateBinding Padding}"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Root" Property="Opacity" Value="0.82"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""));
    private static readonly Lazy<ControlTemplate> InfoBadgeButtonTemplateCache = new(() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border Background="{TemplateBinding Background}" CornerRadius="11">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True">
      <Setter Property="Opacity" Value="0.75"/>
    </Trigger>
    <Trigger Property="IsPressed" Value="True">
      <Setter Property="Opacity" Value="0.5"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""));

    public static void WarmCaches()
    {
        try
        {
            _ = TransparentButtonTemplateCache.Value;
            _ = SubtleSettingsButtonTemplateCache.Value;
            _ = InfoBadgeButtonTemplateCache.Value;
            WarmDropdownIcon("#646464");
            WarmDropdownIcon("#989898");
            ShellLog.Info("settings caches warmed");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings cache warm failed");
        }
    }

    private static void WarmDropdownIcon(string hex)
    {
        DropdownChevronIconCache.GetOrAdd(hex, CreateDropdownChevronIcon);
    }

    private readonly Grid _content = new();
    private readonly Dictionary<string, WpfButton> _nav = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipShellSettings _settings;
    private ClipUpdateStatus _updateStatus;
    private readonly Action<ClipThemePreference> _applyTheme;
    private readonly Action _refreshClipboardManagerTextTheme;
    private readonly Action<AppIconPreference> _applyAppIcon;
    private readonly Action<bool> _applyRunAtStartup;
    private readonly Action<int?> _applyHistoryLimit;
    private readonly Action<long?> _applyMaxItemSize;
    private readonly Action<bool, bool> _applyUpdateSettings;
    private readonly Action<Action<ClipUpdateStatus>> _checkForUpdates;
    private readonly Func<ClipUpdateStatus, Task> _installUpdate;
    private readonly Action<bool> _clearHistory;
    private readonly Action<string> _exportHistory;
    private readonly Action<string> _restoreHistory;
    private readonly Action _openDataFolder;
    private readonly Action _openDebugLog;
    private readonly Action<string> _changeClipboardFolder;
    private readonly Action _resetClipboardFolder;
    private readonly Action<ClipHotkeySettings> _applyHotkeys;
    private readonly Action<ClipPrivacySettings> _applyPrivacy;
    private readonly Action<PasteFormatPreference> _applyDefaultPasteFormat;
    private readonly Action<bool> _applyExtractTextFromImages;
    private readonly Action<bool> _applySourceAppInList;
    private readonly Action _resetAllSettings;
    private readonly Func<SettingsPalette> _paletteProvider;
    private readonly System.Windows.Threading.DispatcherTimer _themeApplyTimer = new(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
    private SettingsPalette? _paletteOverride;
    private ThemeMorphIcon? _themeIcon;
    private Border? _root;
    private Grid? _header;
    private Border? _headerBorder;
    private TextBlock? _headerTitle;
    private WpfButton? _closeButton;
    private Border? _sidebarBorder;
    private StackPanel? _sidebar;
    private ScrollViewer? _contentScroll;
    private WpfBrush _bg = WpfBrushes.Transparent;
    private WpfBrush _surface = WpfBrushes.Transparent;
    private WpfBrush _surface2 = WpfBrushes.Transparent;
    private WpfBrush _surface3 = WpfBrushes.Transparent;
    private WpfBrush _text = WpfBrushes.Black;
    private WpfBrush _muted = WpfBrushes.Gray;
    private WpfBrush _line = WpfBrushes.Transparent;
    private WpfBrush _line2 = WpfBrushes.Transparent;
    private WpfBrush _accent = WpfBrushes.Teal;
    private WpfBrush _accentSoft = WpfBrushes.Transparent;
    private WpfBrush _selected = WpfBrushes.Transparent;
    private WpfBrush _selectedBorder = WpfBrushes.Transparent;
    private Action? _hostClose;
    private string _currentPage = "General";

    public SettingsWindow(ClipShellSettings settings, ClipUpdateStatus updateStatus, Action<ClipThemePreference> applyTheme, Action refreshClipboardManagerTextTheme, Action<AppIconPreference> applyAppIcon, Action<bool> applyRunAtStartup, Action<int?> applyHistoryLimit, Action<long?> applyMaxItemSize, Action<bool, bool> applyUpdateSettings, Action<Action<ClipUpdateStatus>> checkForUpdates, Func<ClipUpdateStatus, Task> installUpdate, Action openDataFolder, Action openDebugLog, Action<bool> clearHistory, Action<string> exportHistory, Action<string> restoreHistory, Action<string> changeClipboardFolder, Action resetClipboardFolder, Action<ClipHotkeySettings> applyHotkeys, Action<ClipPrivacySettings> applyPrivacy, Action<PasteFormatPreference> applyDefaultPasteFormat, Action<bool> applyExtractTextFromImages, Action<bool> applySourceAppInList, Action resetAllSettings, Func<SettingsPalette> paletteProvider)
    {
        _settings = settings;
        _updateStatus = updateStatus;
        _applyTheme = applyTheme;
        _refreshClipboardManagerTextTheme = refreshClipboardManagerTextTheme;
        _applyAppIcon = applyAppIcon;
        _applyRunAtStartup = applyRunAtStartup;
        _applyHistoryLimit = applyHistoryLimit;
        _applyMaxItemSize = applyMaxItemSize;
        _applyUpdateSettings = applyUpdateSettings;
        _checkForUpdates = checkForUpdates;
        _installUpdate = installUpdate;
        _clearHistory = clearHistory;
        _exportHistory = exportHistory;
        _restoreHistory = restoreHistory;
        _openDataFolder = openDataFolder;
        _openDebugLog = openDebugLog;
        _changeClipboardFolder = changeClipboardFolder;
        _resetClipboardFolder = resetClipboardFolder;
        _applyHotkeys = applyHotkeys;
        _applyPrivacy = applyPrivacy;
        _applyDefaultPasteFormat = applyDefaultPasteFormat;
        _applyExtractTextFromImages = applyExtractTextFromImages;
        _applySourceAppInList = applySourceAppInList;
        _resetAllSettings = resetAllSettings;
        _paletteProvider = paletteProvider;
        ApplyPalette(_paletteProvider());
        _themeApplyTimer.Tick += (_, _) => ApplyPendingTheme();

        Title = "Clip Settings";
        Width = 720;
        Height = 500;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Background = PaletteBackdrop.Opaque(_bg);
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => CenterOnCursorScreen();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                RequestClose();
            }
        };

        var root = new Border
        {
            Background = _bg,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        };
        _root = root;
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // A Border does not clip its child to its rounded corners, so the square header and body
        // painted over the 1px border along the curves. The panel is a fixed 720x500 everywhere
        // (NoResize window, fixed-size host), so a static clip matching the inner rounding holds.
        shell.Clip = new RectangleGeometry(new Rect(0, 0, 718, 498), 13, 13);
        root.Child = shell;

        var header = new Grid { Background = _surface2 };
        _header = header;
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragSettingsWindow();
            }
        };
        var headerTitle = new TextBlock
        {
            Text = "Settings",
            Foreground = _text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
        };
        _headerTitle = headerTitle;
        header.Children.Add(headerTitle);
        var close = new WpfButton
        {
            Content = "Close",
            Foreground = _muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Template = TransparentButtonTemplate(),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _closeButton = close;
        close.MouseEnter += (_, _) =>
        {
            close.Background = _accentSoft;
            close.BorderBrush = _selectedBorder;
            close.Foreground = _text;
        };
        close.MouseLeave += (_, _) =>
        {
            close.Background = WpfBrushes.Transparent;
            close.BorderBrush = WpfBrushes.Transparent;
            close.Foreground = _muted;
        };
        close.Click += (_, _) => RequestClose();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var headerBorder = new Border
        {
            Child = header,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        _headerBorder = headerBorder;
        shell.Children.Add(headerBorder);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        shell.Children.Add(body);

        var sidebar = new StackPanel
        {
            Background = _surface2,
            Margin = new Thickness(12, 14, 12, 12),
        };
        _sidebar = sidebar;
        foreach (var page in new[] { "General", "History", "Shortcuts", "Privacy", "App Overrides", "Appearance", "About" })
        {
            var button = NavButton(page);
            button.MouseEnter += (_, _) => ApplyNavButtonTheme(page, button);
            button.MouseLeave += (_, _) => ApplyNavButtonTheme(page, button);
            button.Click += (_, _) => ShowPage(page);
            _nav[page] = button;
            sidebar.Children.Add(button);
        }
        var sidebarBorder = new Border
        {
            Background = _surface2,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebar,
        };
        _sidebarBorder = sidebarBorder;
        body.Children.Add(sidebarBorder);

        _content.Background = _surface;
        _content.Margin = new Thickness(0);
        var contentScroll = new ScrollViewer
        {
            Content = _content,
            Background = _surface,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _contentScroll = contentScroll;
        Grid.SetColumn(contentScroll, 1);
        body.Children.Add(contentScroll);

        Content = root;
        ShowPage("General");
    }

    public FrameworkElement DetachForHost(Action close)
    {
        _hostClose = close;
        if (Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("Settings content is not hostable.");
        }

        Content = null;
        return content;
    }

    private void RequestClose()
    {
        if (_hostClose is not null)
        {
            _hostClose();
            return;
        }

        Close();
    }

    private void DragSettingsWindow()
    {
        if (_hostClose is not null)
        {
            return;
        }

        DragMove();
    }

    private void CenterOnCursorScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        var screenWidth = bottomRight.X - topLeft.X;
        var screenHeight = bottomRight.Y - topLeft.Y;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        Left = topLeft.X + Math.Max(0, (screenWidth - w) / 2);
        Top = topLeft.Y + Math.Max(0, (screenHeight - h) / 2);
    }

    private WpfButton NavButton(string label)
    {
        return new WpfButton
        {
            Content = label,
            Template = TransparentButtonTemplate(),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Height = 36,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(11, 0, 11, 0),
            Background = WpfBrushes.Transparent,
            Foreground = _muted,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            FontSize = 13,
        };
    }

    private static ControlTemplate TransparentButtonTemplate() => TransparentButtonTemplateCache.Value;

    private static ControlTemplate SubtleSettingsButtonTemplate() => SubtleSettingsButtonTemplateCache.Value;

    private static ControlTemplate InfoBadgeButtonTemplate() => InfoBadgeButtonTemplateCache.Value;

    private ImageSource DropdownIcon()
    {
        var color = _muted is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var key = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return DropdownChevronIconCache.GetOrAdd(key, CreateDropdownChevronIcon);
    }

    private static ImageSource CreateDropdownChevronIcon(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new WpfPen(brush, 2.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new System.Windows.Point(6.5, 9), isFilled: false, isClosed: false);
            context.LineTo(new System.Windows.Point(12, 14.5), isStroked: true, isSmoothJoin: true);
            context.LineTo(new System.Windows.Point(17.5, 9), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(WpfBrushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(new GeometryDrawing(null, pen, geometry));
        drawing.Freeze();

        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private void ApplyPalette(SettingsPalette palette)
    {
        _bg = palette.Bg;
        _surface = palette.Surface;
        _surface2 = palette.Surface2;
        _surface3 = palette.Surface3;
        _text = palette.Text;
        _muted = palette.Muted;
        _line = palette.Line;
        _line2 = palette.Line2;
        _accent = palette.Accent;
        _accentSoft = palette.AccentSoft;
        _selected = palette.Selected;
        _selectedBorder = palette.SelectedBorder;
    }

    private void RefreshTheme(bool rebuildPage = true)
    {
        ApplyPalette(_paletteOverride ?? _paletteProvider());
        Background = PaletteBackdrop.Opaque(_bg);
        if (_root is not null)
        {
            _root.Background = _bg;
            _root.BorderBrush = _line;
        }

        if (_header is not null)
        {
            _header.Background = _surface2;
        }

        if (_headerBorder is not null)
        {
            _headerBorder.BorderBrush = _line;
        }

        if (_headerTitle is not null)
        {
            _headerTitle.Foreground = _text;
        }

        if (_closeButton is not null && !_closeButton.IsMouseOver)
        {
            _closeButton.Background = WpfBrushes.Transparent;
            _closeButton.Foreground = _muted;
        }

        if (_sidebar is not null)
        {
            _sidebar.Background = _surface2;
        }

        if (_sidebarBorder is not null)
        {
            _sidebarBorder.Background = _surface2;
            _sidebarBorder.BorderBrush = _line;
        }

        _content.Background = _surface;
        if (_contentScroll is not null)
        {
            _contentScroll.Background = _surface;
        }

        RefreshNavigationTheme();

        if (rebuildPage)
        {
            ShowPage(_currentPage);
        }
    }

    private void ShowPage(string page)
    {
        _currentPage = page;
        RefreshNavigationTheme();

        _content.Children.Clear();
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        panel.Children.Add(BuildPageHeader(page));
        panel.Children.Add(PageSubtitle(page));

        if (string.Equals(page, "General", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(StartupRow());
            panel.Children.Add(UpdateCheckRow());
            panel.Children.Add(DefaultPasteFormatRow());
            panel.Children.Add(ExtractTextFromImagesRow());
            panel.Children.Add(SourceAppInListRow());
            panel.Children.Add(DragClipsAsFilesRow());
        }

        if (string.Equals(page, "Appearance", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(ThemeRow());
            panel.Children.Add(TranslucentBackgroundRow());
            panel.Children.Add(AppIconRow());
        }

        if (string.Equals(page, "History", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(HistoryLimitRow());
            panel.Children.Add(MaxItemSizeRow());
            panel.Children.Add(ClearHistoryRow());
            panel.Children.Add(BackupHistoryRow());
            panel.Children.Add(DataFolderRow());
        }

        if (string.Equals(page, "Shortcuts", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var row in HotkeyRows())
            {
                panel.Children.Add(row);
            }

            panel.Children.Add(ResetHotkeysRow());
        }

        if (string.Equals(page, "Privacy", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(AddExcludedAppRow());
            foreach (var app in _settings.Privacy.ExcludedApps)
            {
                panel.Children.Add(ExcludedAppRow(app));
            }
        }

        if (string.Equals(page, "App Overrides", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(AddAppOverrideRow());
            foreach (var entry in _settings.AppOverrides)
            {
                panel.Children.Add(AppOverrideRow(entry));
            }
        }

        if (string.Equals(page, "About", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(Row("Version", ClipUpdateService.CurrentVersion));
            panel.Children.Add(Row("Updates", _settings.CheckForUpdatesOnStartup
                ? "Checks automatically"
                : "Manual checks only"));
            panel.Children.Add(Row("Data folder", _settings.EffectiveClipboardFolderPath()));
            panel.Children.Add(Row("Update status", _updateStatus.Message));
            panel.Children.Add(AboutActionsRow());
        }

        foreach (var row in RowsFor(page))
        {
            panel.Children.Add(Row(row.Label, row.Value));
        }

        if (string.Equals(page, "General", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.Equals(_currentPage, "General", StringComparison.OrdinalIgnoreCase) &&
                    _content.Children.Contains(panel))
                {
                    panel.Children.Add(ResetDefaultsFooter());
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        _content.Children.Add(panel);
    }

    private void RefreshNavigationTheme()
    {
        foreach (var (name, button) in _nav)
        {
            ApplyNavButtonTheme(name, button);
        }
    }

    private void ApplyNavButtonTheme(string page, WpfButton button)
    {
        var active = string.Equals(_currentPage, page, StringComparison.OrdinalIgnoreCase);
        if (active)
        {
            button.Background = _selected;
            button.Foreground = _text;
            button.BorderBrush = _selectedBorder;
            return;
        }

        button.Background = button.IsMouseOver ? _surface3 : WpfBrushes.Transparent;
        button.Foreground = button.IsMouseOver ? _text : _muted;
        button.BorderBrush = button.IsMouseOver ? _line2 : WpfBrushes.Transparent;
    }

    private FrameworkElement BuildPageHeader(string page)
    {
        var info = InfoDescriptionFor(page);
        if (info is null)
        {
            return new TextBlock
            {
                Text = page,
                Foreground = _text,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            };
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = page,
            Foreground = _text,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var badge = InfoBadge();
        badge.Click += (_, _) => ShowInfoPopup(page, info);
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);
        return grid;
    }

    private static string? InfoDescriptionFor(string page) => page switch
    {
        "Privacy" => "Apps listed here are excluded from future Clip history to help prevent saving copied information from sensitive apps, such as password managers, banking apps, and private browsers.",
        "App Overrides" => "Apps here will use custom hotkeys for two actions: Open Clip (which key opens Clip while that app is focused) and Paste (which key Clip sends when pasting into that app). For example, if Photoshop already uses Alt+V, you can override Open Clip to a different shortcut while in Photoshop, or change Paste to send a different keystroke. Add an app, pick the action, and set the hotkey.",
        _ => null,
    };

    private WpfButton InfoBadge()
    {
        var glyph = new TextBlock
        {
            Text = "i",
            Foreground = _muted,
            FontFamily = new System.Windows.Media.FontFamily("Cambria, Georgia, Segoe UI"),
            FontStyle = FontStyles.Italic,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var ring = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderBrush = _muted,
            BorderThickness = new Thickness(1),
            Background = WpfBrushes.Transparent,
            Child = glyph,
        };
        var button = new WpfButton
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = ring,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "What is this?",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            FocusVisualStyle = null,
        };
        button.Template = InfoBadgeButtonTemplate();
        return button;
    }

    private void ShowInfoPopup(string title, string description)
    {
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = _text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = _muted,
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });

        var close = SecondaryButton("Got it");
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(close);

        var border = new Border
        {
            Background = PaletteBackdrop.Opaque(_surface),
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };

        var shell = new Grid
        {
            Background = WpfBrushes.Transparent,
            Margin = new Thickness(24),
        };
        shell.Children.Add(border);

        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.Center,
            PlacementTarget = this,
            Child = shell,
        };
        close.Click += (_, _) => popup.IsOpen = false;
        popup.IsOpen = true;
    }

    private TextBlock PageSubtitle(string page)
    {
        var subtitle = page switch
        {
            "General" => "Behavior of the Clip clipboard manager",
            "History" => "Storage, limits, and cleanup",
            "Shortcuts" => "Keyboard controls for Clip",
            "Privacy" => "Apps excluded from clipboard history",
            "App Overrides" => "Custom hotkeys per app for Clip actions",
            "Appearance" => "Theme and icon preferences",
            "About" => "Version, updates, and support files",
            _ => string.Empty,
        };

        return new TextBlock
        {
            Text = subtitle,
            Foreground = _muted,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 18),
        };
    }

    private IEnumerable<(string Label, string Value)> RowsFor(string page)
    {
        return page switch
        {
            "History" => new[]
            {
                ("Pinned items", "Kept until unpinned"),
                ("Duplicate handling", "Same content updates copy count"),
            },
            "Shortcuts" => [],
            "Appearance" => [],
            "Privacy" => [],
            "App Overrides" => [],
            "About" => [],
            _ => [],
        };
    }

    private Border AddExcludedAppRow()
    {
        var button = SecondaryButton("Add app");
        button.Width = 104;
        button.Click += (_, _) =>
        {
            var picker = new ExcludedAppPickerWindow(_bg, _surface, _surface2, _surface3, _text, _muted, _line, _selected, _accentSoft, _selectedBorder)
            {
                Owner = this,
            };
            if (picker.ShowDialog() == true && picker.SelectedApp is not null)
            {
                var privacy = CopyPrivacy();
                privacy.AddExcludedApp(picker.SelectedApp.Name, picker.SelectedApp.ExecutablePath);
                ApplyPrivacyChange(privacy);
                ShowPage("Privacy");
            }
        };

        return ControlRow("Add excluded app", "Choose from installed apps or browse for an .exe.", button);
    }

    private Border ExcludedAppRow(ClipExcludedApp app)
    {
        var button = SecondaryButton("Remove");
        button.Click += (_, _) =>
        {
            var privacy = CopyPrivacy();
            privacy.RemoveExcludedApp(app);
            ApplyPrivacyChange(privacy);
            ShowPage("Privacy");
        };

        return ControlRow(app.Name, string.IsNullOrWhiteSpace(app.ExecutablePath) ? "Excluded from clipboard history." : app.ExecutablePath, button);
    }

    private ClipPrivacySettings CopyPrivacy()
    {
        return new ClipPrivacySettings
        {
            ExcludedApps = _settings.Privacy.ExcludedApps
                .Select(app => ClipExcludedApp.Create(app.Name, app.ExecutablePath))
                .Where(app => app is not null)
                .Select(app => app!)
                .ToList(),
        };
    }

    private void ApplyPrivacyChange(ClipPrivacySettings privacy)
    {
        _applyPrivacy(privacy);
        _settings.Privacy = privacy;
    }

    private Border AddAppOverrideRow()
    {
        var button = SecondaryButton("Add app");
        button.Width = 104;
        button.Click += (_, _) =>
        {
            var picker = new ExcludedAppPickerWindow(_bg, _surface, _surface2, _surface3, _text, _muted, _line, _selected, _accentSoft, _selectedBorder)
            {
                Owner = this,
            };
            if (picker.ShowDialog() == true && picker.SelectedApp is not null)
            {
                var name = ProcessNameFromAppEntry(picker.SelectedApp.Name, picker.SelectedApp.ExecutablePath);
                if (string.IsNullOrWhiteSpace(name)) return;
                _settings.AppOverrides.Add(new ClipAppOverride
                {
                    AppName = name,
                    ExecutablePath = picker.SelectedApp.ExecutablePath,
                    Action = ClipAppOverride.ActionPaste,
                    Hotkey = "Alt+V",
                });
                _settings.Save();
                ShowPage("App Overrides");
            }
        };

        return ControlRow("Add app override", "Choose an app, then pick an action and a custom hotkey for it.", button);
    }

    private Border AppOverrideRow(ClipAppOverride entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            var resolved = ResolveExecutablePathFromProcessName(entry.AppName);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                entry.ExecutablePath = resolved;
                _settings.Save();
            }
        }

        var actionDropdown = StyledDropdown(entry.Action, ClipAppOverride.AvailableActions, selected =>
        {
            if (string.Equals(entry.Action, selected, StringComparison.OrdinalIgnoreCase)) return;
            entry.Action = selected;
            _settings.Save();
        });
        actionDropdown.Width = 108;

        var hotkeyInput = HotkeyInput(entry.Hotkey, requireModifier: true, value =>
        {
            entry.Hotkey = value;
            _settings.Save();
        });
        hotkeyInput.Width = 108;

        var remove = SecondaryButton("Remove");
        remove.Click += (_, _) =>
        {
            _settings.AppOverrides.Remove(entry);
            _settings.Save();
            ShowPage("App Overrides");
        };

        var controls = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
        };
        controls.Children.Add(actionDropdown);
        controls.Children.Add(new Border { Width = 6 });
        controls.Children.Add(hotkeyInput);
        controls.Children.Add(new Border { Width = 6 });
        controls.Children.Add(remove);

        var grid = new Grid { MinHeight = 64 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = entry.AppName,
            Foreground = _text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(nameText, 0);
        Grid.SetColumn(nameText, 0);
        grid.Children.Add(nameText);

        var pathText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.ExecutablePath) ? "Path not available — re-add to refresh." : entry.ExecutablePath!,
            Foreground = _muted,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 12),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(pathText, 1);
        Grid.SetColumn(pathText, 0);
        Grid.SetColumnSpan(pathText, 3);
        grid.Children.Add(pathText);

        Grid.SetRow(controls, 0);
        Grid.SetColumn(controls, 2);
        grid.Children.Add(controls);

        return new Border
        {
            Child = grid,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private static string? ResolveExecutablePathFromProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        try
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string ProcessNameFromAppEntry(string? name, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                return Path.GetFileNameWithoutExtension(executablePath);
            }
            catch
            {
            }
        }

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed;
    }

    private Border StartupRow()
    {
        var enabled = StartupRegistration.IsEnabled();
        var toggle = StartupToggle(enabled);
        return ControlRow(
            "Run at startup",
            "Start Clip when you log in to Windows.",
            toggle);
    }

    private Border UpdateCheckRow()
    {
        var toggle = ToggleButton(_settings.CheckForUpdatesOnStartup, next =>
        {
            _settings.CheckForUpdatesOnStartup = next;
            _applyUpdateSettings(_settings.CheckForUpdatesOnStartup, _settings.InstallUpdatesAutomatically);
        });

        return ControlRow("Check for updates", "Look for new Clip releases when the app opens and while it runs.", toggle);
    }

    private Border HistoryLimitRow()
    {
        return ControlRow(
            "History limit",
            "Maximum unpinned items to keep.",
            StyledDropdown(HistoryLimitLabel(_settings.HistoryLimit), new[] { "100", "250", "500", "1000", "Unlimited" }, selected =>
            {
                var limit = string.Equals(selected, "Unlimited", StringComparison.OrdinalIgnoreCase)
                    ? (int?)null
                    : int.Parse(selected, System.Globalization.CultureInfo.InvariantCulture);
                if (limit == _settings.HistoryLimit)
                {
                    return;
                }

                _settings.HistoryLimit = limit;
                _applyHistoryLimit(limit);
                ShellLog.Info($"settings history limit changed limit={selected}");
            }));
    }

    private static string HistoryLimitLabel(int? limit) => limit is null ? "Unlimited" : limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private Border MaxItemSizeRow()
    {
        return ControlRow(
            "Max item size",
            "Ignore copied items larger than this.",
            StyledDropdown(ClipItemSizeLimit.MaxItemSizeLabel(_settings.MaxItemSizeBytes), new[] { "10 MB", "25 MB", "50 MB", "100 MB", "Unlimited" }, selected =>
            {
                var maxBytes = ParseMaxItemSize(selected);
                if (maxBytes == _settings.MaxItemSizeBytes)
                {
                    return;
                }

                _settings.MaxItemSizeBytes = maxBytes;
                _applyMaxItemSize(maxBytes);
            }));
    }

    private static long? ParseMaxItemSize(string label)
    {
        if (label.Equals("Unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var numberText = label.Replace("MB", "", StringComparison.OrdinalIgnoreCase).Trim();
        return long.TryParse(numberText, out var megabytes) ? megabytes * 1024 * 1024 : 50L * 1024 * 1024;
    }

    private Border ClearHistoryRow()
    {
        const double clearControlWidth = 142;
        var includePinned = false;
        var generalOption = ClearHistorySegment("General", selected: true);
        var allOption = ClearHistorySegment("All", selected: false);
        void Refresh()
        {
            ApplyClearHistorySegmentState(generalOption, !includePinned);
            ApplyClearHistorySegmentState(allOption, includePinned);
        }

        generalOption.MouseLeftButtonDown += (_, _) =>
        {
            includePinned = false;
            Refresh();
        };
        allOption.MouseLeftButtonDown += (_, _) =>
        {
            includePinned = true;
            Refresh();
        };

        // No explicit size: the bordered shell below is 142x30 with a 1px border, so a 142x30
        // child would sit 1px proud on every side and paint over the border and corners.
        var selector = new Grid
        {
            Background = WpfBrushes.Transparent,
            ClipToBounds = true,
        };
        selector.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selector.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selector.Children.Add(generalOption);
        Grid.SetColumn(allOption, 1);
        selector.Children.Add(allOption);

        var selectorShell = new Border
        {
            Width = clearControlWidth,
            Height = 30,
            Background = _surface2,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = selector,
        };

        var clear = SecondaryButton("Clear");
        clear.Width = clearControlWidth;
        clear.Height = 28;
        clear.Margin = new Thickness(0, 6, 0, 0);
        clear.Click += (_, _) => ConfirmClearHistory(includePinned);

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 6),
        };
        actions.Children.Add(selectorShell);
        actions.Children.Add(clear);

        return ControlRow("Clear history", "General keeps pinned items. All removes everything.", actions, minHeight: 78);
    }

    private Border BackupHistoryRow()
    {
        var export = SecondaryButton("Export");
        export.Width = 86;
        export.Click += (_, _) => ExportHistoryToFile();

        var restore = SecondaryButton("Restore");
        restore.Width = 86;
        restore.Margin = new Thickness(8, 0, 0, 0);
        restore.Click += (_, _) => RestoreHistoryFromFile();

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        actions.Children.Add(export);
        actions.Children.Add(restore);

        return ActionOverDetailRow(
            "Backup",
            "Export saves every item and file to a zip. Restore replaces the current history with one.",
            actions,
            minHeight: 76);
    }

    private void ExportHistoryToFile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Clip history",
            FileName = $"Clip History {DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExt = ".zip",
            Filter = "Clip export (*.zip)|*.zip|All files|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        _exportHistory(dialog.FileName);
    }

    private void RestoreHistoryFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Restore Clip history",
            DefaultExt = ".zip",
            Filter = "Clip export (*.zip)|*.zip|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        // Check the file before asking, so the confirmation is never spent on a zip that was
        // going to be refused anyway.
        if (!ClipboardHistoryBackup.IsExport(dialog.FileName))
        {
            System.Windows.MessageBox.Show(
                this,
                "That file is not a Clip history export.",
                "Restore history",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            "Replace the current clipboard history with the contents of this export? Everything saved now, including pinned items and snippets, is removed.",
            "Restore history",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _restoreHistory(dialog.FileName);
        ShowPage("History");
    }

    private Border DataFolderRow()
    {
        var open = SecondaryButton("Open");
        open.Width = 72;
        open.Click += (_, _) => _openDataFolder();

        var change = SecondaryButton("Change");
        change.Width = 86;
        change.Margin = new Thickness(8, 0, 0, 0);
        change.Click += (_, _) => PickClipboardFolder();

        var reset = SecondaryButton("Reset");
        reset.Width = 72;
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.Click += (_, _) =>
        {
            _resetClipboardFolder();
            ShowPage("History");
        };

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        actions.Children.Add(open);
        actions.Children.Add(change);
        actions.Children.Add(reset);

        return ActionOverDetailRow(
            "Clipboard folder",
            _settings.EffectiveClipboardFolderPath(),
            actions,
            minHeight: 76);
    }

    private Border AboutActionsRow()
    {
        var check = SecondaryButton("Check");
        check.Width = 74;
        check.Click += (_, _) =>
        {
            _updateStatus = new ClipUpdateStatus("Checking", "Checking for updates...", ClipUpdateService.CurrentVersion);
            ShowPage("About");
            _checkForUpdates(status =>
            {
                _updateStatus = status;
                ShowPage("About");
            });
        };

        var data = SecondaryButton("Data");
        data.Width = 72;
        data.Margin = new Thickness(8, 0, 0, 0);
        data.Click += (_, _) => _openDataFolder();

        var log = SecondaryButton("Log");
        log.Width = 64;
        log.Margin = new Thickness(8, 0, 0, 0);
        log.Click += (_, _) => _openDebugLog();

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        if (_updateStatus.State == "Update available" && !string.IsNullOrWhiteSpace(_updateStatus.DownloadUrl))
        {
            var update = SecondaryButton($"Update to {_updateStatus.LatestVersion}");
            update.Width = double.NaN; // long version strings must grow the button, not clip
            update.MinWidth = 140;
            update.Margin = new Thickness(0, 0, 8, 0);
            var capturedStatus = _updateStatus;
            update.Click += (_, _) =>
            {
                update.IsEnabled = false;
                update.Content = "Installing...";
                _ = _installUpdate(capturedStatus);
            };
            actions.Children.Add(update);
        }

        actions.Children.Add(check);
        actions.Children.Add(data);
        actions.Children.Add(log);

        return ActionOverDetailRow("Tools", "Check updates, open data, or save/open the debug log.", actions, minHeight: 64);
    }

    private void PickClipboardFolder()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose where Clip saves clipboard content.",
            SelectedPath = _settings.EffectiveClipboardFolderPath(),
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var clipboardFolder = string.Equals(Path.GetFileName(dialog.SelectedPath), "Clipboard History", StringComparison.OrdinalIgnoreCase)
            ? dialog.SelectedPath
            : Path.Combine(dialog.SelectedPath, "Clipboard History");
        _changeClipboardFolder(clipboardFolder);
        ShowPage("History");
    }

    private void ConfirmClearHistory(bool includePinned)
    {
        var message = includePinned
            ? "Clear all saved clipboard history, including pinned items and their saved files?"
            : "Clear general clipboard history and saved files while keeping pinned items?";
        var confirm = System.Windows.MessageBox.Show(
            this,
            message,
            "Clear history",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _clearHistory(includePinned);
        ShowPage("History");
    }

    private Border ClearHistorySegment(string text, bool selected)
    {
        var segment = new Border
        {
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ApplyClearHistorySegmentState(segment, selected);
        return segment;
    }

    private void ApplyClearHistorySegmentState(Border segment, bool selected)
    {
        segment.Background = selected ? _surface : WpfBrushes.Transparent;
        if (segment.Child is TextBlock label)
        {
            label.Foreground = selected ? _accent : _muted;
        }
    }

    private Border SourceAppInListRow()
    {
        return ControlRow(
            "Show source app in the list",
            "Display the app each item was copied from on a second line under it.",
            StyledDropdown(
                _settings.ShowSourceAppInList ? "On" : "Off",
                new[] { "On", "Off" },
                selected =>
                {
                    var enabled = string.Equals(selected, "On", StringComparison.OrdinalIgnoreCase);
                    if (enabled == _settings.ShowSourceAppInList)
                    {
                        return;
                    }

                    _settings.ShowSourceAppInList = enabled;
                    _applySourceAppInList(enabled);
                }));
    }

    /// <summary>
    /// No apply callback and no delegate through the constructor, unlike its neighbours: the drag
    /// reads this straight off the shared settings object when a drag starts, so saving it is the
    /// whole of applying it.
    /// </summary>
    private Border DragClipsAsFilesRow()
    {
        return ControlRow(
            "Drag clips out as files",
            "Dropping a text or link clip on the desktop leaves a .txt or .url file. Off by default: with this on, apps that prefer files — VS Code, Slack — take the file instead of the text.",
            StyledDropdown(
                _settings.DragClipsAsFiles ? "On" : "Off",
                new[] { "Off", "On" },
                selected =>
                {
                    var enabled = string.Equals(selected, "On", StringComparison.OrdinalIgnoreCase);
                    if (enabled == _settings.DragClipsAsFiles)
                    {
                        return;
                    }

                    _settings.DragClipsAsFiles = enabled;
                    _settings.Save();
                    ShellLog.Info($"drag clips as files set enabled={enabled}");
                }));
    }

    private Border ExtractTextFromImagesRow()
    {
        var available = OcrTextExtractor.IsAvailable;
        var description = available
            ? "Read text inside copied images so screenshots can be found by what they say. Runs on your PC; nothing is uploaded."
            : "Unavailable: Windows has no text recognition language installed. Add one in Settings, Language & region.";

        var dropdown = StyledDropdown(
            _settings.ExtractTextFromImages ? "On" : "Off",
            new[] { "Off", "On" },
            selected =>
            {
                var enabled = string.Equals(selected, "On", StringComparison.OrdinalIgnoreCase);
                if (enabled == _settings.ExtractTextFromImages)
                {
                    return;
                }

                _settings.ExtractTextFromImages = enabled;
                _applyExtractTextFromImages(enabled);
            });
        dropdown.IsEnabled = available;

        return ControlRow("Search text in images", description, dropdown);
    }

    private Border DefaultPasteFormatRow()
    {
        return ControlRow(
            "Default paste format",
            "Choose how saved text is pasted.",
            StyledDropdown(PasteFormatLabel(_settings.DefaultPasteFormat), new[] { "Plain text", "Original formatting" }, selected =>
            {
                var preference = string.Equals(selected, "Original formatting", StringComparison.OrdinalIgnoreCase)
                    ? PasteFormatPreference.OriginalFormatting
                    : PasteFormatPreference.PlainText;
                if (preference == _settings.DefaultPasteFormat)
                {
                    return;
                }

                _settings.DefaultPasteFormat = preference;
                _applyDefaultPasteFormat(preference);
            }));
    }

    private Border ResetDefaultsFooter()
    {
        var button = SecondaryButton("Reset to defaults");
        button.Width = 128;
        button.Height = 28;
        button.Click += (_, _) =>
        {
            var confirm = System.Windows.MessageBox.Show(
                this,
                "Reset all settings to their defaults? This restores startup, updates, appearance, paste format, history limit, max item size, clipboard folder, hotkeys, and privacy exclusions.",
                "Reset settings",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            _resetAllSettings();
            ShowPage("General");
        };

        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "Changes save automatically.",
            Foreground = _muted,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return new Border { Child = grid };
    }

    private static string PasteFormatLabel(PasteFormatPreference preference)
    {
        return preference switch
        {
            PasteFormatPreference.OriginalFormatting => "Original formatting",
            _ => "Plain text",
        };
    }

    private IEnumerable<Border> HotkeyRows()
    {
        yield return HotkeyRow("Open Clip", "Bring up the app.", _settings.Hotkeys.OpenClip, true, value => ApplyHotkeyChange(openClip: value));
        yield return HotkeyRow("Paste selected", "Paste selected item.", _settings.Hotkeys.PasteSelected, false, value => ApplyHotkeyChange(pasteSelected: value));
        yield return HotkeyRow("Copy selected", "Copy selected item.", _settings.Hotkeys.CopySelected, false, value => ApplyHotkeyChange(copySelected: value));
        yield return HotkeyRow("Pin selected", "Pin or unpin selected item.", _settings.Hotkeys.PinSelected, false, value => ApplyHotkeyChange(pinSelected: value));
        yield return HotkeyRow("Open actions", "Open the item action menu.", _settings.Hotkeys.OpenActions, false, value => ApplyHotkeyChange(openActions: value));
        yield return HotkeyRow("Open selected", "Open selected link, file, or image.", _settings.Hotkeys.OpenSelected, false, value => ApplyHotkeyChange(openSelected: value));
        yield return HotkeyRow("Edit selected", "Edit selected text.", _settings.Hotkeys.EditSelected, false, value => ApplyHotkeyChange(editSelected: value));
        yield return HotkeyRow("Save debug log", "Save a log snapshot.", _settings.Hotkeys.SaveDebugLog, true, value => ApplyHotkeyChange(saveDebugLog: value));
        yield return HotkeyRow("Delete selected", "Delete selected item.", _settings.Hotkeys.DeleteSelected, false, value => ApplyHotkeyChange(deleteSelected: value));
        yield return HotkeyRow("Close", "Close Clip or escape previews.", _settings.Hotkeys.CloseClip, false, value => ApplyHotkeyChange(closeClip: value));
    }

    private Border HotkeyRow(string label, string hint, string current, bool requireModifier, Action<string> apply)
    {
        return ControlRow(label, hint, HotkeyInput(current, requireModifier, apply));
    }

    private WpfTextBox HotkeyInput(string current, bool requireModifier, Action<string> apply)
    {
        var input = new WpfTextBox
        {
            Text = current,
            Width = 170,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            Background = _surface2,
            Foreground = _text,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CaretBrush = _text,
            IsReadOnly = true,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        input.GotKeyboardFocus += (_, _) => input.Text = "Type shortcut";
        input.LostKeyboardFocus += (_, _) =>
        {
            if (input.Text == "Type shortcut")
            {
                input.Text = current;
            }
        };
        input.PreviewKeyDown += (_, e) =>
        {
            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            // Delete / Backspace (no modifiers) unbinds the hotkey — no key for this action.
            if ((pressed == Key.Delete || pressed == Key.Back) && Keyboard.Modifiers == ModifierKeys.None)
            {
                current = string.Empty;
                input.Text = string.Empty;
                apply(string.Empty);
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }

            if (!TryCreateGestureFromKeyEvent(e, requireModifier, out var gesture))
            {
                // Short strings: the app-override variant of this box is 108px wide (86px of
                // usable text width), which hard-clips anything longer.
                input.Text = requireModifier ? "Needs modifier" : "Invalid";
                e.Handled = true;
                return;
            }

            current = gesture.DisplayText;
            input.Text = gesture.DisplayText;
            apply(gesture.DisplayText);
            Keyboard.ClearFocus();
            e.Handled = true;
        };

        return input;
    }

    private static bool TryCreateGestureFromKeyEvent(System.Windows.Input.KeyEventArgs e, bool requireModifier, out ClipHotkeyGesture gesture)
    {
        gesture = default;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return false;
        }

        var modifiers = Keyboard.Modifiers;
        var text = ClipHotkeyGesture.Format(modifiers, key);
        return requireModifier ? ClipHotkeyGesture.TryParseGlobal(text, out gesture) : ClipHotkeyGesture.TryParse(text, out gesture);
    }

    private Border ResetHotkeysRow()
    {
        var button = SecondaryButton("Reset");
        button.Click += (_, _) =>
        {
            var hotkeys = new ClipHotkeySettings();
            hotkeys.ResetToDefaults();
            _applyHotkeys(hotkeys);
            ShowPage("Shortcuts");
        };

        return ControlRow("Reset hotkeys", "Restore the default shortcuts.", button);
    }

    private void ApplyHotkeyChange(string? openClip = null, string? pasteSelected = null, string? copySelected = null, string? pinSelected = null, string? openActions = null, string? openSelected = null, string? editSelected = null, string? saveDebugLog = null, string? deleteSelected = null, string? closeClip = null)
    {
        var hotkeys = new ClipHotkeySettings
        {
            OpenClip = openClip ?? _settings.Hotkeys.OpenClip,
            PasteSelected = pasteSelected ?? _settings.Hotkeys.PasteSelected,
            CopySelected = copySelected ?? _settings.Hotkeys.CopySelected,
            PinSelected = pinSelected ?? _settings.Hotkeys.PinSelected,
            OpenActions = openActions ?? _settings.Hotkeys.OpenActions,
            OpenSelected = openSelected ?? _settings.Hotkeys.OpenSelected,
            EditSelected = editSelected ?? _settings.Hotkeys.EditSelected,
            SaveDebugLog = saveDebugLog ?? _settings.Hotkeys.SaveDebugLog,
            DeleteSelected = deleteSelected ?? _settings.Hotkeys.DeleteSelected,
            CloseClip = closeClip ?? _settings.Hotkeys.CloseClip,
        };
        _applyHotkeys(hotkeys);
        _settings.Hotkeys = hotkeys;
    }

    private WpfButton StartupToggle(bool enabled)
    {
        return ToggleButton(enabled, next => _applyRunAtStartup(next));
    }

    private WpfButton ToggleButton(bool enabled, Action<bool> apply)
    {
        var trackOff = _line2;
        var trackBorderOff = _line2;
        var knob = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = WpfBrushes.White,
            HorizontalAlignment = enabled ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3),
        };
        var track = new Border
        {
            Width = 42,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = enabled ? _accent : trackOff,
            BorderBrush = enabled ? _accent : trackBorderOff,
            BorderThickness = new Thickness(1),
            Child = knob,
        };
        var toggle = new WpfButton
        {
            Width = 46,
            Height = 34,
            Padding = new Thickness(0),
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = track,
            Tag = enabled,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        toggle.Click += (_, _) =>
        {
            var next = toggle.Tag is not true;
            toggle.Tag = next;
            knob.HorizontalAlignment = next ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
            track.Background = next ? _accent : trackOff;
            track.BorderBrush = next ? _accent : trackBorderOff;
            apply(next);
        };

        return toggle;
    }

    private Border ThemeRow()
    {
        return ControlRow(
            "Theme",
            "Choose System, Light, or Dark.",
            ThemeToggleDropdown(),
            minHeight: 66);
    }

    private Border AppIconRow()
    {
        return ControlRow(
            "App icon",
            "Choose Light or Dark.",
            AppIconPicker());
    }

    private Border TranslucentBackgroundRow()
    {
        var supported = PaletteBackdrop.IsSupported();
        var description = supported
            ? "Let the desktop blur through the palette, like the Windows 11 flyouts."
            : "Unavailable: needs Windows 10 1803 or later.";

        var dropdown = StyledDropdown(
            _settings.TranslucentBackground ? "On" : "Off",
            new[] { "On", "Off" },
            selected =>
            {
                var enabled = string.Equals(selected, "On", StringComparison.OrdinalIgnoreCase);
                if (enabled == _settings.TranslucentBackground)
                {
                    return;
                }

                _settings.TranslucentBackground = enabled;
                // Re-applying the theme is what applies or removes the backdrop and swaps the
                // brushes between the glass and opaque palettes — and it saves the setting.
                _applyTheme(_settings.Theme);
            });
        dropdown.IsEnabled = supported;

        return ControlRow("Translucent background", description, dropdown);
    }

    private FrameworkElement ThemeToggleDropdown()
    {
        var host = new Grid { Width = 74, Height = 30 };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var toggle = AnimatedThemeToggle(_settings.Theme);
        var themeIcon = (ThemeMorphIcon)toggle.Tag;
        Grid.SetColumn(toggle, 0);
        host.Children.Add(toggle);

        var arrow = new WpfButton
        {
            Width = 28,
            Height = 30,
            Padding = new Thickness(0),
            Background = _surface2,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            Content = new WpfImage { Source = DropdownIcon(), Width = 11, Height = 11, Tag = DropdownIconTag },
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
        };
        arrow.MouseEnter += (_, _) => arrow.Background = _accentSoft;
        arrow.MouseLeave += (_, _) => arrow.Background = _surface2;
        Grid.SetColumn(arrow, 1);
        host.Children.Add(arrow);

        var optionHost = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = host,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = PaletteBackdrop.Opaque(_surface),
                BorderBrush = _line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                MinWidth = 112,
                Child = optionHost,
            },
        };

        foreach (var item in new[] { "System", "Light", "Dark" })
        {
            optionHost.Children.Add(ThemeOptionRow(item, selected =>
            {
                ApplyThemeThroughToggle(selected, themeIcon);
                var closeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                closeTimer.Tick += (_, _) =>
                {
                    closeTimer.Stop();
                    popup.IsOpen = false;
                };
                closeTimer.Start();
            }));
        }

        arrow.Click += (_, _) => popup.IsOpen = true;
        return host;
    }

    private WpfButton AnimatedThemeToggle(ClipThemePreference current)
    {
        var dark = current switch
        {
            ClipThemePreference.Dark => true,
            ClipThemePreference.Light => false,
            _ => MainWindow.IsWindowsDarkMode(),
        };

        var icon = new ThemeMorphIcon(_text, dark ? 1 : 0)
        {
            Width = 26,
            Height = 26,
        };

        var button = new WpfButton
        {
            Width = 42,
            Height = 30,
            Padding = new Thickness(7, 1, 7, 1),
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Content = icon,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
            Tag = icon,
        };
        button.MouseEnter += (_, _) =>
        {
            button.BorderBrush = _line2;
            button.Background = WpfBrushes.Transparent;
            button.Opacity = 1;
        };
        button.MouseLeave += (_, _) =>
        {
            button.BorderBrush = WpfBrushes.Transparent;
            button.Background = WpfBrushes.Transparent;
            button.Opacity = 1;
        };
        button.PreviewMouseLeftButtonDown += (_, _) => button.Opacity = 0.72;
        button.PreviewMouseLeftButtonUp += (_, _) => button.Opacity = 1;
        button.Click += (_, _) =>
        {
            var next = MainWindow.NextThemeTogglePreference(PendingTheme ?? _settings.Theme, MainWindow.IsWindowsDarkMode());
            AnimateAndApplyTheme(next, icon);
        };
        return button;
    }

    private ClipThemePreference? PendingTheme { get; set; }

    private void ApplyThemeThroughToggle(ClipThemePreference theme, ThemeMorphIcon icon)
    {
        if (theme == _settings.Theme && PendingTheme is null)
        {
            return;
        }

        AnimateAndApplyTheme(theme, icon);
    }

    private void AnimateAndApplyTheme(ClipThemePreference theme, ThemeMorphIcon icon)
    {
        PendingTheme = theme;
        _themeIcon = icon;
        var dark = theme switch
        {
            ClipThemePreference.Dark => true,
            ClipThemePreference.Light => false,
            _ => MainWindow.IsWindowsDarkMode(),
        };
        icon.AnimateTo(
            dark,
            midway: () => { },
            completed: () => { });

        _themeApplyTimer.Stop();
        _themeApplyTimer.Start();
    }

    private void ApplyPendingTheme()
    {
        _themeApplyTimer.Stop();
        if (PendingTheme is not { } theme)
        {
            return;
        }

        ApplyThemeSelection(theme, refreshImmediately: false);
        _paletteOverride = null;
        // Rebuild the page instead of walking and repainting it: the blanket repaint erased
        // state colors (selected app icon ring, toggle accent, dropdown values) and never
        // reached popup subtrees. A rebuild reconstructs every row from the fresh palette.
        RefreshTheme(rebuildPage: true);
        _refreshClipboardManagerTextTheme();
        _themeIcon?.SetInk(_text);
        PendingTheme = null;
        ShellLog.Info($"settings and main theme applied theme={theme}");
    }

    private sealed class ThemeMorphIcon : Grid
    {
        private SolidColorBrush _ink;
        private readonly Grid _sun = new();
        private readonly Grid _moon = new();
        private readonly ScaleTransform _sunScale = new();
        private readonly ScaleTransform _moonScale = new();
        private readonly RotateTransform _sunRotate = new();
        private readonly RotateTransform _moonRotate = new();

        public ThemeMorphIcon(WpfBrush ink, double progress)
        {
            _ink = DetachedBrush(ink);
            ClipToBounds = false;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
            BuildIcon(progress >= 0.5);
        }

        public void SetInk(WpfBrush ink)
        {
            _ink = DetachedBrush(ink);
            ApplyInk(_sun);
            ApplyInk(_moon);
        }

        public void AnimateTo(bool dark, Action midway, Action completed)
        {
            midway();
            var duration = TimeSpan.FromMilliseconds(320);
            Animate(_sun, OpacityProperty, dark ? 0 : 1, duration);
            Animate(_moon, OpacityProperty, dark ? 1 : 0, duration, completed);
            Animate(_sunScale, ScaleTransform.ScaleXProperty, dark ? 0.86 : 1, duration);
            Animate(_sunScale, ScaleTransform.ScaleYProperty, dark ? 0.86 : 1, duration);
            Animate(_moonScale, ScaleTransform.ScaleXProperty, dark ? 1 : 0.86, duration);
            Animate(_moonScale, ScaleTransform.ScaleYProperty, dark ? 1 : 0.86, duration);
            Animate(_sunRotate, RotateTransform.AngleProperty, dark ? 42 : 0, duration);
            Animate(_moonRotate, RotateTransform.AngleProperty, dark ? 0 : -42, duration);
        }

        private void BuildIcon(bool dark)
        {
            Children.Clear();
            BuildSun();
            BuildMoon();
            Children.Add(_sun);
            Children.Add(_moon);
            SetInitialState(dark);
        }

        private void BuildSun()
        {
            _sun.Width = 24;
            _sun.Height = 24;
            _sun.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            _sun.VerticalAlignment = VerticalAlignment.Center;
            _sun.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            _sun.RenderTransform = new TransformGroup { Children = { _sunScale, _sunRotate } };
            _sun.Children.Add(new WpfEllipse
            {
                Width = 9.5,
                Height = 9.5,
                Fill = _ink,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _sun.Children.Add(new WpfPath
            {
                Data = Geometry.Parse("M12,1 L12,3 M12,21 L12,23 M1,12 L3,12 M21,12 L23,12 M4.2,4.2 L5.7,5.7 M18.3,5.7 L19.8,4.2 M4.2,19.8 L5.7,18.3 M18.3,18.3 L19.8,19.8"),
                Stroke = _ink,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
            });
        }

        private void BuildMoon()
        {
            _moon.Width = 24;
            _moon.Height = 24;
            _moon.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            _moon.VerticalAlignment = VerticalAlignment.Center;
            _moon.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            _moon.RenderTransform = new TransformGroup { Children = { _moonScale, _moonRotate } };
            _moon.Children.Add(new WpfPath
            {
                Data = Geometry.Parse("M21,12.8 A9,9 0 1 1 11.2,3 A7,7 0 1 0 21,12.8 Z"),
                Fill = _ink,
                Stretch = Stretch.None,
            });
        }

        private void SetInitialState(bool dark)
        {
            _sun.Opacity = dark ? 0 : 1;
            _moon.Opacity = dark ? 1 : 0;
            _sunScale.ScaleX = _sunScale.ScaleY = dark ? 0.86 : 1;
            _moonScale.ScaleX = _moonScale.ScaleY = dark ? 1 : 0.86;
            _sunRotate.Angle = dark ? 42 : 0;
            _moonRotate.Angle = dark ? 0 : -42;
        }

        private void ApplyInk(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is WpfShape shape)
                {
                    shape.Fill = shape.Fill is not null ? _ink : shape.Fill;
                    shape.Stroke = shape.Stroke is not null ? _ink : shape.Stroke;
                }

                ApplyInk(child);
            }
        }

        private static void Animate(DependencyObject target, DependencyProperty property, double to, TimeSpan duration, Action? completed = null)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(duration),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            if (completed is not null)
            {
                animation.Completed += (_, _) => completed();
            }

            if (target is UIElement element)
            {
                element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            }
            else if (target is Animatable animatable)
            {
                animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            }
        }

        private static SolidColorBrush DetachedBrush(WpfBrush brush)
        {
            if (brush is SolidColorBrush solid)
            {
                var detached = new SolidColorBrush(solid.Color);
                detached.Freeze();
                return detached;
            }

            var fallback = new SolidColorBrush(Colors.White);
            fallback.Freeze();
            return fallback;
        }
    }

    private Border ThemeOptionRow(string item, Action<ClipThemePreference> onSelected)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7, 10, 7),
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
        };
        row.Child = new TextBlock
        {
            Text = item,
            Foreground = string.Equals(item, _settings.Theme.ToString(), StringComparison.OrdinalIgnoreCase) ? _accent : _muted,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = _accentSoft;
            row.BorderBrush = _selectedBorder;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = WpfBrushes.Transparent;
            row.BorderBrush = WpfBrushes.Transparent;
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (Enum.TryParse<ClipThemePreference>(item, out var theme))
            {
                onSelected(theme);
            }

            e.Handled = true;
        };
        return row;
    }

    private void ApplyThemeSelection(ClipThemePreference theme, bool refreshImmediately = true)
    {
        if (theme == _settings.Theme)
        {
            return;
        }

        _applyTheme(theme);
        if (refreshImmediately)
        {
            RefreshTheme(rebuildPage: false);
        }

        ShellLog.Info($"settings theme changed theme={theme}");
    }

    private FrameworkElement AppIconPicker()
    {
        // 32, not 30: each icon button is 24px image + 3px padding each side + 1px border each
        // side = 32 exactly - a 30px host clipped the bottom edge.
        var host = new Grid { Width = 74, Height = 32 };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var light = AppIconButton(AppIconPreference.Light);
        var dark = AppIconButton(AppIconPreference.Dark);
        Grid.SetColumn(light, 0);
        Grid.SetColumn(dark, 2);
        host.Children.Add(light);
        host.Children.Add(dark);
        return host;
    }

    private WpfButton AppIconButton(AppIconPreference preference)
    {
        var active = preference == _settings.AppIcon;
        var button = new WpfButton
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(3),
            Background = WpfBrushes.Transparent,
            BorderBrush = active ? _selectedBorder : WpfBrushes.Transparent,
            // Thickness stays 1 either way: the hover handler swaps only the brush, and a 0px
            // border made hover invisible on exactly the button the user is about to click.
            BorderThickness = new Thickness(1),
            Content = new WpfImage
            {
                Source = LoadAppIconImage(preference),
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
            },
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
            ToolTip = $"{preference} app icon",
        };
        RenderOptions.SetBitmapScalingMode(button, BitmapScalingMode.HighQuality);
        button.MouseEnter += (_, _) => button.BorderBrush = _selectedBorder;
        button.MouseLeave += (_, _) => button.BorderBrush = active ? _selectedBorder : WpfBrushes.Transparent;
        button.Click += (_, _) =>
        {
            if (preference == _settings.AppIcon)
            {
                return;
            }

            _applyAppIcon(preference);
            ShellLog.Info($"settings app icon changed icon={preference}");
            ShowPage(_currentPage);
        };
        return button;
    }

    private static ImageSource LoadAppIconImage(AppIconPreference preference)
    {
        return MainWindow.RenderAppTileIcon(preference);
    }

    private WpfButton StyledDropdown(string selected, IReadOnlyList<string> items, Action<string> onSelected)
    {
        var label = new TextBlock
        {
            Text = selected,
            Foreground = _text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(label);
        var arrow = new WpfImage
        {
            Source = DropdownIcon(),
            Width = 11,
            Height = 11,
            Tag = DropdownIconTag,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(arrow, 1);
        content.Children.Add(arrow);

        var button = new WpfButton
        {
            Width = 170,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = _surface2,
            Foreground = _text,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            Content = content,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
        };
        button.MouseEnter += (_, _) => button.Background = _accentSoft;
        button.MouseLeave += (_, _) => button.Background = _surface2;

        var optionHost = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = PaletteBackdrop.Opaque(_surface),
                BorderBrush = _line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                MinWidth = 170,
                Child = optionHost,
            },
        };

        foreach (var item in items)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Background = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(1),
            };
            row.Child = new TextBlock
            {
                Text = item,
                Foreground = string.Equals(item, selected, StringComparison.OrdinalIgnoreCase) ? _accent : _muted,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
            };
            row.MouseEnter += (_, _) =>
            {
                row.Background = _accentSoft;
                row.BorderBrush = _selectedBorder;
            };
            row.MouseLeave += (_, _) =>
            {
                row.Background = WpfBrushes.Transparent;
                row.BorderBrush = WpfBrushes.Transparent;
            };
            row.MouseLeftButtonDown += (_, e) =>
            {
                popup.IsOpen = false;
                label.Text = item;
                onSelected(item);
                foreach (Border optionRow in optionHost.Children)
                {
                    var isSelected = optionRow.Child is TextBlock text && string.Equals(text.Text, item, StringComparison.OrdinalIgnoreCase);
                    optionRow.Background = WpfBrushes.Transparent;
                    // The popup closes on this same event, so MouseLeave never fires - without
                    // this the hovered row keeps its 1px hover outline until the next open.
                    optionRow.BorderBrush = WpfBrushes.Transparent;
                    if (optionRow.Child is TextBlock optionText)
                    {
                        optionText.Foreground = isSelected ? _accent : _muted;
                    }
                }

                e.Handled = true;
            };
            optionHost.Children.Add(row);
        }

        button.Click += (_, _) => popup.IsOpen = true;
        return button;
    }

    private WpfButton SecondaryButton(string text)
    {
        var button = new WpfButton
        {
            Content = text,
            Width = 88,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Background = _surface2,
            Foreground = _text,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
        };
        button.MouseEnter += (_, _) => button.Background = _accentSoft;
        button.MouseLeave += (_, _) => button.Background = _surface2;
        return button;
    }

    private Border ControlRow(string label, string hint, FrameworkElement control, double minHeight = 58)
    {
        var grid = new Grid { MinHeight = minHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = _text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = hint,
            Foreground = _muted,
            FontSize = 12,
            LineHeight = 16,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        grid.Children.Add(textPanel);
        Grid.SetColumn(control, 2);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);

        return new Border
        {
            Child = grid,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Border ActionOverDetailRow(string label, string detail, FrameworkElement actions, double minHeight)
    {
        var grid = new Grid { MinHeight = minHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = _text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        Grid.SetColumn(actions, 1);
        actions.Margin = new Thickness(0, 9, 0, 0);
        actions.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(actions);

        var detailText = new TextBlock
        {
            Text = detail,
            Foreground = _muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 16, 12),
        };
        Grid.SetRow(detailText, 1);
        Grid.SetColumnSpan(detailText, 2);
        grid.Children.Add(detailText);

        return new Border
        {
            Child = grid,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Border Row(string label, string value)
    {
        var grid = new Grid { MinHeight = 46 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = _muted,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var valueBox = new WpfTextBox
        {
            Text = value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = WpfBrushes.Transparent,
            Foreground = _text,
            FontSize = 13,
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueBox, 1);
        grid.Children.Add(valueBox);

        return new Border
        {
            Child = grid,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }
}

internal sealed class MenuAction
{
    public static readonly MenuAction Separator = new("", static () => { }, isSeparator: true);

    public MenuAction(string label, Action invoke, bool enabled = true, bool danger = false, bool isSeparator = false, string shortcut = "", IReadOnlyList<MenuAction>? children = null)
    {
        Label = label;
        Invoke = invoke;
        Enabled = enabled;
        Danger = danger;
        IsSeparator = isSeparator;
        Shortcut = shortcut;
        Children = children ?? [];
    }

    public static MenuAction Submenu(string label, IReadOnlyList<MenuAction> children) => new(label, static () => { }, children: children);

    public string Label { get; }
    public Action Invoke { get; }
    public bool Enabled { get; }
    public bool Danger { get; }
    public bool IsSeparator { get; }
    public string Shortcut { get; }
    public IReadOnlyList<MenuAction> Children { get; }
}

internal static class ShellLog
{
    private static readonly object FileGate = new();
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    // CLIP_LOG_ROOT redirects logging the same way Clip.Watcher's LogRoot does, so the test
    // suite's deliberate failure cases (corrupt settings, broken previews) don't write "error"
    // lines into the real shell.log, where they read like production bugs in later triage.
    private static readonly string LogRoot =
        Environment.GetEnvironmentVariable("CLIP_LOG_ROOT") is { Length: > 0 } logRootOverride
            ? logRootOverride
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip");
    public static readonly string Path = System.IO.Path.Combine(LogRoot, "shell.log");
    private static readonly string TempPath = System.IO.Path.Combine(LogRoot, "shell.log.tmp");
    private const long MaxLogBytes = 5L * 1024 * 1024;
    private const long TrimmedLogBytes = 2L * 1024 * 1024;
    private static readonly Lazy<Thread> Writer = new(StartWriter);
    private static volatile bool _stopping;
    private static volatile bool _traceEnabled = TraceEnabledByEnvironment();
    internal static Action<string>? Mirror;

    public static void Configure(string[] args)
    {
        if (_traceEnabled)
        {
            return;
        }

        _traceEnabled = args.Any(arg =>
            string.Equals(arg, "--debug-perf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--debug-log", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--open-test", StringComparison.OrdinalIgnoreCase));
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Snapshot(string message) => Write("INFO", message, force: true);

    public static void Error(Exception exception, string message) => Write("ERROR", $"{message}: {exception}", force: true);

    public static void Flush() => FlushPending();

    public static void Shutdown()
    {
        if (!Writer.IsValueCreated)
        {
            return;
        }

        _stopping = true;
        Signal.Set();
        if (!Writer.Value.Join(TimeSpan.FromSeconds(2)))
        {
            FlushPending();
        }
    }

    private static void Write(string level, string message, bool force = false)
    {
        if (!force && !_traceEnabled)
        {
            return;
        }

        _ = Writer.Value;
        Mirror?.Invoke($"[{level}] {message}");
        Pending.Enqueue($"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        Signal.Set();
    }

    private static bool TraceEnabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("CLIP_SHELL_TRACE");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static Thread StartWriter()
    {
        var thread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "Clip shell log writer",
        };
        thread.Start();
        return thread;
    }

    private static void WriteLoop()
    {
        while (!_stopping || !Pending.IsEmpty)
        {
            Signal.WaitOne(TimeSpan.FromSeconds(1));
            FlushPending();
        }
    }

    private static void FlushPending()
    {
        if (Pending.IsEmpty)
        {
            return;
        }

        var builder = new StringBuilder();
        while (Pending.TryDequeue(out var line))
        {
            builder.Append(line);
        }

        if (builder.Length == 0)
        {
            return;
        }

        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(LogRoot);
                TrimLogIfNeeded();
                File.AppendAllText(Path, builder.ToString());
            }
        }
        catch
        {
        }
    }

    private static void TrimLogIfNeeded()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        var info = new FileInfo(Path);
        if (info.Length <= MaxLogBytes)
        {
            return;
        }

        using (var input = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var output = File.Create(TempPath))
        {
            var marker = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O} [INFO] shell log trimmed to last {TrimmedLogBytes / 1024 / 1024} MB{Environment.NewLine}");
            output.Write(marker, 0, marker.Length);
            input.Seek(Math.Max(0, input.Length - TrimmedLogBytes), SeekOrigin.Begin);
            input.CopyTo(output);
        }

        File.Copy(TempPath, Path, overwrite: true);
        File.Delete(TempPath);
    }
}

internal sealed class RenameWindow : Window
{
    private readonly WpfTextBox _box = new();
    public string Value => _box.Text;

    public RenameWindow(string value, WpfBrush background, WpfBrush foreground, WpfBrush muted, WpfBrush line, WpfBrush surface, WpfBrush accentSoft, WpfBrush selected, WpfBrush selectedBorder, WpfBrush textSelection)
    {
        Title = "Rename";
        Width = 420;
        Height = 190;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = PaletteBackdrop.Opaque(background);
        Foreground = foreground;
        ShowInTaskbar = false;
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);

        _box.Text = value;
        _box.FocusVisualStyle = null;
        _box.Margin = new Thickness(0);
        _box.Padding = new Thickness(12, 8, 12, 8);
        _box.Background = WpfBrushes.Transparent;
        _box.Foreground = foreground;
        _box.BorderThickness = new Thickness(0);
        _box.FontSize = 13;
        _box.SelectionBrush = textSelection;
        _box.MaxLength = 120;
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };

        var fieldBackground = surface;
        var primaryBackground = accentSoft;
        var primaryBorder = selectedBorder;
        var hoverBackground = selected;

        var grid = new Grid { Background = background };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Rename",
            Foreground = foreground,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(18, 16, 18, 2),
        };
        grid.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "Leave blank to use the original title.",
            Foreground = muted,
            FontSize = 12,
            Margin = new Thickness(18, 0, 18, 12),
        };
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        var body = new StackPanel { Margin = new Thickness(18, 0, 18, 18) };
        var boxShell = new Border
        {
            Background = fieldBackground,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = _box,
        };
        body.Children.Add(boxShell);

        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = ModalButton("Cancel", foreground, line, fieldBackground, primaryBackground, primaryBorder, hoverBackground, false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = ModalButton("Save", foreground, line, fieldBackground, primaryBackground, primaryBorder, hoverBackground, true);
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        body.Children.Add(buttons);

        Grid.SetRow(body, 2);
        grid.Children.Add(body);
        Content = grid;

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    private static WpfButton ModalButton(string text, WpfBrush foreground, WpfBrush line, WpfBrush fieldBackground, WpfBrush primaryBackground, WpfBrush primaryBorder, WpfBrush hoverBackground, bool primary)
    {
        var idleBackground = primary ? primaryBackground : fieldBackground;
        var idleBorder = primary ? primaryBorder : line;
        var hoverBg = primary ? hoverBackground : primaryBackground;
        var button = new WpfButton
        {
            Content = text,
            Height = 32,
            MinWidth = primary ? 74 : 68,
            Padding = new Thickness(14, 0, 14, 0),
            Background = idleBackground,
            BorderBrush = idleBorder,
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
        };
        button.Template = ClipControlTemplates.CenterButton;
        button.MouseEnter += (_, _) => { button.Background = hoverBg; button.BorderBrush = primaryBorder; };
        button.MouseLeave += (_, _) => { button.Background = idleBackground; button.BorderBrush = idleBorder; };
        return button;
    }
}

internal sealed class TextEditWindow : Window
{
    private readonly System.Windows.Controls.TextBox _box = new();
    public string Value => _box.Text;

    public TextEditWindow(string value, System.Windows.Media.Brush background, System.Windows.Media.Brush foreground, System.Windows.Media.Brush line, System.Windows.Media.Brush surface, System.Windows.Media.Brush textCursor, System.Windows.Media.Brush accentSoft, System.Windows.Media.Brush selected, System.Windows.Media.Brush selectedBorder, System.Windows.Media.Brush textSelection)
    {
        Title = "Edit Text";
        Width = 640;
        Height = 420;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = PaletteBackdrop.Opaque(background);
        Foreground = foreground;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Auto);
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);

        var editorBackground = surface;
        var primaryBackground = accentSoft;
        var primaryBorder = selectedBorder;
        var secondaryBackground = surface;
        var selectionBrush = textSelection;
        var hoverBackground = selected;

        _box.Text = value;
        _box.TextWrapping = TextWrapping.Wrap;
        _box.AcceptsReturn = true;
        _box.FocusVisualStyle = null;
        _box.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(_box, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(_box, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(_box, TextHintingMode.Auto);
        _box.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _box.Margin = new Thickness(0);
        _box.Padding = new Thickness(14);
        _box.Background = WpfBrushes.Transparent;
        _box.Foreground = foreground;
        _box.BorderThickness = new Thickness(0);
        _box.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        _box.FontSize = 13;
        _box.CaretBrush = textCursor;
        _box.SelectionBrush = selectionBrush;

        var grid = new Grid { Background = background };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(18, 14, 18, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Edit Text",
            Foreground = foreground,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var trim = ModalButton("Trim", foreground, line, secondaryBackground, primaryBackground, primaryBorder, hoverBackground, false);
        trim.Margin = new Thickness(0, 0, 8, 0);
        trim.Click += (_, _) => _box.Text = _box.Text.Trim();
        Grid.SetColumn(trim, 1);
        header.Children.Add(trim);
        grid.Children.Add(header);

        var editorShell = new Border
        {
            Background = editorBackground,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(18, 0, 18, 0),
            Child = _box,
        };
        Grid.SetRow(editorShell, 1);
        grid.Children.Add(editorShell);

        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(18, 14, 18, 18) };
        var cancel = ModalButton("Cancel", foreground, line, secondaryBackground, primaryBackground, primaryBorder, hoverBackground, false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = ModalButton("Save", foreground, line, secondaryBackground, primaryBackground, primaryBorder, hoverBackground, true);
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
    }

    private static WpfButton ModalButton(string text, WpfBrush foreground, WpfBrush line, WpfBrush secondaryBackground, WpfBrush primaryBackground, WpfBrush primaryBorder, WpfBrush hoverBackground, bool primary)
    {
        var idleBackground = primary ? primaryBackground : secondaryBackground;
        var idleBorder = primary ? primaryBorder : line;
        var hoverBg = primary ? hoverBackground : primaryBackground;
        var button = new WpfButton
        {
            Content = text,
            Height = 32,
            MinWidth = primary ? 74 : 68,
            Padding = new Thickness(14, 0, 14, 0),
            Background = idleBackground,
            BorderBrush = idleBorder,
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
        };
        button.Template = ClipControlTemplates.CenterButton;
        button.MouseEnter += (_, _) =>
        {
            button.Background = hoverBg;
            button.BorderBrush = primaryBorder;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = idleBackground;
            button.BorderBrush = idleBorder;
        };
        return button;
    }
}
