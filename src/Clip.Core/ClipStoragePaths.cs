using System.Text.Json;

namespace Clip.Core;

public static class ClipStoragePaths
{
    private const string ClipboardFolderName = "Clipboard History";

    // Test seam: redirects the whole %LocalAppData%\Clip tree for the current async context so
    // tests can exercise the real load/save paths against a temp directory. AsyncLocal keeps
    // parallel test classes from seeing each other's override. Never set in production.
    internal static readonly AsyncLocal<string?> RootOverride = new();

    // Harness seam: the same redirect for a whole process rather than one async context, so a
    // timing run can be pointed at a frozen copy of a history and measure the same work every
    // time. Windows resolves LocalApplicationData from the shell, not the environment, so an
    // env var is the only way to move the tree without touching the real one. Never set in
    // production.
    private static readonly string? EnvironmentRoot =
        Environment.GetEnvironmentVariable("CLIP_ROOT") is { Length: > 0 } configured ? configured : null;

    public static string Root => RootOverride.Value ?? EnvironmentRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip");

    public static string SettingsPath => Path.Combine(Root, "settings.json");

    public static string DefaultClipboardFolderPath => Path.Combine(Root, ClipboardFolderName);

    public static string WebView2UserDataFolderPath => Path.Combine(Root, "WebView2");

    /// <summary>
    /// Scratch space for the files a drag materialises so a drop can leave a real file behind.
    /// Under the Clip tree rather than %TEMP% so it is one folder Clip owns and sweeps itself —
    /// see <see cref="ClipboardDragFile.CleanStale"/> — instead of adding to the pile Windows
    /// never quite gets round to clearing.
    /// </summary>
    public static string DragFilesFolderPath => Path.Combine(Root, "DragFiles");

    public static string EffectiveClipboardFolderPath()
    {
        var configured = ConfiguredClipboardFolderPath();
        return string.IsNullOrWhiteSpace(configured) ? DefaultClipboardFolderPath : configured;
    }

    private static string? ConfiguredClipboardFolderPath()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return document.RootElement.TryGetProperty("ClipboardFolderPath", out var value) &&
                value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch
        {
            return null;
        }
    }
}
