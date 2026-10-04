using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace iPodManager
{
    public partial class MainWindow
    {
        private const int WaveformPeakCount = 2048;
        private sealed record WaveformData(float[] Peaks, double DurationSeconds);
        private sealed record DecodedAudio(byte[] Pcm, double DurationSeconds);

        private readonly Dictionary<string, WaveformData> _waveforms =
            new(StringComparer.OrdinalIgnoreCase);
        private float[]? _currentWaveformPeaks;
        private int _waveformDisplayVersion;
        private string? _activeWaveformPath;

        private bool IsActiveWaveform(string audioPath) =>
            !_analysisClosed && string.Equals(
                _activeWaveformPath,
                audioPath,
                StringComparison.OrdinalIgnoreCase);

        private static string GetAnalysisCacheKey(string audioPath)
        {
            try
            {
                var file = new FileInfo(audioPath);
                return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return audioPath;
            }
        }

        private async Task ShowWaveformAsync(string audioPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _activeWaveformPath = audioPath;
            int displayVersion = ++_waveformDisplayVersion;

            string analysisKey = GetAnalysisCacheKey(audioPath);
            if (_waveforms.TryGetValue(analysisKey, out WaveformData? cached))
            {
                _currentWaveformPeaks = cached.Peaks;
                HoveredTrackDuration = FormatTrackDuration(cached.DurationSeconds);
                DrawWaveform(cached.Peaks, displayVersion: displayVersion);
                return;
            }

            _currentWaveformPeaks = null;
            WaveformImage.Source = null;

            try
            {
                WaveformData? waveform = await RunAnalysisWorkerAsync(
                    () => LoadOrGenerateWaveformPeaksAsync(audioPath, cancellationToken),
                    cancellationToken);

                if (waveform == null || cancellationToken.IsCancellationRequested || _analysisClosed)
                    return;

                _waveforms[analysisKey] = waveform;
                _currentWaveformPeaks = waveform.Peaks;
                HoveredTrackDuration = FormatTrackDuration(waveform.DurationSeconds);
                DrawWaveform(waveform.Peaks, displayVersion: displayVersion);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Moving directly to another row intentionally cancels stale work.
            }
            catch (IOException exception)
            {
                AppLog.Warn($"Waveform unavailable: {exception}");
                // Leave the panel blank when audio or cache data is inaccessible.
            }
            catch (UnauthorizedAccessException exception)
            {
                AppLog.Warn($"Waveform unavailable: {exception}");
                // Waveforms are supplemental and must never prevent normal use.
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Unhandled waveform error: {exception}");
            }
        }

        private static string FormatTrackDuration(double durationSeconds)
        {
            int totalSeconds = Math.Max(0, (int)Math.Floor(durationSeconds));
            return totalSeconds == 0
                ? string.Empty
                : $"{totalSeconds / 60}:{totalSeconds % 60:00}";
        }

        private void WaveformDrawingSurface_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            if (!_analysisClosed && _currentWaveformPeaks != null)
                DrawWaveform(_currentWaveformPeaks, animate: false);
        }

        private void DrawWaveform(
            float[] peaks,
            bool animate = true,
            int displayVersion = -1)
        {
            double width = WaveformDrawingSurface.ActualWidth;
            double height = WaveformDrawingSurface.ActualHeight;

            if (width <= 1 || height <= 1 || peaks.Length == 0)
            {
                WaveformImage.Source = null;
                return;
            }

            double centerY = height + 1;
            double halfHeight = Math.Max(0, height - 1) * 0.9;
            int pointCount = Math.Max(2, (int)Math.Ceiling(width));
            var amplitudes = new double[pointCount];

            for (int x = 0; x < pointCount; x++)
            {
                int peakStart = x * peaks.Length / pointCount;
                int peakEnd = Math.Max(
                    peakStart + 1,
                    (x + 1) * peaks.Length / pointCount);
                float amplitude = 0;

                for (int peakIndex = peakStart;
                     peakIndex < peakEnd && peakIndex < peaks.Length;
                     peakIndex++)
                {
                    amplitude = Math.Max(amplitude, peaks[peakIndex]);
                }

                amplitudes[x] = amplitude * halfHeight;
            }

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                Point GetTopPoint(int index) => new(
                    index * width / (pointCount - 1),
                    centerY - amplitudes[index]);
                context.BeginFigure(
                    GetTopPoint(0),
                    isFilled: true,
                    isClosed: true);

                // Convert a Catmull-Rom spline to cubic Bezier segments. Each
                // original amplitude point remains on the curve, preserving detail.
                for (int index = 0; index < pointCount - 1; index++)
                {
                    Point point0 = GetTopPoint(Math.Max(0, index - 1));
                    Point point1 = GetTopPoint(index);
                    Point point2 = GetTopPoint(index + 1);
                    Point point3 = GetTopPoint(Math.Min(pointCount - 1, index + 2));
                    Point control1 = new(
                        point1.X + (point2.X - point0.X) / 6,
                        point1.Y + (point2.Y - point0.Y) / 6);
                    Point control2 = new(
                        point2.X - (point3.X - point1.X) / 6,
                        point2.Y - (point3.Y - point1.Y) / 6);
                    context.BezierTo(
                        control1,
                        control2,
                        point2,
                        isStroked: true,
                        isSmoothJoin: false);
                }

                context.LineTo(
                    new Point(width, centerY),
                    isStroked: true,
                    isSmoothJoin: false);
                context.LineTo(
                    new Point(0, centerY),
                    isStroked: true,
                    isSmoothJoin: false);
            }

            geometry.Freeze();

            const int supersamplingScale = 3;
            var drawingVisual = new DrawingVisual();
            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                drawingContext.DrawGeometry(
                    new SolidColorBrush(Color.FromArgb(18, 0xF3, 0xF3, 0xCF)),
                    null,
                    geometry);

                Brush outlineBrush = TryFindResource("CenterHeaderTextColor") as Brush
                    ?? new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xCF));
                outlineBrush = outlineBrush.Clone();
                outlineBrush.Opacity *= 0.75;
                var outlinePen = new Pen(outlineBrush, 1)
                {
                    LineJoin = PenLineJoin.Round
                };
                drawingContext.DrawGeometry(null, outlinePen, geometry);
            }

            var renderedWaveform = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(width * supersamplingScale)),
                Math.Max(1, (int)Math.Ceiling(height * supersamplingScale)),
                96 * supersamplingScale,
                96 * supersamplingScale,
                PixelFormats.Pbgra32);
            renderedWaveform.Render(drawingVisual);
            renderedWaveform.Freeze();
            WaveformImage.Source = renderedWaveform;

            var revealClip = new RectangleGeometry(new Rect(0, 0, width, height));
            WaveformDrawingSurface.Clip = revealClip;

            if (animate)
            {
                DurationValueGrid.Visibility = Visibility.Collapsed;
                var revealAnimation = new RectAnimation
                {
                    From = new Rect(0, 0, 0, height),
                    To = new Rect(0, 0, width, height),
                    Duration = TimeSpan.FromSeconds(0.96),
                    FillBehavior = FillBehavior.HoldEnd
                };
                revealAnimation.Completed += (_, _) =>
                {
                    if (!_analysisClosed && displayVersion == _waveformDisplayVersion &&
                        !string.IsNullOrEmpty(HoveredTrackDuration))
                        DurationValueGrid.Visibility = Visibility.Visible;
                };
                revealClip.BeginAnimation(
                    RectangleGeometry.RectProperty,
                    revealAnimation,
                    HandoffBehavior.SnapshotAndReplace);
            }
            else if (!string.IsNullOrEmpty(HoveredTrackDuration))
            {
                DurationValueGrid.Visibility = Visibility.Visible;
            }
        }

        private static async Task<WaveformData?> LoadOrGenerateWaveformPeaksAsync(
            string audioPath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(audioPath))
                return null;

            string cacheDirectory = Path.Combine(GamePaths.Resolve(AppContext.BaseDirectory).Cache, "waveforms");
            Directory.CreateDirectory(cacheDirectory);
            var audioFile = new FileInfo(audioPath);
            string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"waveform-v3|{audioFile.FullName}|{audioFile.Length}|{audioFile.LastWriteTimeUtc.Ticks}")));
            string cachePath = Path.Combine(cacheDirectory, cacheKey + ".peaks");

            if (File.Exists(cachePath))
            {
                byte[] cachedBytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                if (cachedBytes.Length == (WaveformPeakCount + 1) * sizeof(float))
                {
                    var cachedPeaks = new float[WaveformPeakCount];
                    double durationSeconds = BitConverter.ToSingle(cachedBytes, 0);
                    Buffer.BlockCopy(cachedBytes, sizeof(float), cachedPeaks, 0,
                        WaveformPeakCount * sizeof(float));
                    return new WaveformData(cachedPeaks, durationSeconds);
                }
            }

            DecodedAudio? decoded = await DecodeToMonoPcmAsync(audioPath, cancellationToken);
            if (decoded == null || decoded.Pcm.Length < sizeof(short))
                return null;

            float[] peaks = CalculateWaveformPeaks(decoded.Pcm, cancellationToken);
            var peakBytes = new byte[(peaks.Length + 1) * sizeof(float)];
            Buffer.BlockCopy(BitConverter.GetBytes((float)decoded.DurationSeconds), 0,
                peakBytes, 0, sizeof(float));
            Buffer.BlockCopy(peaks, 0, peakBytes, sizeof(float),
                peaks.Length * sizeof(float));
            await File.WriteAllBytesAsync(cachePath, peakBytes, cancellationToken);
            return new WaveformData(peaks, decoded.DurationSeconds);
        }

        private static float[] CalculateWaveformPeaks(byte[] pcm, CancellationToken cancellationToken)
        {
            int sampleCount = pcm.Length / sizeof(short);
            var peaks = new float[WaveformPeakCount];

            for (int peakIndex = 0; peakIndex < peaks.Length; peakIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int sampleStart = (int)((long)peakIndex * sampleCount / peaks.Length);
                int sampleEnd = Math.Max(
                    sampleStart + 1,
                    (int)((long)(peakIndex + 1) * sampleCount / peaks.Length));
                int maximum = 0;

                for (int sampleIndex = sampleStart;
                     sampleIndex < sampleEnd && sampleIndex < sampleCount;
                     sampleIndex++)
                {
                    int byteIndex = sampleIndex * sizeof(short);
                    short sample = (short)(pcm[byteIndex] | (pcm[byteIndex + 1] << 8));
                    maximum = Math.Max(maximum, Math.Abs((int)sample));
                }

                peaks[peakIndex] = Math.Min(1f, maximum / 32768f);
            }

            return peaks;
        }

        private static async Task<DecodedAudio?> DecodeToMonoPcmAsync(
            string audioPath,
            CancellationToken cancellationToken)
        {
            bool isBank = string.Equals(
                Path.GetExtension(audioPath),
                ".bank",
                StringComparison.OrdinalIgnoreCase);

            if (isBank)
                return await DecodeBankToPcmAsync(audioPath, cancellationToken);

            string? ffmpegPath = FindAudioTool("ffmpeg.exe", "ffmpeg");

            if (ffmpegPath == null)
            {
                AppLog.Warn("FFmpeg was not found beside the app, under tools, or on PATH.");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using Process ffmpeg = StartPcmDecoder(ffmpegPath, audioPath);
            var decoderTasks = new List<Task>();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                TryKill(ffmpeg));

            try
            {
                Task<string> ffmpegErrors = ffmpeg.StandardError.ReadToEndAsync(cancellationToken);
                decoderTasks.Add(ffmpegErrors);
                using var output = new MemoryStream();
                Task pcmCopy = ffmpeg.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
                decoderTasks.Add(pcmCopy);

                await pcmCopy;
                await ffmpeg.WaitForExitAsync(cancellationToken);

                await ffmpegErrors;

                if (ffmpeg.ExitCode != 0)
                {
                    AppLog.Warn(
                        $"FFmpeg exited with code {ffmpeg.ExitCode}: {await ffmpegErrors}");
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                byte[] pcm = output.ToArray();
                return new DecodedAudio(pcm, pcm.Length / (12000.0 * sizeof(short)));
            }
            finally
            {
                await FinishAnalysisDecoderAsync(ffmpeg, decoderTasks.ToArray());
            }
        }

        private static async Task<DecodedAudio?> DecodeBankToPcmAsync(
            string audioPath,
            CancellationToken cancellationToken)
        {
            string? decoderPath = FindAudioTool("vgmstream-cli.exe", "vgmstream-cli");
            if (decoderPath == null)
            {
                AppLog.Warn("vgmstream-cli was not found beside the app, under tools, or on PATH.");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using Process decoder = StartVgmstream(decoderPath, audioPath);
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => TryKill(decoder));
            Task<string> errors = decoder.StandardError.ReadToEndAsync(cancellationToken);
            using var waveOutput = new MemoryStream();
            Task audioCopy = decoder.StandardOutput.BaseStream.CopyToAsync(
                waveOutput,
                cancellationToken);
            try
            {
                await audioCopy;
                await decoder.WaitForExitAsync(cancellationToken);
                await errors;
            }
            finally
            {
                await FinishAnalysisDecoderAsync(decoder, audioCopy, errors);
            }

            if (decoder.ExitCode != 0)
            {
                AppLog.Warn(
                    $"vgmstream-cli exited with code {decoder.ExitCode}: {await errors}");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[]? pcm = ExtractPcm16WaveData(
                waveOutput.ToArray(),
                out double durationSeconds);
            if (pcm == null)
                AppLog.Warn("vgmstream output was not a supported PCM16 WAV stream.");

            return pcm == null ? null : new DecodedAudio(pcm, durationSeconds);
        }

        private static byte[]? ExtractPcm16WaveData(
            byte[] wave,
            out double durationSeconds)
        {
            durationSeconds = 0;
            if (wave.Length < 12 ||
                wave[0] != (byte)'R' || wave[1] != (byte)'I' ||
                wave[2] != (byte)'F' || wave[3] != (byte)'F' ||
                wave[8] != (byte)'W' || wave[9] != (byte)'A' ||
                wave[10] != (byte)'V' || wave[11] != (byte)'E')
            {
                return null;
            }

            bool isPcm16 = false;
            int byteRate = 0;
            int offset = 12;

            while (offset + 8 <= wave.Length)
            {
                int chunkSize = wave[offset + 4] |
                                (wave[offset + 5] << 8) |
                                (wave[offset + 6] << 16) |
                                (wave[offset + 7] << 24);
                int chunkData = offset + 8;

                if (chunkSize < 0 || chunkData > wave.Length)
                    return null;

                bool isFormatChunk =
                    wave[offset] == (byte)'f' && wave[offset + 1] == (byte)'m' &&
                    wave[offset + 2] == (byte)'t' && wave[offset + 3] == (byte)' ';
                bool isDataChunk =
                    wave[offset] == (byte)'d' && wave[offset + 1] == (byte)'a' &&
                    wave[offset + 2] == (byte)'t' && wave[offset + 3] == (byte)'a';

                if (isFormatChunk && chunkSize >= 16 && chunkData + 16 <= wave.Length)
                {
                    int format = wave[chunkData] | (wave[chunkData + 1] << 8);
                    byteRate = wave[chunkData + 8] |
                               (wave[chunkData + 9] << 8) |
                               (wave[chunkData + 10] << 16) |
                               (wave[chunkData + 11] << 24);
                    int bitsPerSample = wave[chunkData + 14] | (wave[chunkData + 15] << 8);
                    isPcm16 = format == 1 && bitsPerSample == 16;
                }
                else if (isDataChunk && isPcm16)
                {
                    int availableSize = Math.Min(chunkSize, wave.Length - chunkData);
                    var pcm = new byte[availableSize];
                    Buffer.BlockCopy(wave, chunkData, pcm, 0, availableSize);
                    if (byteRate > 0)
                        durationSeconds = availableSize / (double)byteRate;
                    return pcm;
                }

                long nextOffset = (long)chunkData + chunkSize + (chunkSize & 1);
                if (nextOffset <= offset || nextOffset > wave.Length)
                    break;

                offset = (int)nextOffset;
            }

            return null;
        }

        private static Process StartPcmDecoder(string executablePath, string audioPath)
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(audioPath);
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("12000");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("s16le");
            startInfo.ArgumentList.Add("-acodec");
            startInfo.ArgumentList.Add("pcm_s16le");
            startInfo.ArgumentList.Add("pipe:1");
            return Process.Start(startInfo)!;
        }

        private static Process StartVgmstream(string executablePath, string audioPath)
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(audioPath);
            return Process.Start(startInfo)!;
        }

        private static string? FindAudioTool(string windowsName, string commandName)
        {
            string[] localCandidates =
            {
                Path.Combine(GamePaths.Resolve(AppContext.BaseDirectory).Tools, windowsName),
                Path.Combine(AppContext.BaseDirectory, windowsName)
            };

            foreach (string candidate in localCandidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                string windowsCandidate = Path.Combine(directory.Trim(), windowsName);
                if (File.Exists(windowsCandidate))
                    return windowsCandidate;

                string commandCandidate = Path.Combine(directory.Trim(), commandName);
                if (File.Exists(commandCandidate))
                    return commandCandidate;
            }

            return null;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the state check and termination.
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception || exception is AggregateException)
            {
                // One failed termination must not prevent other requests cancelling.
                AppLog.Warn($"Decoder termination failed: {exception}");
            }
        }

        private static async Task FinishAnalysisDecoderAsync(Process process, params Task[] pipeTasks)
        {
            try
            {
                TryKill(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            finally
            {
                // A failed/cancelled await must not strand another redirected stream task.
                try
                {
                    await Task.WhenAll(pipeTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The request already owns cancellation handling.
                }
                catch (Exception exception)
                {
                    AppLog.Warn($"Decoder stream cleanup: {exception}");
                }
            }
        }

    }
}
