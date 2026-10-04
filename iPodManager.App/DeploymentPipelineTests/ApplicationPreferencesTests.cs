using iPodManager;

internal static class ApplicationPreferencesTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "mgs4-ipod-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "preferences.json");
        try
        {
            File.WriteAllText(path, "{\"conversionQuality\":75,\"uiSoundsDisabled\":true}");
            ApplicationPreferences old = ApplicationPreferences.Load(path);
            Check(!old.BackgroundPlaybackEnabled && old.UiSoundsDisabled,
                "existing preferences load with background playback off");

            old.BackgroundPlaybackEnabled = true;
            old.Save(path);
            Check(ApplicationPreferences.Load(path).BackgroundPlaybackEnabled,
                "background playback on persists");

            old.BackgroundPlaybackEnabled = false;
            old.Save(path);
            Check(!ApplicationPreferences.Load(path).BackgroundPlaybackEnabled,
                "background playback off persists");

            File.WriteAllText(path, "{}");
            Check(!ApplicationPreferences.Load(path).BackgroundPlaybackEnabled,
                "missing background playback preference defaults off");
            foreach (string malformedRoot in new[] { "[]", "null", "42", "\"text\"" })
            {
                File.WriteAllText(path, malformedRoot);
                Check(ApplicationPreferences.Load(path).ConversionQuality == 75,
                    "non-object preferences safely use defaults: " + malformedRoot);
            }
            foreach (string malformedQuality in new[] { "null", "\"75\"", "{}", "[]" })
            {
                File.WriteAllText(path, "{\"conversionQuality\":" + malformedQuality +
                    ",\"uiSoundsDisabled\":true}");
                ApplicationPreferences recovered = ApplicationPreferences.Load(path);
                Check(recovered.ConversionQuality == 75 && recovered.UiSoundsDisabled,
                    "invalid quality preserves valid preferences: " + malformedQuality);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }
}
