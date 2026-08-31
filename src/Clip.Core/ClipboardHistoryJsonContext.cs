using System.Text.Json.Serialization;

namespace Clip.Core;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ClipboardHistoryItem))]
[JsonSerializable(typeof(List<ClipboardHistoryItem>))]
[JsonSerializable(typeof(ClipboardAssetMetadata))]
[JsonSerializable(typeof(ClipboardHistoryBackupManifest))]
internal partial class ClipboardHistoryJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ClipboardHistoryItem>))]
internal partial class ClipboardHistorySummaryJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClipboardHistoryKeyItem))]
[JsonSerializable(typeof(List<ClipboardHistoryKeyItem>))]
internal partial class ClipboardHistoryKeyJsonContext : JsonSerializerContext
{
}
