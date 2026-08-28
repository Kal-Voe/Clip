using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clip.Core;

public enum ClipSharedAppIcon
{
    Light = 0,
    Dark = 1,
}

public readonly record struct ClipSharedSettingsSnapshot(
    ClipSharedAppIcon AppIcon,
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
            AppIcon: EnumValue(root, "AppIcon", ClipSharedAppIcon.Light),
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

    public static void SetAppIcon(ClipSharedAppIcon icon)
    {
        Update(json => SetAppIconJson(json, icon));
    }

    public static string SetAppIconJson(string json, ClipSharedAppIcon icon)
    {
        var root = ParseRootObject(json);
        root["AppIcon"] = (int)icon;
        return root.ToJsonString(JsonOptions);
    }

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
        File.WriteAllText(path, updateJson(existing));
    }

    private static ClipSharedSettingsSnapshot DefaultSnapshot() =>
        new(
            ClipSharedAppIcon.Light,
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

    private static TEnum EnumValue<TEnum>(JsonObject root, string name, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.Number && node.GetValue<int>() is var numeric)
        {
            return Enum.IsDefined(typeof(TEnum), numeric) ? (TEnum)Enum.ToObject(typeof(TEnum), numeric) : defaultValue;
        }

        return node.GetValueKind() == JsonValueKind.String &&
            Enum.TryParse<TEnum>(node.GetValue<string>(), ignoreCase: true, out var parsed)
                ? parsed
                : defaultValue;
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
