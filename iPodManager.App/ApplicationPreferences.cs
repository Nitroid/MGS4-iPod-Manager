using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace iPodManager;

public sealed class ApplicationPreferences
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public int ConversionQuality { get; set; } = 75;
    public bool UiSoundsDisabled { get; set; }
    public bool BackgroundPlaybackEnabled { get; set; }

    public static ApplicationPreferences Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Preferences must be a JSON object.");
            var result = new ApplicationPreferences();
            if (root.TryGetProperty("conversionQuality", out JsonElement quality) &&
                quality.ValueKind == JsonValueKind.Number && quality.TryGetInt32(out int value))
                result.ConversionQuality = Math.Clamp(value, 1, 100);
            if (root.TryGetProperty("uiSoundsDisabled", out JsonElement sounds) && sounds.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result.UiSoundsDisabled = sounds.GetBoolean();
            if (root.TryGetProperty("backgroundPlaybackEnabled", out JsonElement background) &&
                background.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result.BackgroundPlaybackEnabled = background.GetBoolean();
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Could not load preferences; defaults were used: {ex}");
            return new();
        }
    }

    public void Save(string path)
    {
        ConversionQuality = Math.Clamp(ConversionQuality, 1, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
