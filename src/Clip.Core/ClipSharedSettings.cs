using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clip.Core;

public readonly record struct ClipSharedSettingsSnapshot(
    bool CheckForUpdatesOnStartup,
    PasteFormatPreference DefaultPasteFormat,
    int? HistoryLimit,
    long? MaxItemSizeBytes,
    string? ClipboardFolderPath,
    bool CapturePaused);

public static class ClipSharedSettings
{
    // Canonical defaults shared with Clip.Watcher.WatcherSettings (Program.cs:844-849)
    // so every surface reads settings.json the same way.
    public const int DefaultHistoryLimit = 500;
    public const long DefaultMaxItemSizeBytes = 50L * 1024 * 1024;
    public const PasteFormatPreference DefaultPasteFormat = PasteFormatPreference.PlainText;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static ClipSharedSettingsSnapshot Load()
    {
        if (!File.Exists(ClipStoragePaths.SettingsPath))
        {
            return DefaultSnapshot();
        }

        try
        {
            return LoadFromJson(File.ReadAllText(ClipStoragePaths.SettingsPath));
        }
        catch
        {
            return DefaultSnapshot();
        }
    }

    public static ClipSharedSettingsSnapshot LoadFromJson(string json)
    {
        var root = ParseRootObject(json);
        return new ClipSharedSettingsSnapshot(
            CheckForUpdatesOnStartup: BoolValue(root, "CheckForUpdatesOnStartup", true),
            DefaultPasteFormat: PasteFormatValue(root, "DefaultPasteFormat", DefaultPasteFormat),
            HistoryLimit: NullableIntValue(root, "HistoryLimit", DefaultHistoryLimit),
            MaxItemSizeBytes: NullableLongValue(root, "MaxItemSizeBytes", DefaultMaxItemSizeBytes),
            ClipboardFolderPath: StringValue(root, "ClipboardFolderPath"),
            CapturePaused: BoolValue(root, "CapturePaused", false));
    }

    /// <summary>
    /// Reads the user's preferred paste/copy format from settings.json
    /// ("DefaultPasteFormat"). Defaults to <see cref="PasteFormatPreference.PlainText"/>
    /// when the key is absent or invalid.
    /// </summary>
    public static PasteFormatPreference LoadDefaultPasteFormat() => Load().DefaultPasteFormat;

    public static void SetCheckForUpdatesOnStartup(bool enabled)
    {
        Update(json => SetCheckForUpdatesOnStartupJson(json, enabled));
    }

    public static string SetCheckForUpdatesOnStartupJson(string json, bool enabled)
    {
        var root = ParseRootObject(json);
        root["CheckForUpdatesOnStartup"] = enabled;
        return root.ToJsonString(JsonOptions);
    }

    public static void SetDefaultPasteFormat(PasteFormatPreference format)
    {
        Update(json => SetDefaultPasteFormatJson(json, format));
    }

    public static string SetDefaultPasteFormatJson(string json, PasteFormatPreference format)
    {
        var root = ParseRootObject(json);
        root["DefaultPasteFormat"] = (int)format;
        return root.ToJsonString(JsonOptions);
    }

    public static void SetHistoryLimit(int? limit)
    {
        Update(json => SetHistoryLimitJson(json, limit));
    }

    public static string SetHistoryLimitJson(string json, int? limit)
    {
        var root = ParseRootObject(json);
        root["HistoryLimit"] = limit is null ? null : JsonValue.Create(limit.Value);
        return root.ToJsonString(JsonOptions);
    }

    public static void SetMaxItemSizeBytes(long? bytes)
    {
        Update(json => SetMaxItemSizeBytesJson(json, bytes));
    }

    public static string SetMaxItemSizeBytesJson(string json, long? bytes)
    {
        var root = ParseRootObject(json);
        root["MaxItemSizeBytes"] = bytes is null ? null : JsonValue.Create(bytes.Value);
        return root.ToJsonString(JsonOptions);
    }

    public static void SetCapturePaused(bool paused)
    {
        Update(json => SetCapturePausedJson(json, paused));
    }

