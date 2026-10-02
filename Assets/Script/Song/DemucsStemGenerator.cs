using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Localization;
using YARG.Menu.Persistent;
using YARG.Settings;

namespace YARG.Song
{
    public static class DemucsStemGenerator
    {
        private const long MinimumMixBytes = 4096;

        private static readonly object Gate = new();
        private static bool _running;
        private static volatile bool _stop;
        private static Process _active;
        private static int _launcher = -1;

        public static bool IsRunning
        {
            get
            {
                lock (Gate)
                {
                    return _running;
                }
            }
        }

        public static void Stop()
        {
            Process active;
            lock (Gate)
            {
                if (!_running)
                {
                    ToastManager.ToastInformation(Localize.Key("Menu.Toast.StemsNotRunning"));
                    return;
                }

                _stop = true;
                active = _active;
            }

            TryKill(active);
            ToastManager.ToastInformation(Localize.Key("Menu.Toast.StemsStopping"));
        }

        private static bool IsStopRequested()
        {
            return _stop;
        }

        private static void Attach(Process process)
        {
            lock (Gate)
            {
                _active = process;
                if (_stop)
                {
                    TryKill(process);
                }
            }
        }

        private static void Detach(Process process)
        {
            lock (Gate)
            {
                if (ReferenceEquals(_active, process))
                {
                    _active = null;
                }
            }
        }

        private static void TryKill(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                // /T stops python children started by the demucs launcher, which are the process using the GPU.
                var killer = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /T /PID " + process.Id,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                if (killer.Start())
                {
                    killer.WaitForExit(3000);
                }

                killer.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
            }
        }

