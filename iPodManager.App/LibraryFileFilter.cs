using System.IO;

namespace iPodManager;

internal static class LibraryFileFilter
{
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac",
        ".flac",
        ".m4a",
        ".mp3",
        ".ogg",
        ".wav",
        ".wma"
    };

    internal static bool IsAppleDouble(string path) =>
        Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);

    internal static bool IsSupportedAudio(string path) =>
        !IsAppleDouble(path) && SupportedAudioExtensions.Contains(Path.GetExtension(path));
}