    public static string SetCapturePausedJson(string json, bool paused)
    {
        var root = ParseRootObject(json);
        root["CapturePaused"] = paused;
        return root.ToJsonString(JsonOptions);
    }

    public static void SetClipboardFolderPath(string? path)
    {
        Update(json => SetClipboardFolderPathJson(json, path));
    }

    public static string SetClipboardFolderPathJson(string json, string? path)
    {
        var root = ParseRootObject(json);
        root["ClipboardFolderPath"] = string.IsNullOrWhiteSpace(path) ? null : path;
        return root.ToJsonString(JsonOptions);
    }

    private static void Update(Func<string, string> updateJson)
    {
        var path = ClipStoragePaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "{}";

        // A file that exists but does not parse would come out of the merge as just the one
        // key being set here — the user's hotkeys, excluded apps and everything else silently
        // gone. Park the unreadable bytes next door before writing anything over them.
        if (!string.IsNullOrWhiteSpace(existing) && !IsParseableObject(existing))
        {
            QuarantineCorruptSettings();
        }

        WriteSettingsFileAtomic(updateJson(existing));
    }

    /// <summary>
    /// Writes settings.json through a temp file + rename so a crash mid-write can never leave
    /// a truncated file behind. A truncated settings.json is worse than it looks: every loader
    /// falls back to defaults, and the next save then flattens those defaults over the user's
    /// real settings. Shared by every settings writer — this class and the Shell's
    /// ClipShellSettings.Save — so the file on disk is always either the old version or the
    /// new one, never half of one.
    /// </summary>
    public static void WriteSettingsFileAtomic(string json)
    {
        var path = ClipStoragePaths.SettingsPath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Moves an unreadable settings.json aside as settings.json.corrupt-* and returns the new
    /// path (null when there was nothing to move or the move failed). Loaders call this when
    /// the file exists but does not parse: falling back to defaults is right, but the defaults
    /// will be saved sooner or later, and that save must not overwrite the user's only copy of
    /// their settings — quarantining keeps the bytes recoverable next to the fresh file.
    /// </summary>
    public static string? QuarantineCorruptSettings()
    {
        var path = ClipStoragePaths.SettingsPath;
        if (!File.Exists(path))
        {
            return null;
        }

        // Guid rather than a timestamp: the shell's load and the watcher's tray toggle can trip
        // over the same corrupt file at the same moment, and the second move must not throw.
        var quarantinePath = $"{path}.corrupt-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, quarantinePath);
            return quarantinePath;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsParseableObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ClipSharedSettingsSnapshot DefaultSnapshot() =>
        new(
            true,
            DefaultPasteFormat,
            DefaultHistoryLimit,
            DefaultMaxItemSizeBytes,
            null,
            false);

    private static JsonObject ParseRootObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static bool BoolValue(JsonObject root, string name, bool defaultValue)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null)
        {
            return defaultValue;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static PasteFormatPreference PasteFormatValue(JsonObject root, string name, PasteFormatPreference defaultValue)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.Number && node.GetValue<int>() is var numeric)
        {
            return Enum.IsDefined(typeof(PasteFormatPreference), numeric)
                ? (PasteFormatPreference)numeric
                : defaultValue;
        }

        return node.GetValueKind() == JsonValueKind.String &&
            Enum.TryParse<PasteFormatPreference>(node.GetValue<string>(), ignoreCase: true, out var parsed)
                ? parsed
                : defaultValue;
    }

    private static string? StringValue(JsonObject root, string name)
    {
        return root.TryGetPropertyValue(name, out var node) && node is not null && node.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : null;
    }

    // Mirrors Clip.Watcher.WatcherSettings.NullableIntProperty (Program.cs:988): an
    // absent OR explicitly-null key falls back to the default.
    private static int? NullableIntValue(JsonObject root, string name, int? defaultValue)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return defaultValue;
        }

        return node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var result)
            ? result
            : defaultValue;
    }

    // Mirrors Clip.Watcher.WatcherSettings.NullableLongProperty (Program.cs:998).
    private static long? NullableLongValue(JsonObject root, string name, long? defaultValue)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return defaultValue;
        }

        return node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<long>(out var result)
            ? result
            : defaultValue;
    }
}