        public static void StartAllMissing(Action onFinished)
        {
            var pending = new List<SongEntry>();
            var songs = SongContainer.UnfilteredSongs;
            if (songs != null)
            {
                foreach (var song in songs)
                {
                    if (song != null && song.GetStemSeparation() == StemSeparation.Missing)
                    {
                        pending.Add(song);
                    }
                }
            }

            lock (Gate)
            {
                if (_running)
                {
                    ToastManager.ToastInformation(Localize.Key("Menu.Toast.StemsBusy"));
                    return;
                }

                _running = true;
                _stop = false;
            }

            ToastManager.ToastInformation(Localize.Key("Menu.Toast.StemsChecking"));
            Task.Run(() =>
            {
                int generated = 0;
                int failed = 0;
                var report = true;
                var stopped = false;
                try
                {
                    StemSilenceScanner.ProbeBlocking(songs, IsStopRequested);
                    if (IsStopRequested())
                    {
                        stopped = true;
                    }
                    else
                    {
                        pending.Clear();
                        if (songs != null)
                        {
                            foreach (var song in songs)
                            {
                                if (song != null && song.GetStemSeparation() == StemSeparation.Missing)
                                {
                                    pending.Add(song);
                                }
                            }
                        }
                    }

                    if (!stopped && pending.Count == 0)
                    {
                        report = false;
                        UnityMainThreadCallback.QueueEvent(() =>
                        {
                            ToastManager.ToastInformation(Localize.Key("Menu.Toast.StemsNoneMissing"));
                        });
                        return;
                    }

                    if (!stopped)
                    {
                        UnityMainThreadCallback.QueueEvent(() =>
                        {
                            ToastManager.ToastInformation(Localize.KeyFormat("Menu.Toast.StemsGeneratingAll", pending.Count));
                        });
                    }

                    foreach (var song in pending)
                    {
                        if (stopped || IsStopRequested())
                        {
                            stopped = true;
                            break;
                        }

                        if (song.HasInstalledDemucsStems())
                        {
                            continue;
                        }

                        if (song.GetStemSeparation() != StemSeparation.Missing)
                        {
                            continue;
                        }

                        string label = SongLabel(song);
                        UnityMainThreadCallback.QueueEvent(() =>
                        {
                            ToastManager.ToastInformation(Localize.KeyFormat("Menu.Toast.StemsGenerating", label));
                        });

                        string error;
                        if (Execute(song, out error))
                        {
                            song.InvalidateStemSeparation();
                            generated++;
                        }
                        else if (IsStopRequested())
                        {
                            stopped = true;
                            break;
                        }
                        else
                        {
                            failed++;
                            string summary = SummarizeError(error);
                            YargLogger.LogError("Demucs stem generation failed for " + label + ": " + summary);
                            UnityMainThreadCallback.QueueEvent(() =>
                            {
                                ToastManager.ToastError(Localize.KeyFormat("Menu.Toast.StemsFailed", label + ": " + summary));
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (IsStopRequested())
                    {
                        stopped = true;
                    }
                    else
                    {
                        failed++;
                        YargLogger.LogException(ex, "Demucs stem generation failed");
                    }
                }
                finally
                {
                    var finished = onFinished;
                    var generatedCount = generated;
                    var failedCount = failed;
                    var shouldReport = report;
                    var wasStopped = stopped;
                    UnityMainThreadCallback.QueueEvent(() =>
                    {
                        lock (Gate)
                        {
                            _running = false;
                            _stop = false;
                        }

                        if (wasStopped)
                        {
                            ToastManager.ToastInformation(Localize.KeyFormat("Menu.Toast.StemsStopped", generatedCount));
                            if (generatedCount > 0)
                            {
                                finished?.Invoke();
                            }

                            return;
                        }

                        if (!shouldReport)
                        {
                            return;
                        }

                        if (generatedCount == 0 && failedCount == 0)
                        {
                            ToastManager.ToastInformation(Localize.Key("Menu.Toast.StemsNoneMissing"));
                            return;
                        }

                        if (failedCount == 0)
                        {
                            ToastManager.ToastSuccess(Localize.KeyFormat("Menu.Toast.StemsGeneratedAll", generatedCount));
                        }
                        else
                        {
                            ToastManager.ToastError(Localize.KeyFormat("Menu.Toast.StemsGeneratedPartial", generatedCount, failedCount));
                        }

                        finished?.Invoke();
                    });
                }
            });
        }

        public static void Start(SongEntry song, Action onFinished)
        {
            lock (Gate)
            {
                if (_running)
                {
                    ToastManager.ToastInformation(Localize.Key("Menu.Toast.StemsBusy"));
                    return;
                }

                if (song.GetStemSeparation() == StemSeparation.Present)
                {
                    ToastManager.ToastInformation(Localize.Key("Menu.Toast.StemsAlreadyPresent"));
                    return;
                }

                _running = true;
                _stop = false;
            }

            ToastManager.ToastInformation(Localize.KeyFormat("Menu.Toast.StemsGenerating", SongLabel(song)));
            Task.Run(() =>
            {
                string error = null;
                var succeeded = false;
                try
                {
                    succeeded = Execute(song, out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    YargLogger.LogException(ex, "Demucs stem generation failed");
                }
                finally
                {
                    var finished = onFinished;
                    var message = error;
                    var ok = succeeded;
                    var wasStopped = IsStopRequested();
                    if (!ok && !wasStopped)
                    {
                        message = SummarizeError(message);
                        YargLogger.LogError("Demucs stem generation failed for " + SongLabel(song) + ": " + message);
                    }
                    UnityMainThreadCallback.QueueEvent(() =>
                    {
                        lock (Gate)
                        {
                            _running = false;
                            _stop = false;
                        }

                        if (wasStopped)
                        {
                            ToastManager.ToastInformation(Localize.KeyFormat("Menu.Toast.StemsStopped", ok ? 1 : 0));
                            if (ok)
                            {
                                song.InvalidateStemSeparation();
                                finished?.Invoke();
                            }

                            return;
                        }

                        if (ok)
                        {
                            song.InvalidateStemSeparation();
                            ToastManager.ToastSuccess(Localize.Key("Menu.Toast.StemsGenerated"));
                            finished?.Invoke();
                        }
                        else
                        {
                            ToastManager.ToastError(Localize.KeyFormat("Menu.Toast.StemsFailed", message ?? "Unknown error"));
                        }
                    });
                }
            });
        }

        private static bool Execute(SongEntry song, out string error)
        {
            string temp = Path.Combine(Path.GetTempPath(), "yarg-demucs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                error = null;
                if (IsStopRequested())
                {
                    return false;
                }

                if (!song.TryExportFullMix(Path.Combine(temp, "mix"), out var mixPath))
                {
                    error = "Could not find a full mix in the song stem.";
                    return false;
                }

                if (IsStopRequested())
                {
                    return false;
                }

                if (!TryMakeStereoMix(song, mixPath, Path.Combine(temp, "stereo.wav"), out var stereoPath, out error))
                {
                    return false;
                }

                if (IsStopRequested())
                {
                    return false;
                }

                string outDir = Path.Combine(temp, "out");
                if (!RunDemucs(stereoPath, outDir, out error))
                {
                    return false;
                }

                if (IsStopRequested())
                {
                    return false;
                }

                string stemDir = FindSeparatedDirectory(outDir);
                if (stemDir == null)
                {
                    error = "Demucs did not write vocals, bass, drums, and other.";
                    return false;
                }

                if (!song.TryInstallDemucsStems(
                    Path.Combine(stemDir, "vocals.wav"),
                    Path.Combine(stemDir, "bass.wav"),
                    Path.Combine(stemDir, "drums.wav"),
                    Path.Combine(stemDir, "other.wav")))
                {
                    error = "Could not save the generated stems.";
                    return false;
                }

                error = null;
                return true;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temp))
                    {
                        Directory.Delete(temp, true);
                    }
                }
                catch (Exception ex)
                {
                    YargLogger.LogException(ex, "Failed to delete Demucs temp files");
                }
            }
        }

        private static bool TryMakeStereoMix(SongEntry song, string mixPath, string destination, out string stereoPath, out string error)
        {
            stereoPath = destination;
            // Demucs keeps only the first two channels of a packed mogg. On a CON those are the
            // silent drum pair, and a silent file makes its pad check throw AssertionError.
            bool songStemOnly = song.TryGetSongStemChannels(out var channels) && channels.Length > 0;
            string filterArgs = songStemOnly
                ? "-af " + Quote(BuildPanFilter(channels)) + " "
                : "-ac 2 ";
            if (!RunFfmpeg(mixPath, destination, filterArgs, out error))
            {
                return false;
            }

            if (WavHasEnergy(destination))
            {
                error = null;
                return true;
            }

            // The leftover song channels were silent. The mix is in another stem, so downmix every channel.
            if (songStemOnly && RunFfmpeg(mixPath, destination, "-ac 2 ", out error) && WavHasEnergy(destination))
            {
                YargLogger.LogFormatInfo("Song stem was silent; Demucs will use the full mix for {0}", SongLabel(song));
                error = null;
                return true;
            }

            error = "The song mix is silent, so Demucs cannot separate it.";
            return false;
        }

        private static bool RunFfmpeg(string mixPath, string destination, string filterArgs, out string error)
        {
            var start = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-y -hide_banner -loglevel error -i " + Quote(mixPath) + " " + filterArgs + "-ar 44100 " + Quote(destination),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            Process process;
            try
            {
                process = new Process { StartInfo = start };
                if (!process.Start())
                {
                    error = "ffmpeg did not start.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is FileNotFoundException)
            {
                error = "ffmpeg was not found. It is required to prepare the mix.";
                return false;
            }

            var stderrTask = Task.Run(() => ReadTail(process.StandardError));
            var stdoutTask = Task.Run(() => ReadTail(process.StandardOutput));
            Attach(process);
            process.WaitForExit();
            string stderr = stderrTask.Result;
            stdoutTask.Wait();
            int exitCode = process.ExitCode;
            Detach(process);
            process.Dispose();

            if (IsStopRequested())
            {
                error = null;
                return false;
            }

            if (exitCode == 0 && File.Exists(destination) && new FileInfo(destination).Length > MinimumMixBytes)
            {
                error = null;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                YargLogger.LogError("ffmpeg failed to prepare the Demucs mix: " + stderr.Trim());
            }

            error = "Could not convert the mix to stereo.";
            return false;
        }

        private static string BuildPanFilter(int[] channels)
        {
            if (channels.Length == 1)
            {
                string channel = "c" + channels[0];
                return "pan=stereo|c0=" + channel + "|c1=" + channel;
            }

            if (channels.Length == 2)
            {
                return "pan=stereo|c0=c" + channels[0] + "|c1=c" + channels[1];
            }

            var left = new StringBuilder();
            var right = new StringBuilder();
            int leftCount = 0;
            int rightCount = 0;
            for (int i = 0; i < channels.Length; i++)
            {
                bool toLeft = i % 2 == 0;
                var side = toLeft ? left : right;
                if (toLeft)
                {
                    if (leftCount > 0)
                    {
                        side.Append('+');
                    }

                    leftCount++;
                }
                else
                {
                    if (rightCount > 0)
                    {
                        side.Append('+');
                    }

                    rightCount++;
                }

                side.Append('c').Append(channels[i]);
            }

            return "pan=stereo|c0=" + left + "|c1=" + right;
        }

        private static string SummarizeError(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "Unknown error";
            }

            if (text.IndexOf("CUDA out of memory", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "CUDA out of memory";
            }

            string last = null;
            string exceptionLine = null;
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || IsTraceNoise(line))
                {
                    continue;
                }

                last = line;
                if (IsExceptionLine(line))
                {
                    exceptionLine = line;
                }
            }

            if (exceptionLine == null && text.IndexOf("AssertionError", StringComparison.Ordinal) >= 0)
            {
                exceptionLine = "AssertionError";
            }

            string chosen = exceptionLine ?? last ?? text.Trim();
            return chosen.Length > 180 ? chosen.Substring(0, 180) : chosen;
        }

        private static bool IsTraceNoise(string line)
        {
            if (line.StartsWith("File ", StringComparison.Ordinal) ||
                line.StartsWith("Traceback", StringComparison.Ordinal) ||
                line.StartsWith("During handling", StringComparison.Ordinal) ||
                line.IndexOf("site-packages", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            bool marker = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '^' || c == '~')
                {
                    marker = true;
                    continue;
                }

                if (c != ' ')
                {
                    return false;
                }
            }

            return marker;
        }

        private static bool IsExceptionLine(string line)
        {
            int colon = line.IndexOf(':');
            string head = (colon > 0 ? line.Substring(0, colon) : line).Trim();
            int dot = head.LastIndexOf('.');
            if (dot >= 0 && dot < head.Length - 1)
            {
                head = head.Substring(dot + 1);
            }

            return head.EndsWith("Error", StringComparison.Ordinal)
                || head.EndsWith("Exception", StringComparison.Ordinal);
        }

        private static bool RunDemucs(string mixPath, string outDir, out string error)
        {
            Directory.CreateDirectory(outDir);
            int[] order = _launcher >= 0 ? new[] { _launcher } : new[] { 0, 1, 2 };
            string lastError = "demucs was not found. Install it and make sure it is on PATH.";
            foreach (int launcher in order)
            {
                Process process;
                try
                {
                    process = CreateProcess(launcher, mixPath, outDir);
                    if (!process.Start())
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is FileNotFoundException)
                {
                    lastError = ex.Message;
                    if (_launcher == launcher)
                    {
                        _launcher = -1;
                        return RunDemucs(mixPath, outDir, out error);
                    }

                    continue;
                }

                var stderrTask = Task.Run(() => ReadTail(process.StandardError));
                var stdoutTask = Task.Run(() => ReadTail(process.StandardOutput));
                Attach(process);
                process.WaitForExit();
                string stderr = stderrTask.Result;
                string stdout = stdoutTask.Result;
                int exitCode = process.ExitCode;
                Detach(process);
                process.Dispose();

                if (IsStopRequested())
                {
                    error = null;
                    return false;
                }

                if (exitCode == 0 && FindSeparatedDirectory(outDir) != null)
                {
                    _launcher = launcher;
                    error = null;
                    return true;
                }

                string output = CombineDemucsOutput(stderr, stdout);
                lastError = output.Length > 0 ? output : "demucs exited with code " + exitCode + ".";
                if (IsMissingDemucs(lastError))
                {
                    if (_launcher == launcher)
                    {
                        _launcher = -1;
                        return RunDemucs(mixPath, outDir, out error);
                    }

                    continue;
                }

                error = lastError;
                return false;
            }

            error = lastError;
            return false;
        }

        private static bool IsMissingDemucs(string error)
        {
            return error.IndexOf("No module named demucs", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Process CreateProcess(int launcher, string mixPath, string outDir)
        {
            var start = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            string demucsArgs = "--shifts=" + DemucsShifts() + " -o " + Quote(outDir) + " " + Quote(mixPath);
            switch (launcher)
            {
                case 0:
                    start.FileName = "demucs";
                    start.Arguments = demucsArgs;
                    break;
                case 1:
                    start.FileName = "py";
                    start.Arguments = "-m demucs " + demucsArgs;
                    break;
                default:
                    start.FileName = "python";
                    start.Arguments = "-m demucs " + demucsArgs;
                    break;
            }

            return new Process { StartInfo = start };
        }

        private static int DemucsShifts()
        {
            int shifts = 1;
            if (SettingsManager.Settings != null)
            {
                shifts = (int) Math.Round(SettingsManager.Settings.DemucsShifts.Value);
            }

            if (shifts < 1)
            {
                return 1;
            }

            return shifts > 8 ? 8 : shifts;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string FindSeparatedDirectory(string outDir)
        {
            if (!Directory.Exists(outDir))
            {
                return null;
            }

            foreach (var directory in Directory.EnumerateDirectories(outDir, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(directory, "vocals.wav")) &&
                    File.Exists(Path.Combine(directory, "bass.wav")) &&
                    File.Exists(Path.Combine(directory, "drums.wav")) &&
                    File.Exists(Path.Combine(directory, "other.wav")))
                {
                    return directory;
                }
            }

            return null;
        }

        private static string SongLabel(SongEntry song)
        {
            string artist = song.Artist.ToString();
            string name = song.Name.ToString();
            if (string.IsNullOrWhiteSpace(artist))
            {
                return name;
            }

            return artist + " - " + name;
        }

        private static string ReadTail(StreamReader reader)
        {
            var tail = new StringBuilder();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                tail.AppendLine(line);
                if (tail.Length > 16000)
                {
                    tail.Remove(0, tail.Length - 12000);
                }
            }

            return tail.ToString();
        }

        private static string CombineDemucsOutput(string stderr, string stdout)
        {
            bool hasError = !string.IsNullOrWhiteSpace(stderr);
            bool hasOut = !string.IsNullOrWhiteSpace(stdout);
            if (hasError && hasOut)
            {
                return stderr.Trim() + "\n" + stdout.Trim();
            }

            if (hasError)
            {
                return stderr.Trim();
            }

            return hasOut ? stdout.Trim() : string.Empty;
        }

        private static bool WavHasEnergy(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var header = new byte[12];
                    if (stream.Read(header, 0, 12) < 12 ||
                        header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F')
                    {
                        return false;
                    }

                    int bits = 16;
                    int format = 1;
                    var id = new byte[4];
                    var sizeBytes = new byte[4];
                    while (stream.Position + 8 <= stream.Length)
                    {
                        if (stream.Read(id, 0, 4) < 4 || stream.Read(sizeBytes, 0, 4) < 4)
                        {
                            break;
                        }

                        long size = (uint) (sizeBytes[0] | (sizeBytes[1] << 8) | (sizeBytes[2] << 16) | (sizeBytes[3] << 24));
                        long start = stream.Position;
                        if (id[0] == 'f' && id[1] == 'm' && id[2] == 't' && id[3] == ' ')
                        {
                            var fmt = new byte[size < 16 ? (int) size : 16];
                            if (fmt.Length >= 16 && stream.Read(fmt, 0, fmt.Length) >= 16)
                            {
                                format = fmt[0] | (fmt[1] << 8);
                                bits = fmt[14] | (fmt[15] << 8);
                            }
                        }
                        else if (id[0] == 'd' && id[1] == 'a' && id[2] == 't' && id[3] == 'a')
                        {
                            return PcmHasEnergy(stream, size, format, bits);
                        }

                        long next = start + size + (size & 1);
                        if (next < start || next > stream.Length)
                        {
                            break;
                        }

                        stream.Position = next;
                    }
                }
            }
            catch (Exception)
            {
                return true;
            }

            return false;
        }

        private static bool PcmHasEnergy(Stream stream, long size, int format, int bits)
        {
            const float silent = 0.001f;
            if (size < 2)
            {
                return false;
            }

            var buffer = new byte[8192];
            long remaining = size;
            if (format == 1 && bits == 16)
            {
                while (remaining > 1)
                {
                    int want = remaining > buffer.Length ? buffer.Length : (int) remaining;
                    int read = stream.Read(buffer, 0, want);
                    if (read <= 1)
                    {
                        break;
                    }

                    remaining -= read;
                    for (int i = 0; i + 1 < read; i += 2)
                    {
                        short sample = (short) (buffer[i] | (buffer[i + 1] << 8));
                        int abs = sample == short.MinValue ? 32768 : Math.Abs(sample);
                        if (abs >= 33)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            if (format == 3 && bits == 32)
            {
                while (remaining > 3)
                {
                    int want = remaining > buffer.Length ? buffer.Length : (int) remaining;
                    int read = stream.Read(buffer, 0, want);
                    if (read < 4)
                    {
                        break;
                    }

                    remaining -= read;
                    for (int i = 0; i + 3 < read; i += 4)
                    {
                        float sample = BitConverter.ToSingle(buffer, i);
                        if (sample > silent || sample < -silent)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            return true;
        }
    }
}
