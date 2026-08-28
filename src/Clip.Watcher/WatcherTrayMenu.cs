namespace Clip.Watcher;

internal enum WatcherTrayAction
{
    OpenClip,
    PasteLatest,
    TogglePauseCapture,
    CheckForUpdates,
    SaveLogSnapshot,
    OpenSettings,
    Exit
}

internal sealed record WatcherTrayMenuItem(WatcherTrayAction Action, string Label);

internal static class WatcherTrayMenu
{
    public static IReadOnlyList<WatcherTrayMenuItem> DefaultItems { get; } =
    [
        new(WatcherTrayAction.OpenClip, "Open Clip"),
        new(WatcherTrayAction.PasteLatest, "Paste latest item"),
        new(WatcherTrayAction.TogglePauseCapture, "Pause capture"),
        new(WatcherTrayAction.CheckForUpdates, "Check for updates"),
        new(WatcherTrayAction.SaveLogSnapshot, "Save log snapshot"),
        new(WatcherTrayAction.OpenSettings, "Settings"),
        new(WatcherTrayAction.Exit, "Exit")
    ];

    public static string? TrayActionArgument(WatcherTrayAction action) => action switch
    {
        WatcherTrayAction.OpenClip => "open",
        WatcherTrayAction.OpenSettings => "settings",
        WatcherTrayAction.CheckForUpdates => "check-updates",
        WatcherTrayAction.SaveLogSnapshot => "save-log",
        _ => null
    };
}
