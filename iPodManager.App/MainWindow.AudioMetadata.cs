using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace iPodManager
{
    public partial class MainWindow
    {
        public static readonly DependencyProperty HoveredTrackCodecProperty =
            RegisterAudioMetadataProperty(nameof(HoveredTrackCodec));
        public static readonly DependencyProperty HoveredTrackBitrateProperty =
            RegisterAudioMetadataProperty(nameof(HoveredTrackBitrate));
        public static readonly DependencyProperty HoveredTrackSampleRateProperty =
            RegisterAudioMetadataProperty(nameof(HoveredTrackSampleRate));
        public static readonly DependencyProperty HoveredTrackBitDepthProperty =
            RegisterAudioMetadataProperty(nameof(HoveredTrackBitDepth));
        public static readonly DependencyProperty HoveredTrackChannelsProperty =
            RegisterAudioMetadataProperty(nameof(HoveredTrackChannels));
        public static readonly DependencyProperty HoveredTrackBitrateModeProperty =
            RegisterAudioMetadataProperty(nameof(HoveredTrackBitrateMode));
        public static readonly DependencyProperty HoveredTrackAnnotationProperty =
            RegisterAudioMetadataProperty(nameof(HoveredTrackAnnotation));

        private static readonly AudioMetadata UnavailableAudioMetadata =
            new("N/A", "N/A", "N/A", "N/A", "N/A", "N/A");

        public string HoveredTrackCodec
        {
            get => (string)GetValue(HoveredTrackCodecProperty);
            set => SetValue(HoveredTrackCodecProperty, value);
        }

        public string HoveredTrackBitrate
        {
            get => (string)GetValue(HoveredTrackBitrateProperty);
            set => SetValue(HoveredTrackBitrateProperty, value);
        }

        public string HoveredTrackSampleRate
        {
            get => (string)GetValue(HoveredTrackSampleRateProperty);
            set => SetValue(HoveredTrackSampleRateProperty, value);
        }

        public string HoveredTrackBitDepth
        {
            get => (string)GetValue(HoveredTrackBitDepthProperty);
            set => SetValue(HoveredTrackBitDepthProperty, value);
        }

        public string HoveredTrackChannels
        {
            get => (string)GetValue(HoveredTrackChannelsProperty);
            set => SetValue(HoveredTrackChannelsProperty, value);
        }

        public string HoveredTrackBitrateMode
        {
            get => (string)GetValue(HoveredTrackBitrateModeProperty);
            set => SetValue(HoveredTrackBitrateModeProperty, value);
        }

        public string HoveredTrackAnnotation
        {
            get => (string)GetValue(HoveredTrackAnnotationProperty);
            set => SetValue(HoveredTrackAnnotationProperty, value);
        }

        private static DependencyProperty RegisterAudioMetadataProperty(string name) =>
            DependencyProperty.Register(
                name,
                typeof(string),
                typeof(MainWindow),
                new PropertyMetadata("-"));

        private async Task ShowTrackAudioMetadataAsync(
            string audioPath,
            string annotation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearTrackAudioMetadata();

            try
            {
                AudioMetadata? metadata = await RunAnalysisWorkerAsync(
                    () => LoadTrackAudioMetadataAsync(audioPath, cancellationToken),
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested || !IsActiveWaveform(audioPath))
                {
                    return;
                }

                await TypeTrackAudioMetadataAsync(
                    metadata ?? UnavailableAudioMetadata,
                    audioPath,
                    annotation,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Moving to another track intentionally cancels stale metadata.
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is TagLib.CorruptFileException ||
                exception is TagLib.UnsupportedFormatException)
            {
                if (!cancellationToken.IsCancellationRequested && IsActiveWaveform(audioPath))
                {
                    await TypeTrackAudioMetadataAsync(
                        UnavailableAudioMetadata,
                        audioPath,
                        annotation,
                        cancellationToken);
                }
            }
        }

        private void ClearTrackAudioMetadata()
        {
            InformationScrollViewer.ScrollToTop();
            HoveredTrackCodec = "-";
            HoveredTrackBitrate = "-";
            HoveredTrackSampleRate = "-";
            HoveredTrackBitDepth = "-";
            HoveredTrackChannels = "-";
            HoveredTrackBitrateMode = "-";
            HoveredTrackAnnotation = "-";
            SetAnnotationTypingText("-", string.Empty);
        }

        private async Task TypeTrackAudioMetadataAsync(
            AudioMetadata metadata,
            string audioPath,
            string annotation,
            CancellationToken cancellationToken)
        {
            string[] values =
            {
                AvailableOrNA(metadata.Codec),
                AvailableOrNA(metadata.Bitrate),
                AvailableOrNA(metadata.SampleRate),
                AvailableOrNA(metadata.BitDepth),
                AvailableOrNA(metadata.Channels),
                AvailableOrNA(metadata.BitrateMode)
            };

            await Task.WhenAll(
                TypeAudioMetadataFieldsAsync(values, audioPath, cancellationToken),
                TypeAnnotationAsync(
                    AvailableOrNA(annotation),
                    audioPath,
                    cancellationToken));
        }

        private async Task TypeAudioMetadataFieldsAsync(
            string[] values,
            string audioPath,
            CancellationToken cancellationToken)
        {
            int characterCount = values.Max(value => value.Length);

            for (int length = 1; length <= characterCount; length++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsActiveWaveform(audioPath))
                    return;

                HoveredTrackCodec = Prefix(values[0], length);
                HoveredTrackBitrate = Prefix(values[1], length);
                HoveredTrackSampleRate = Prefix(values[2], length);
                HoveredTrackBitDepth = Prefix(values[3], length);
                HoveredTrackChannels = Prefix(values[4], length);
                HoveredTrackBitrateMode = Prefix(values[5], length);

                await Task.Delay(60, cancellationToken);
            }
        }

        private async Task TypeAnnotationAsync(
            string annotation,
            string audioPath,
            CancellationToken cancellationToken)
        {
            for (int length = 1; length <= annotation.Length; length++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsActiveWaveform(audioPath))
                    return;

                string visibleText = Prefix(annotation, length);
                HoveredTrackAnnotation = visibleText;
                SetAnnotationTypingText(visibleText, annotation[length..]);
                await Task.Delay(25, cancellationToken);
            }
        }

        private void SetAnnotationTypingText(string visibleText, string hiddenText)
        {
            AnnotationShadowVisibleRun.Text = visibleText;
            AnnotationShadowHiddenRun.Text = hiddenText;
            AnnotationVisibleRun.Text = visibleText;
            AnnotationHiddenRun.Text = hiddenText;
        }

        private static string Prefix(string value, int length) =>
            value[..Math.Min(length, value.Length)];

        private static string AvailableOrNA(string value) =>
            string.IsNullOrWhiteSpace(value) ? "N/A" : value;

        private static async Task<AudioMetadata?> LoadTrackAudioMetadataAsync(
            string audioPath,
            CancellationToken cancellationToken)
        {
            if (string.Equals(Path.GetExtension(audioPath), ".bank",
                StringComparison.OrdinalIgnoreCase))
            {
                return await LoadBankAudioMetadataAsync(audioPath, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using TagLib.File file = TagLib.File.Create(audioPath);
            cancellationToken.ThrowIfCancellationRequested();
            TagLib.Properties properties = file.Properties;
            string codec = properties.Codecs.FirstOrDefault()?.Description ??
                Path.GetExtension(audioPath).TrimStart('.').ToUpperInvariant();

            return new AudioMetadata(
                codec,
                properties.AudioBitrate > 0 ? $"{properties.AudioBitrate} kb/s" : string.Empty,
                properties.AudioSampleRate > 0 ? $"{properties.AudioSampleRate} Hz" : string.Empty,
                "N/A",
                properties.AudioChannels > 0 ? FormatChannels(properties.AudioChannels) : string.Empty,
                InferBitrateMode(codec));
        }

        private static async Task<AudioMetadata?> LoadBankAudioMetadataAsync(
            string audioPath,
            CancellationToken cancellationToken)
        {
            string? decoderPath = FindAudioTool("vgmstream-cli.exe", "vgmstream-cli");
            if (decoderPath == null)
                return null;

            var startInfo = new ProcessStartInfo(decoderPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(audioPath);

            cancellationToken.ThrowIfCancellationRequested();
            using Process process = Process.Start(startInfo)!;
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => TryKill(process));
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            string output;
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(outputTask, errorTask);
                output = (await outputTask) + Environment.NewLine + (await errorTask);
            }
            finally
            {
                await FinishAnalysisDecoderAsync(process, outputTask, errorTask);
            }
            if (process.ExitCode != 0)
                return null;

            string encoding = ReadMetadataLine(output, "encoding:");
            string bitrate = ReadMetadataLine(output, "bitrate:");
            string sampleRate = ReadMetadataLine(output, "sample rate:");
            string channels = ReadMetadataLine(output, "channels:");
            string sampleType = ReadMetadataLine(output, "sample type:");

            return new AudioMetadata(
                encoding,
                bitrate.Replace("kbps", "kb/s", StringComparison.OrdinalIgnoreCase),
                sampleRate,
                FormatBankBitDepth(sampleType),
                FormatChannels(channels),
                InferBitrateMode(encoding));
        }

        private static string ReadMetadataLine(string metadata, string prefix)
        {
            foreach (string line in metadata.Split('\n'))
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return trimmedLine[prefix.Length..].Trim();
            }

            return string.Empty;
        }

        private static string FormatBankBitDepth(string sampleType)
        {
            if (sampleType.Equals("float", StringComparison.OrdinalIgnoreCase))
                return "32-bit float";
            return sampleType;
        }

        private static string FormatChannels(int channelCount) =>
            channelCount switch
            {
                1 => "1 (Mono)",
                2 => "2 (Stereo)",
                _ => $"{channelCount} (Multichannel)"
            };

        private static string FormatChannels(string channelCount) =>
            int.TryParse(channelCount, out int parsedChannelCount) && parsedChannelCount > 0
                ? FormatChannels(parsedChannelCount)
                : channelCount;

        private static string InferBitrateMode(string codec)
        {
            if (codec.Contains("Vorbis", StringComparison.OrdinalIgnoreCase) ||
                codec.Contains("AAC", StringComparison.OrdinalIgnoreCase))
            {
                return "Variable Bitrate (VBR)";
            }

            return "N/A";
        }

        private sealed record AudioMetadata(
            string Codec,
            string Bitrate,
            string SampleRate,
            string BitDepth,
            string Channels,
            string BitrateMode);
    }
}
