using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TvStationAssistant
{
    class Program
    {

        static string[] file_extensions = new string[] { ".3g2", ".3gp", ".asf", ".avi", ".flv", ".h264", ".m2t", ".m2ts", ".m4a", ".m4v", ".mkv", ".mod", ".mov", ".mp3", ".ogg", "wav", ".mp4", ".mpg", ".png", ".tod", ".ts", ".vob", ".webm", ".wmv" };
        static string ffmpeg_location = "";
        static string temp_folder = "";
        static bool use_nvidia = false;
        static string nvenc_preset = "fast"; // fallback default
        private static string Log_File = "";

        static bool Normalizing = false;

        static bool DetectNvidiaEncoder()
        {
            if (string.IsNullOrEmpty(ffmpeg_location) || !File.Exists(ffmpeg_location))
                return false;

            // 1. Try modern SDK presets (p4)
            if (TestNvencPreset("p4"))
            {
                nvenc_preset = "p4";
                return true;
            }

            // 2. Try legacy SDK presets (fast)
            if (TestNvencPreset("fast"))
            {
                nvenc_preset = "fast";
                return true;
            }

            // 3. Neither worked or no NVIDIA GPU/driver available
            return false;
        }

        static string PromptSetting(string label, string currentValue)
        { //done
            Console.WriteLine();
            Console.WriteLine();
            if (!string.IsNullOrEmpty(currentValue))
            {
                Console.WriteLine($"{label} [Current: {currentValue}]");
                Console.WriteLine("(Press Enter to keep current, or type a new path)");
            }
            else
            {
                Console.WriteLine(label);
            }
            Console.Write("> ");

            string input = Console.ReadLine()?.Trim();
            return string.IsNullOrEmpty(input) ? currentValue : input;
        }        

        static bool TestNvencPreset(string presetName)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ffmpeg_location,
                    Arguments = $"-y -f lavfi -i color=c=black:s=64x64:d=0.04 -c:v h264_nvenc -preset {presetName} -f null -",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    proc.StandardError.ReadToEnd(); // drain buffer
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        static List<string> GetFiles(string folder, string[] ext_filter = null, string[] name_filter = null)
        { //done
            List<string> str = new List<string>();

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return str;

            foreach (string file in Directory.EnumerateFiles(folder, "*.*"))
            {
                // 1. Check Extension Filter (whitelist: must match at least one if provided)
                if (ext_filter != null && ext_filter.Length > 0)
                {
                    string fe = Path.GetExtension(file);
                    bool matchExt = false;
                    foreach (string ext in ext_filter)
                    {
                        if (string.Equals(fe, ext, StringComparison.OrdinalIgnoreCase))
                        {
                            matchExt = true;
                            break;
                        }
                    }

                    if (!matchExt) continue;
                }

                // 2. Check Name Filter (blacklist: skip if contains any forbidden substring)
                if (name_filter != null && name_filter.Length > 0)
                {
                    string fn = Path.GetFileNameWithoutExtension(file);
                    bool forbidden = false;
                    foreach (string badStr in name_filter)
                    {
                        if (fn.IndexOf(badStr, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            forbidden = true;
                            break;
                        }
                    }

                    if (forbidden) continue;
                }

                // Passed all checks
                str.Add(file);
            }

            return str;
        }

        static TimeSpan[] NormalizeAudio(string file, bool Verbose = false)
        { //done
            Normalizing = true;
            if (Verbose) Console.WriteLine("Normalizing has begun!");
            TimeSpan[] ret = { TimeSpan.Zero, TimeSpan.Zero };

            string pad = "_NA_";
            string path = Path.GetDirectoryName(file);
            string fileName = Path.GetFileNameWithoutExtension(file);
            string extName = Path.GetExtension(file);
            string targetPath = Path.Combine(path, fileName + pad + extName);

            if (Verbose) Console.WriteLine("Checking if file exists " + targetPath);
            if (File.Exists(targetPath) || file.Contains(pad))
            {
                if (Verbose) Console.WriteLine("File exists skipping");
                AddLog("File Exists, skipping normalization");
                Normalizing = false;
                return ret;
            }

            // ==========================================
            // PASS 1: Loudness Analysis
            // ==========================================
            Console.WriteLine("Generating Filter...");
            if (!Verbose) Console.CursorVisible = false;

            DateTime start = DateTime.Now;
            TimeSpan totalDuration = TimeSpan.Zero;

            string input_i = "", input_lra = "", input_tp = "", input_thresh = "", input_offset = "";

            // -vn and -sn drop video/subs decode so analysis runs dramatically faster.
            // -f null - replaces /dev/null to run properly on modern Windows binaries.
            ProcessStartInfo psiPass1 = new ProcessStartInfo
            {
                FileName = ffmpeg_location,
                Arguments = $"-y -i \"{file}\" -vn -sn -af loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json -f null -",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process proc = Process.Start(psiPass1))
            {
                if (proc == null)
                {
                    Console.WriteLine("Error starting FFmpeg");
                    if (!Verbose) Console.CursorVisible = true;
                    Normalizing = false;
                    return ret;
                }

                string line;
                while ((line = proc.StandardError.ReadLine()) != null)
                {
                    // Duration parsing
                    if (line.Contains("Duration") && !line.Contains("Segment"))
                    {
                        int a = line.IndexOf("Duration");
                        int b = line.IndexOf(",", a + 1);
                        if (a >= 0 && b > a)
                        {
                            string durStr = line.Substring(a + 10, b - a - 10).Trim();
                            TimeSpan.TryParse(durStr, out totalDuration);
                        }
                    }

                    // Progress Bar render
                    RenderConsoleProgress(line, totalDuration);

                    // Parameter capture
                    ExtractJsonParam(line, "\"input_i\" : \"", ref input_i, "input_i");
                    ExtractJsonParam(line, "\"input_lra\" : \"", ref input_lra, "input_lra");
                    ExtractJsonParam(line, "\"input_tp\" : \"", ref input_tp, "input_tp");
                    ExtractJsonParam(line, "\"input_thresh\" : \"", ref input_thresh, "input_thresh");
                    ExtractJsonParam(line, "\"target_offset\" : \"", ref input_offset, "input_offset");
                }
                proc.WaitForExit();
            }

            try { Console.SetCursorPosition(0, Console.CursorTop + 2); } catch { }

            TimeSpan pass1Duration = DateTime.Now - start;
            ret[0] = pass1Duration;
            Console.WriteLine("Finished Generating Filter. Took " + pass1Duration);
            Console.WriteLine();

            // ==========================================
            // PASS 2: Applying Audio Filter
            // ==========================================
            Console.WriteLine("Applying audio filter...");
            start = DateTime.Now;

            string filter = $"loudnorm=I=-16:TP=-1.5:LRA=11:measured_I={input_i}:measured_LRA={input_lra}:measured_TP={input_tp}:measured_thresh={input_thresh}:offset={input_offset}:linear=true:print_format=summary";
            string mp3Args = file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? " -map_metadata 0 -id3v2_version 3 -write_id3v1 1 " : " ";

            ProcessStartInfo psiPass2 = new ProcessStartInfo
            {
                FileName = ffmpeg_location,
                Arguments = $"-y -i \"{file}\" -vcodec copy -af {filter}{mp3Args}\"{targetPath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process proc = Process.Start(psiPass2))
            {
                if (proc == null)
                {
                    Console.WriteLine("Error starting FFmpeg pass 2");
                    if (!Verbose) Console.CursorVisible = true;
                    Normalizing = false;
                    return ret;
                }

                string line;
                while ((line = proc.StandardError.ReadLine()) != null)
                {
                    if (line.Contains("Duration") && !line.Contains("Segment"))
                    {
                        int a = line.IndexOf("Duration");
                        int b = line.IndexOf(",", a + 1);
                        if (a >= 0 && b > a)
                        {
                            string durStr = line.Substring(a + 10, b - a - 10).Trim();
                            TimeSpan.TryParse(durStr, out totalDuration);
                        }
                    }

                    RenderConsoleProgress(line, totalDuration);
                }
                proc.WaitForExit();
            }

            try { Console.SetCursorPosition(0, Console.CursorTop + 1); } catch { }

            TimeSpan pass2Duration = DateTime.Now - start;
            ret[1] = pass2Duration;
            Console.WriteLine();
            Console.WriteLine("Finished Applying Filter. Took " + pass2Duration);
            Console.WriteLine();

            // Sidecar commercials rename
            string commSrc = file + ".commercials";
            string commDst = targetPath + ".commercials";
            if (File.Exists(commSrc))
            {
                Console.WriteLine("\nRenamed commercials file!");
                File.SetAttributes(commSrc, FileAttributes.Normal);
                File.Move(commSrc, commDst);
            }

            // Safely delete original only if target exists
            if (File.Exists(file) && File.Exists(targetPath))
            {
                Console.WriteLine("\nDeleted original file!");
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            Console.WriteLine();
            if (!Verbose) Console.CursorVisible = true;
            Normalizing = false;
            return ret;
        }

        // Helper to keep the ASCII progress bar logic tidy and re-usable across passes
        static void RenderConsoleProgress(string line, TimeSpan totalDuration)
        { //done
            if (totalDuration.TotalSeconds <= 0 || !line.Contains("time=")) return;

            try
            {
                int a = line.IndexOf("time=");
                int b = line.IndexOf(" ", a + 1);
                if (a >= 0 && b > a)
                {
                    string timeStr = line.Substring(a + 5, b - a - 5).Trim();
                    if (TimeSpan.TryParse(timeStr, out TimeSpan currentTime))
                    {
                        int percent = (int)((currentTime.TotalSeconds / totalDuration.TotalSeconds) * 100);
                        percent = Math.Max(0, Math.Min(100, percent));

                        int filled = Math.Min(50, (int)Math.Floor(percent / 2.0));
                        string bar = new string('█', filled).PadRight(50, '░');

                        Console.WriteLine(bar);
                        try { Console.SetCursorPosition(0, Console.CursorTop - 1); } catch { }
                    }
                }
            }
            catch { }
        }

        // Helper to extract JSON string fields from loudnorm's print_format=json output
        static void ExtractJsonParam(string line, string tag, ref string outputVar, string logName)
        { //done
            int a = line.IndexOf(tag);
            if (a >= 0 && !line.Contains("Segment"))
            {
                int b = line.IndexOf("\"", a + tag.Length);
                if (b > a)
                {
                    outputVar = line.Substring(a + tag.Length, b - a - tag.Length);
                    AddLog($"Found {logName}: {outputVar}");
                }
            }
        }

        static void AddTimeStringToFileName(string file)
        { //done
            const string padL = "%T(";
            const string padR = ")%";

            if (file.Contains(padL) && file.Contains(padR))
            {
                Console.WriteLine("Time already added, skipping!");
                return;
            }

            string path = Path.GetDirectoryName(file);
            string fileName = Path.GetFileNameWithoutExtension(file);
            string extName = Path.GetExtension(file);

            Console.Write($"Getting video length: {fileName} ... ");
            fileName = ReturnCleanASCII(fileName);

            TimeSpan duration = getVideoDuration(file);
            Console.WriteLine(duration.ToString());

            string pad = $"{padL}{Math.Round(duration.TotalSeconds)}{padR}";
            string newFileName = $"{fileName}{pad}{extName}";
            string targetPath = Path.Combine(path, newFileName);

            if (File.Exists(targetPath))
            {
                Console.WriteLine("File Exists, skipping!");
                return;
            }

            int retryCount = 0;
            while (retryCount <= 15)
            {
                try
                {
                    Console.Write("Renaming file... ");
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Move(file, targetPath);

                    if (File.Exists(targetPath))
                        break;
                }
                catch
                {
                    Console.Write($"{retryCount}... ");
                    System.Threading.Thread.Sleep(1000);
                }

                retryCount++;
            }

            // Rename matching .commercials sidecar if present
            string commSrc = file + ".commercials";
            string commDst = targetPath + ".commercials";
            if (File.Exists(commSrc))
            {
                Console.WriteLine();
                Console.WriteLine("Renamed commercials file!");
                File.SetAttributes(commSrc, FileAttributes.Normal);
                try
                {
                    File.Move(commSrc, commDst);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }

            Console.WriteLine("Done!");
            Console.WriteLine();
        }

        static void checkSplits(string in_path, string out_path, double threshhold, double black_level)
        { //done
            if (!Directory.Exists(in_path))
            {
                Console.WriteLine("Input directory does not exist: " + in_path);
                return;
            }

            if (!Directory.Exists(out_path))
            {
                Directory.CreateDirectory(out_path);
            }

            Random rnd = new Random();
            List<string> files = GetFiles(in_path, file_extensions);

            string vcodec = use_nvidia
                ? $"-c:v h264_nvenc -preset {nvenc_preset} -profile:v high -level 3.1 -rc vbr -cq 22 -b:v 0 -maxrate 4M -bufsize 4M -g 60 -bf 0"
                : "-c:v libx264 -preset fast -profile:v high -level 3.1 -crf 23 -maxrate 4M -bufsize 4M -g 60 -bf 2";

            string vf = "yadif=0:-1:1,scale=640:480,setsar=1,setdar=4/3,fps=29.97";

            Stopwatch batchTimer = Stopwatch.StartNew();

            for (int fileIdx = 0; fileIdx < files.Count; fileIdx++)
            {
                string file = files[fileIdx];
                Console.WriteLine($"\n[{fileIdx + 1}/{files.Count}] Processing: {Path.GetFileName(file)}");
                AddLog("Scanning compilation for individual ads: " + file);

                Stopwatch fileTimer = Stopwatch.StartNew();

                List<TimeSpan> breaks = scanForCommercialBreaks(
                    file,
                    threshhold,
                    wait: 0,
                    min_time_add: 12,
                    min_end_time: 5,
                    black_level: black_level,
                    addStartEnd: true
                );

                if (breaks.Count < 2)
                {
                    Console.WriteLine("No ad transitions detected in: " + Path.GetFileName(file));
                    continue;
                }

                int totalCuts = breaks.Count - 1;
                Console.WriteLine($"Found {totalCuts} cuts.");

                Stopwatch splitTimer = Stopwatch.StartNew();
                int processedCuts = 0;

                for (int i = 1; i <= totalCuts; i++)
                {
                    TimeSpan start = breaks[i - 1];
                    TimeSpan rawLength = breaks[i] - start;

                    TimeSpan length = rawLength > TimeSpan.FromMilliseconds(500)
                        ? rawLength - TimeSpan.FromMilliseconds(333)
                        : rawLength;

                    if (length.TotalSeconds < 3.0)
                    {
                        continue;
                    }

                    string startStr = $"{start.Hours:D2}:{start.Minutes:D2}:{start.Seconds:D2}.{start.Milliseconds:D3}";
                    string lengthStr = $"{length.Hours:D2}:{length.Minutes:D2}:{length.Seconds:D2}.{length.Milliseconds:D3}";

                    string baseName = Path.GetFileNameWithoutExtension(file);
                    string outputName = $"{baseName}_{i:D3}_{rnd.Next(1, 999):D3}.mp4";
                    string targetPath = Path.Combine(out_path, outputName);

                    // Format ETA string based on completed segments in this file
                    string etaString = "Estimating...";
                    if (processedCuts > 0)
                    {
                        double msPerCut = splitTimer.Elapsed.TotalMilliseconds / processedCuts;
                        int remainingCuts = totalCuts - i + 1;
                        TimeSpan eta = TimeSpan.FromMilliseconds(msPerCut * remainingCuts);
                        etaString = $"~{eta.Minutes:D2}m {eta.Seconds:D2}s remaining";
                    }

                    TimeSpan fileElapsed = fileTimer.Elapsed;
                    Console.Write($"\rExtracting cut {i}/{totalCuts} ({Math.Round(length.TotalSeconds)}s) | Elapsed: {fileElapsed.Minutes:D2}:{fileElapsed.Seconds:D2} | ETA: {etaString}   ");

                    AddLog($"Exporting video #{i}: Start {startStr}, Duration {lengthStr} -> {outputName}");

                    string args = $"-y -ss {startStr} -i \"{file}\" -t {lengthStr} {vcodec} " +
                                  $"-pix_fmt yuv420p -vsync cfr -vf \"{vf}\" " +
                                  $"-c:a aac -b:a 128k -ar 48000 -ac 2 " +
                                  $"-af \"aresample=async=1:first_pts=0\" " +
                                  $"-movflags +faststart \"{targetPath}\"";

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = ffmpeg_location,
                        Arguments = args,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process proc = Process.Start(psi))
                    {
                        if (proc == null) continue;
                        proc.StandardError.ReadToEnd();
                        proc.WaitForExit();
                    }

                    processedCuts++;
                }

                fileTimer.Stop();
                Console.WriteLine($"\nFinished file in {fileTimer.Elapsed.Minutes}m {fileTimer.Elapsed.Seconds}s.");
            }

            batchTimer.Stop();
            Console.WriteLine($"\nCompilation splitting complete. Total batch runtime: {batchTimer.Elapsed.Hours}h {batchTimer.Elapsed.Minutes}m {batchTimer.Elapsed.Seconds}s.");
        }

        static TimeSpan getVideoDuration(string file)
        { //done
            AddLog("Getting Duration " + file);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ffmpeg_location,
                Arguments = $"-i \"{file}\" -f null -",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    Console.WriteLine("Error starting FFmpeg");
                    return TimeSpan.Zero;
                }

                string line;
                while ((line = proc.StandardError.ReadLine()) != null)
                {
                    int a = line.IndexOf("Duration");
                    int xx = line.IndexOf("Durations");

                    if (a >= 0 && xx == -1)
                    {
                        int b = line.IndexOf(",", a + 1);
                        if (b > a + 10)
                        {
                            string dur = line.Substring(a + 10, b - a - 10).Trim();
                            AddLog("Found length " + dur);

                            // Execute battle-tested instant kill
                            try
                            {
                                if (!proc.HasExited)
                                    proc.Kill();
                            }
                            catch { }

                            if (TimeSpan.TryParse(dur, out TimeSpan parsedSpan))
                            {
                                return parsedSpan;
                            }
                        }
                    }
                }
            }

            return TimeSpan.Zero;
        }

        static void MoveTaggedFiles(string dir)
        { //done
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Console.WriteLine("Directory does not exist: " + dir);
                return;
            }

            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "%AM%", "am" },
                { "%PM%", "pm" },
                { "%ANY%", "any" }
            };

            Console.WriteLine("Searching for tagged files in: " + dir);

            foreach (string file in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(file);

                foreach (var kvp in tags)
                {
                    if (name.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string targetDir = Path.Combine(dir, kvp.Value);
                        Directory.CreateDirectory(targetDir);

                        string destPath = Path.Combine(targetDir, name);

                        if (File.Exists(destPath))
                        {
                            Console.WriteLine($"Destination exists, skipping: {destPath}");
                            break;
                        }

                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Move(file, destPath);
                            Console.WriteLine($"Moved: {name} ==> {kvp.Value}\\");

                            // Move matching .commercials sidecar if it exists
                            string sidecar = file + ".commercials";
                            string sidecarDest = destPath + ".commercials";
                            if (File.Exists(sidecar))
                            {
                                File.SetAttributes(sidecar, FileAttributes.Normal);
                                File.Move(sidecar, sidecarDest);
                                Console.WriteLine($"Moved sidecar: {Path.GetFileName(sidecar)} ==> {kvp.Value}\\");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to move {name}: {ex.Message}");
                        }

                        break;
                    }
                }
            }

            Console.WriteLine("Finished moving tagged files.\n");
        }

        static List<TimeSpan> scanForCommercialBreaks(string file, double threshhold, double wait, int min_time_add = 300, int min_end_time = 59, double black_level = 0.05, bool addStartEnd = true)
        { //done
            TimeSpan vid_dur = getVideoDuration(file);

            // Invariant formatting guarantees '.' is used for decimals regardless of Windows locale
            string sThresh = threshhold.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string sBlack = black_level.ToString(System.Globalization.CultureInfo.InvariantCulture);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ffmpeg_location,
                Arguments = $"-i \"{file}\" -vf \"blackdetect=d={sThresh}:pix_th={sBlack}\" -an -f null -",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            AddLog("scanForCommercialBreaks:" + psi.Arguments);

            List<TimeSpan> nums = new List<TimeSpan>();
            if (addStartEnd) nums.Add(TimeSpan.FromSeconds(0));

            using (Process proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    AddLog("Error starting FFmpeg process");
                    if (addStartEnd) nums.Add(TimeSpan.FromSeconds(vid_dur.TotalSeconds));
                    return nums;
                }

                AddLog("Scanning for Commercial Breaks...");
                DateTime now = DateTime.Now;
                double last_break = 0;
                string line;

                while ((line = proc.StandardError.ReadLine()) != null)
                {
                    if ((DateTime.Now - now) > TimeSpan.FromSeconds(0.25))
                    {
                        Console.Write(".");
                        now = DateTime.Now;
                    }

                    if (line.Contains("blackdetect"))
                    {
                        int a = line.IndexOf("black_start:");
                        int b = line.IndexOf(" ", a + 1);
                        if (a == -1 || b <= a + 12) continue;

                        string sStart = line.Substring(a + 12, b - a - 12);

                        a = line.IndexOf("black_end:", b + 1);
                        b = line.IndexOf(" ", a + 1);
                        if (a == -1 || b <= a + 10) continue;

                        string sEnd = line.Substring(a + 10, b - a - 10);

                        a = line.IndexOf("black_duration:", b + 1);
                        if (a == -1 || line.Length <= a + 15) continue;

                        string sDur = line.Substring(a + 15);

                        if (!double.TryParse(sStart, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double bstart) ||
                            !double.TryParse(sEnd, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double bend) ||
                            !double.TryParse(sDur, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double bdur))
                        {
                            continue;
                        }

                        double bmed = bstart + (bdur / 2.0);

                        // Exact original timing checks and logic branches
                        if (bmed > wait && ((vid_dur.TotalSeconds - bmed) > min_end_time))
                        {
                            if ((bmed - last_break) > min_time_add)
                            {
                                nums.Add(TimeSpan.FromSeconds(bmed));
                                last_break = bmed;
                                Console.Write("+" + TimeSpan.FromSeconds(bmed).ToString());
                            }
                            else if (nums.Count == 0)
                            {
                                nums.Add(TimeSpan.FromSeconds(bmed));
                                last_break = bmed;
                                Console.Write("+" + TimeSpan.FromSeconds(bmed).ToString());
                            }
                            else
                            {
                                Console.Write("x");
                            }
                        }
                    }
                }

                try
                {
                    if (!proc.HasExited) proc.Kill();
                }
                catch { }
            }

            Console.WriteLine();
            AddLog("Finished scan..... found " + nums.Count + " breaks");

            if (addStartEnd) nums.Add(TimeSpan.FromSeconds(vid_dur.TotalSeconds));
            return nums;
        }

        static void ResetLog()
        { //done
            if (string.IsNullOrEmpty(Log_File)) return;

            try
            {
                if (!Directory.Exists(Log_File))
                {
                    Directory.CreateDirectory(Log_File);
                }

                string logPath = Path.Combine(Log_File, "log.txt");
                File.WriteAllText(logPath, string.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error resetting log file: " + ex.Message);
            }
        }

        static void AddLog(string log)
        { //done
            if (string.IsNullOrEmpty(Log_File)) return;

            try
            {
                string logPath = Path.Combine(Log_File, "log.txt");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}    {log}{Environment.NewLine}");
            }
            catch
            {
                // Don't let a logging lockup crash the video pipeline
            }
        }

        static void drawScreen(int which_one)
        {
            Console.Clear();
            //check and convert any non-mpg video to mpg
            switch (which_one)
            {
                case 1:
                    Console.WriteLine(@"  _   _                            _ _         ");
                    Console.WriteLine(@" | \ | |                          | (_)        ");
                    Console.WriteLine(@" |  \| | ___  _ __ _ __ ___   __ _| |_ _______ ");
                    Console.WriteLine(@" | . ` |/ _ \| '__| '_ ` _ \ / _` | | |_  / _ \");
                    Console.WriteLine(@" | |\  | (_) | |  | | | | | | (_| | | |/ /  __/");
                    Console.WriteLine(@" |_| \_|\___/|_|  |_| |_| |_|\__,_|_|_/___\___|");
                    break;
                case 2:
                    Console.Clear();
                    Console.WriteLine("  _____      _       _     ____                 _        ");
                    Console.WriteLine(" |  __ \\    (_)     | |   |  _ \\               | |       ");
                    Console.WriteLine(" | |__) | __ _ _ __ | |_  | |_) |_ __ ___  __ _| | _____ ");
                    Console.WriteLine(" |  ___/ '__| | '_ \\| __| |  _ <| '__/ _ \\/ _` | |/ / __|");
                    Console.WriteLine(" | |   | |  | | | | | |_  | |_) | | |  __/ (_| |   <\\__ \\");
                    Console.WriteLine(" |_|   |_|  |_|_| |_|\\__| |____/|_|  \\___|\\__,_|_|\\_\\___/");
                    break;
                case 3:
                    Console.WriteLine(@"  _________  _     ____ ______      __ __ ____ ___     ___  ___  ");
                    Console.WriteLine(@" / ___/    \| |   |    |      |    |  |  |    |   \   /  _]/   \ ");
                    Console.WriteLine(@"(   \_|  o  ) |    |  ||      |    |  |  ||  ||    \ /  [_|     |");
                    Console.WriteLine(@" \__  |   _/| |___ |  ||_|  |_|    |  |  ||  ||  D  |    _]  O  |");
                    Console.WriteLine(@" /  \ |  |  |     ||  |  |  |      |  :  ||  ||     |   [_|     |");
                    Console.WriteLine(@" \    |  |  |     ||  |  |  |       \   / |  ||     |     |     |");
                    Console.WriteLine(@"  \___|__|  |_____|____| |__|        \_/ |____|_____|_____|\___/ ");
                    break;
                case 5:
                    Console.WriteLine(@" __  __  _____  _  _  ____    ____   __    ___   ___  ____  ____     ____  ____  __    ____  ___ ");
                    Console.WriteLine(@"(  \/  )(  _  )( \/ )( ___)  (_  _) /__\  / __) / __)( ___)(  _ \   ( ___)(_  _)(  )  ( ___)/ __)");
                    Console.WriteLine(@" )    (  )(_)(  \  /  )__)     )(  /(__)\( (_-.( (_-. )__)  )(_) )   )__)  _)(_  )(__  )__) \__ \");
                    Console.WriteLine(@"(_/\/\_)(_____)  \/  (____)   (__)(__)(__)\___/ \___/(____)(____/   (__)  (____)(____)(____)(___/");
                    break;
                case 6:
                    Console.Clear();
                    Console.WriteLine("  _____      _       _     ____                 _        ");
                    Console.WriteLine(" |  __ \\    (_)     | |   |  _ \\               | |       ");
                    Console.WriteLine(" | |__) | __ _ _ __ | |_  | |_) |_ __ ___  __ _| | _____ ");
                    Console.WriteLine(" |  ___/ '__| | '_ \\| __| |  _ <| '__/ _ \\/ _` | |/ / __|");
                    Console.WriteLine(" | |   | |  | | | | | |_  | |_) | | |  __/ (_| |   <\\__ \\");
                    Console.WriteLine(" |_|   |_|  |_|_| |_|\\__| |____/|_|  \\___|\\__,_|_|\\_\\___/");
                    Console.WriteLine(" ------------------------------------------------------------- ");
                    Console.WriteLine(" | TEST                                                      |");
                    Console.WriteLine(" ------------------------------------------------------------- ");
                    break;
                case 7:
                    Console.WriteLine(@"  _____              __  __ U _____ u   _     ____    ____ U _____ u  ____     ");
                    Console.WriteLine(@" |_ - _|    ___    U|' \/ '|\| ___-|U  /-\  u|  _-\  |  _-\\| ___-|U |  _-\ u  ");
                    Console.WriteLine(@"   | |     |_-_|   \| |\/| |/|  _|-  \/ _ \//| | | |/| | | ||  _|-  \| |_) |/  ");
                    Console.WriteLine(@"  /| |\     | |     | |  | | | |___  / ___ \U| |_| |U| |_| || |___   |  _ <    ");
                    Console.WriteLine(@" u |_|U   U/| |\u   |_|  |_| |_____|/_/   \_\|____/ u|____/ |_____|  |_| \_\   ");
                    Console.WriteLine(@" _// \\.-,_|___|_,-<<,-,,-.  <<   >> \\    >> |||_    |||_  <<   >>  //   \\_  ");
                    Console.WriteLine(@"(__) (__\_)-' '-(_/ (./  \.)(__) (__(__)  (__(__)_)  (__)_)(__) (__)(__)  (__)");
                    break;
                case 9:
                    Console.WriteLine(@"    )    (               (         )       )   (     ");
                    Console.WriteLine(@" ( /(    )\ )    *   )   )\ )   ( /(    ( /(   )\ )  ");
                    Console.WriteLine(@" )\())  (()/(  ` )  /(  (()/(   )\())   )\()) (()/(  ");
                    Console.WriteLine(@"((_)\    /(_))  ( )(_))  /(_)) ((_)\   ((_)\   /(_)) ");
                    Console.WriteLine(@"  ((_)  (_))   (_(_())  (_))     ((_)   _((_) (_))   ");
                    Console.WriteLine(@" / _ \  | _ \  |_   _|  |_ _|   / _ \  | \| | / __|  ");
                    Console.WriteLine(@"| (_) | |  _/    | |     | |   | (_) | | .` | \__ \  ");
                    Console.WriteLine(@" \___/  |_|      |_|    |___|   \___/  |_|\_| |___/  ");
                    break;
                case 'n':
                    Console.WriteLine(@"Remove Normaliztion mark from filename");
                    break;
                case 'l':
                    Console.WriteLine(@"Replace string with string in filename");
                    break;
                case 'k':
                    Console.WriteLine(@"Add string to end of filename");
                    break;
                case 'm':
                    Console.WriteLine(@"Add Normaliztion mark to filename");
                    break;
                case 't':
                    Console.WriteLine(@"Remove Timestamp from filename");
                    break;
                case 'u':
                    Console.WriteLine(@"Clean Ascii filenames");
                    break;
                default:
                    var version = "0.4.4";
                    Console.WriteLine(@" _____ _    _   ____  _        _   _              ");
                    Console.WriteLine(@"|_   _\ \  / / / ___|| |_ __ _| |_(_) ___  _ __   ");
                    Console.WriteLine(@"  | |  \ \/ /  \___ \| __/ _` | __| |/ _ \| '_ \  ");
                    Console.WriteLine(@"  | |   \  /    ___) | || (_| | |_| | (_) | | | | ");
                    Console.WriteLine(@"  |_|    \/    |____/ \__\__,_|\__|_|\___/|_| |_| ");
                    Console.WriteLine(@"    _             _     _                         ");
                    Console.WriteLine(@"   / \   ___ ___ (_)___| |_ __ _ _ __  _|_        ");
                    Console.WriteLine(@"  / _ \ / __/ __|| / __| __/ _` | '_ \| __|       ");
                    Console.WriteLine(@" / ___ \\__ \__ \| \__ \ || (_| | | | | |_        ");
                    Console.WriteLine(@"/_/   \_\___/___/|_|___/\__\__,_|_| |_|\__|       ");
                    Console.WriteLine("version: " + version.ToString());
                    Console.WriteLine("");
                    Console.WriteLine("██████████████████████████████████████████████████████████████");
                    Console.WriteLine("█                                                            █");
                    Console.WriteLine("█ Options:                                                   █");
                    Console.WriteLine("█                                                            █");
                    Console.WriteLine("█   [ 1 ] - Batch Normalize Audio   [ n ] Remove NA Mark     █");
                    Console.WriteLine("█   [ 2 ] - Print Breaks            [ u ] Remove Non-Ascii   █");
                    Console.WriteLine("█   [ 3 ] - Split Video             [ l ] Replace String     █");
                    Console.WriteLine("█   [ 5 ] - Move Tagged Files                                █");
                    Console.WriteLine("█   [ 6 ] - Test Print Breaks       [ k ] Add to Filename    █");
                    Console.WriteLine("█   [ 7 ] - Time Adder              [ t ] Remove Time        █");
                    Console.WriteLine("█                                                            █");
                    Console.WriteLine("█   [ 9 ] - Options                                          █");
                    Console.WriteLine("█                                                            █");
                    Console.WriteLine("██████████████████████████████████████████████████████████████");
                    break;
            }
            Console.WriteLine();
            Console.WriteLine();
        }

        static void drawMessage(string message)
        {
            Console.Clear();
            Console.WriteLine(@" _______  ___      _______  ______    _______ ");
            Console.WriteLine(@"|   _   ||   |    |       ||    _ |  |       |");
            Console.WriteLine(@"|  |_|  ||   |    |    ___||   | ||  |_     _|");
            Console.WriteLine(@"|       ||   |    |   |___ |   |_||_   |   |  ");
            Console.WriteLine(@"|       ||   |___ |    ___||    __  |  |   |  ");
            Console.WriteLine(@"|   _   ||       ||   |___ |   |  | |  |   |  ");
            Console.WriteLine(@"|__| |__||_______||_______||___|  |_|  |___|  ");
            Console.WriteLine("");
            Console.WriteLine("*****************************************************************");
            string[] lines = message.Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
            foreach (string msg in lines)
            {
                Console.WriteLine(">>>>>     " + msg);
            }
            Console.WriteLine("*****************************************************************");
            Console.WriteLine("");
            Console.WriteLine("Press a key to continue!");
            Console.ReadKey();
        }

        static void recurse_add_duration(string dir)
        { //done
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Console.WriteLine("Directory does not exist: " + dir);
                return;
            }

            RecurseAddDurationInternal(dir);

            Console.WriteLine("Times have been added. Press any key to continue...");
            Console.ReadKey(true);
        }

        static void RecurseAddDurationInternal(string currentDir)
        { //done
            // 1. Process all video files in this folder
            List<string> vidlen_files = GetFiles(currentDir, file_extensions);
            foreach (string file in vidlen_files)
            {
                AddTimeStringToFileName(file);
            }

            // 2. Step strictly one level down to avoid duplicate traversal
            try
            {
                string[] subDirs = Directory.GetDirectories(currentDir, "*", SearchOption.TopDirectoryOnly);
                foreach (string subDir in subDirs)
                {
                    RecurseAddDurationInternal(subDir);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Safely skip folders with restricted OS permissions
            }
        }

        static string ReturnCleanASCII(string s)
        { //done
            if (string.IsNullOrEmpty(s)) return string.Empty;

            // Optional: Normalize common fancy unicode punctuation to standard ASCII equivalents
            s = s.Replace('’', '\'')
                 .Replace('‘', '\'')
                 .Replace('“', '"')
                 .Replace('”', '"')
                 .Replace('—', '-')
                 .Replace('–', '-');

            StringBuilder sb = new StringBuilder(s.Length);
            char[] invalidChars = Path.GetInvalidFileNameChars();

            foreach (char c in s)
            {
                // Strip non-ASCII or control characters
                if (c < 32 || c > 126) continue;

                // Strip illegal filename characters (includes '?', '<', '>', ':', '"', '/', '\', '|', '*')
                if (Array.IndexOf(invalidChars, c) >= 0) continue;

                sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        static void LoadSettings(string settingsFile)
        { //done

            if (!File.Exists(settingsFile)) return;

            string[] lines = File.ReadAllLines(settingsFile);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                string[] opt = line.Split(new[] { '=' }, 2);
                if (opt.Length < 2) continue;

                string key = opt[0].Trim().ToLower();
                string val = opt[1].Trim();

                switch (key)
                {
                    case "ffmpeg location":
                        Console.WriteLine("Setting FFMPEG location to " + val);
                        ffmpeg_location = val;
                        break;

                    case "temp folder":
                        Console.WriteLine("Setting Temp Folder to " + val);
                        temp_folder = val;
                        break;

                    case "log file":
                        Console.WriteLine("Setting Log File location to " + val);
                        Log_File = val;
                        break;

                    case "use nvidia":
                        use_nvidia = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        Console.WriteLine("Setting NVIDIA Hardware Acceleration to " + (use_nvidia ? "ENABLED" : "DISABLED"));
                        break;
                }
            }
        }

        static bool HandleCommandLineArguments(string[] args)
        {
            if (args == null || args.Length == 0)
                return false;

            bool cliHandled = false;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string flag = args[i].ToLower().Trim();

                    if (flag == "--normalize" || flag == "-n")
                    {
                        cliHandled = true;

                        if (i + 1 >= args.Length)
                        {
                            Console.WriteLine("Error: Missing file path after " + args[i]);
                            break;
                        }

                        string filePath = args[i + 1];

                        if (File.Exists(filePath))
                        {
                            if (filePath.IndexOf("_NA_", StringComparison.OrdinalIgnoreCase) > -1)
                            {
                                Console.WriteLine(filePath + " has already been normalized.");
                            }
                            else
                            {
                                Console.WriteLine("Normalizing file: " + filePath);
                                NormalizeAudio(filePath, true);

                                while (Normalizing)
                                {
                                    Console.WriteLine("Normalizing...");
                                    System.Threading.Thread.Sleep(3000);
                                }

                                Console.WriteLine("Normalization complete.");
                            }
                        }
                        else
                        {
                            Console.WriteLine(filePath + " does not exist.");
                        }

                        i++; // Skip the file path on next loop iteration
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("CLI Error: " + ex.Message);
                return true;
            }

            return cliHandled;
        }

        static string PromptPath(string prompt, bool mustExist = true, bool isFile = false)
        {
            Console.WriteLine(prompt);
            Console.Write("> ");
            string input = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(input)) return "";

            // Strip quotation marks if user dragged and dropped a folder/file into the terminal
            input = input.Trim('"', '\'');

            if (mustExist)
            {
                bool exists = isFile ? File.Exists(input) : Directory.Exists(input);
                if (!exists)
                {
                    drawMessage((isFile ? "File" : "Path") + " does not exist: " + input);
                    return null; // Signals invalid input
                }
            }

            return input;
        }

        static double PromptDouble(string prompt, double defaultValue)
        {
            Console.WriteLine();
            Console.WriteLine($"{prompt} [Default: {defaultValue}]");
            Console.Write("> ");
            string input = Console.ReadLine()?.Trim() ?? "";

            if (double.TryParse(input, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                return val;

            return defaultValue;
        }

        static int PromptInt(string prompt, int defaultValue)
        {
            Console.WriteLine();
            Console.WriteLine($"{prompt} [Default: {defaultValue}]");
            Console.Write("> ");
            string input = Console.ReadLine()?.Trim() ?? "";

            if (int.TryParse(input, out int val))
                return val;

            return defaultValue;
        }

        static bool PromptBool(string prompt, bool defaultVal = false)
        {
            Console.WriteLine();
            Console.WriteLine($"{prompt} ([Y]es / [N]o) [Default: {(defaultVal ? "Y" : "N")}]");
            Console.Write("> ");
            string input = Console.ReadLine()?.Trim().ToLower() ?? "";

            if (string.IsNullOrEmpty(input)) return defaultVal;
            return input.StartsWith("y");
        }

        static void ProcessAudioNormalizationMenu()
        {
            drawScreen(1);

            List<string> paths = new List<string>();
            bool addPaths = true;

            while (addPaths)
            {
                addPaths = false;
                string folder = PromptPath("Enter path to video files (leave blank to finish queue, prefix with '+' to add more):", false);
                if (string.IsNullOrEmpty(folder)) break;

                if (folder.StartsWith("+"))
                {
                    folder = folder.Substring(1).Trim('"', '\'');
                    addPaths = true;
                }

                if (!Directory.Exists(folder))
                {
                    drawMessage("Path does not exist: " + folder);
                }
                else
                {
                    paths.Add(folder);
                    Console.WriteLine($"\n{folder} added to queue ({paths.Count} total)\n");
                }
            }

            if (paths.Count == 0) return;

            TimeSpan totalFilter = TimeSpan.Zero;
            TimeSpan totalApply = TimeSpan.Zero;

            foreach (string folder in paths)
            {
                List<string> files = GetFiles(folder, file_extensions, new[] { "_NA_" });
                int totalFiles = files.Count;
                int processed = 0;

                for (int i = 0; i < files.Count; i++)
                {
                    string file = files[i];
                    drawScreen(1);

                    TimeSpan elapsed = totalFilter + totalApply;
                    double avgSeconds = processed > 0 ? elapsed.TotalSeconds / processed : 0;
                    TimeSpan eta = TimeSpan.FromSeconds(avgSeconds * (totalFiles - processed));

                    Console.WriteLine($"ETA: {eta:hh\\:mm\\:ss} | Elapsed: {elapsed:hh\\:mm\\:ss}\n");
                    Console.WriteLine($"Normalizing audio: {Path.GetFileName(file)}");
                    Console.WriteLine($"File {i + 1} of {totalFiles}\n");

                    TimeSpan[] runTimes = NormalizeAudio(file);
                    totalFilter += runTimes[0];
                    totalApply += runTimes[1];

                    if (runTimes[0].TotalSeconds > 0) processed++;
                }
            }

            TimeSpan totalNorm = totalFilter + totalApply;
            drawMessage($"Normalization complete!\nTotal runtime: {totalNorm:hh\\:mm\\:ss}");
        }

        static void ProcessPrintBreaksMenu()
        {
            drawScreen(2);
            string printFolder = PromptPath("Enter path to video files: (leave blank to cancel)");
            if (string.IsNullOrEmpty(printFolder)) return;

            List<string> breakFiles = GetFiles(printFolder, file_extensions);
            if (breakFiles.Count == 0)
            {
                drawMessage("No matching video files found in directory.");
                return;
            }

            double pThresh = PromptDouble("Enter threshold (length of black frames in seconds):", 0.5);
            double pBlack = PromptDouble("Enter black luminance (pure=0, black=0.05, light black=0.1):", 0.05);
            int pMinComm = PromptInt("Enter minimum detected breaks required to write file:", 3);
            double pWait = PromptDouble("Enter minimum start time (seconds from beginning):", 0.0);
            int pMinBetween = PromptInt("Enter minimum length between breaks (seconds):", 300);
            int pEndPad = PromptInt("Enter time from end of video to stop looking (seconds):", 0);
            bool synthBreaks = PromptBool("Fabricate synthetic breaks if none are detected?", false);

            foreach (string file in breakFiles)
            {
                string commercialFile = Path.Combine(printFolder, Path.GetFileName(file) + ".commercials");
                if (File.Exists(commercialFile))
                {
                    AddLog("Commercials metadata already exists for: " + Path.GetFileName(file));
                    continue;
                }

                Console.WriteLine($"\nScanning: {Path.GetFileName(file)}");
                List<TimeSpan> cms = scanForCommercialBreaks(file, pThresh, pWait, pMinBetween, pEndPad, pBlack, false);

                if (cms.Count >= pMinComm)
                {
                    string fileData = string.Join("\n", cms.ConvertAll(t => t.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    File.WriteAllText(commercialFile, fileData);
                    AddLog("Printed breaks: " + commercialFile);
                    Console.WriteLine($"Wrote {cms.Count} breaks.");
                }
                else if (synthBreaks)
                {
                    Console.WriteLine("Insufficient breaks found. Generating synthetic intervals...");
                    TimeSpan dur = getVideoDuration(file);
                    if (dur.TotalSeconds > 600)
                    {
                        List<string> synth = new List<string>();
                        for (int s = 600; s < dur.TotalSeconds; s += 600)
                            synth.Add(s.ToString());

                        if (synth.Count > 0)
                            File.WriteAllText(commercialFile, string.Join("\n", synth));
                    }
                }
                else
                {
                    Console.WriteLine($"Found {cms.Count} breaks (under threshold of {pMinComm}). Skipped.");
                }
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey(true);
            drawMessage("Print Breaks complete!");
        }

        static void ProcessSplitVideoMenu()
        {
            drawScreen(3);
            string splitIn = PromptPath("Enter path to input video files:");
            if (string.IsNullOrEmpty(splitIn)) return;

            Console.WriteLine("\nEnter output path (leave blank for default '\\output\\'):");
            Console.Write("> ");
            string splitOut = Console.ReadLine()?.Trim('"', '\'') ?? "";

            if (string.IsNullOrEmpty(splitOut))
            {
                splitOut = Path.Combine(splitIn, "output");
                if (!Directory.Exists(splitOut))
                    Directory.CreateDirectory(splitOut);
            }
            else if (!Directory.Exists(splitOut))
            {
                drawMessage("Output directory does not exist: " + splitOut);
                return;
            }

            double sThresh = PromptDouble("Enter threshold (length of black dip in seconds):", 0.5);
            double sBlack = PromptDouble("Enter black level (e.g. 0.05):", 0.05);

            checkSplits(splitIn, splitOut, sThresh, sBlack);

            drawMessage("Splits complete!");
            AddLog("Splits complete for: " + splitIn);
        }

        static void ProcessMoveTaggedFilesMenu()
        {
            drawScreen(5);
            string bbarsFolder = PromptPath("Enter path to video files:");
            if (string.IsNullOrEmpty(bbarsFolder)) return;

            MoveTaggedFiles(bbarsFolder);

            Console.WriteLine("\nFile moving complete. Press any key to continue...");
            Console.ReadKey(true);
        }

        static void ProcessTestSingleBreakScanMenu()
        {
            drawScreen(6);
            string testFile = PromptPath("Enter path to single video file:", mustExist: true, isFile: true);
            if (string.IsNullOrEmpty(testFile)) return;

            double tThresh = PromptDouble("Enter threshold (default 0.5s):", 0.5);
            double tBlack = PromptDouble("Enter black luminance (default 0.10):", 0.10);
            double tWait = PromptDouble("Enter minimum start time (default 0s):", 0);
            int tLength = PromptInt("Enter minimum spacing between breaks (default 300s):", 300);
            int tEnd = PromptInt("Enter end cutoff padding (default 0s):", 0);

            Console.WriteLine("\nScanning for commercials...");
            List<TimeSpan> testBreaks = scanForCommercialBreaks(testFile, tThresh, tWait, tLength, tEnd, tBlack, false);

            Console.WriteLine($"\nFound {testBreaks.Count} commercial(s):");
            foreach (TimeSpan t in testBreaks)
                Console.WriteLine($"Break at: {t:hh\\:mm\\:ss\\.fff} ({Math.Round(t.TotalSeconds, 2)}s)");

            Console.WriteLine("\nPress any key to return to menu...");
            Console.ReadKey(true);
        }

        static void ProcessAddDurationToFilenamesMenu()
        {
            drawScreen(7);
            string durFolder = PromptPath("Enter path to video files:");
            if (string.IsNullOrEmpty(durFolder)) return;

            recurse_add_duration(durFolder);
        }

        static void ProcessSettingsMenu()
        {
            drawScreen(9);

            string flocation = PromptSetting("Enter FFMPEG Location:", ffmpeg_location);
            if (!File.Exists(flocation))
            {
                drawMessage("FFmpeg binary not found at: " + flocation);
                return;
            }
            ffmpeg_location = flocation;

            string tlocation = PromptSetting("Enter TEMP Path:", temp_folder);
            if (!string.IsNullOrEmpty(tlocation) && !Directory.Exists(tlocation))
            {
                try { Directory.CreateDirectory(tlocation); }
                catch (Exception ex) { Console.WriteLine("Warning: Could not create temp directory: " + ex.Message); }
            }
            temp_folder = tlocation;

            string llocation = PromptSetting("Enter Log File Location:", Log_File);
            if (!string.IsNullOrEmpty(llocation) && !Directory.Exists(llocation))
            {
                try { Directory.CreateDirectory(llocation); }
                catch (Exception ex) { Console.WriteLine("Warning: Could not create log directory: " + ex.Message); }
            }
            Log_File = llocation;

            Console.Write("\nTesting for NVIDIA NVENC hardware encoder... ");
            bool nvencAvailable = DetectNvidiaEncoder();

            if (nvencAvailable)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[AVAILABLE - Preset: {nvenc_preset}]");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("[NOT DETECTED / FAILED]");
            }
            Console.ResetColor();

            use_nvidia = PromptBool("Enable NVIDIA Hardware Acceleration?", nvencAvailable);
            Console.WriteLine("NVIDIA acceleration set to: " + (use_nvidia ? "ENABLED" : "DISABLED"));

            string settingsContent =
                $"ffmpeg location={ffmpeg_location}\n" +
                $"temp folder={temp_folder}\n" +
                $"log file={Log_File}\n" +
                $"use nvidia={use_nvidia.ToString().ToLower()}\n";

            File.WriteAllText("settings.txt", settingsContent);
            drawMessage("Settings saved successfully!");
        }

        static void ProcessRemoveNormalizationMarkMenu()
        {
            drawScreen('n');
            string rnaFolder = PromptPath("Enter path to files:");
            if (string.IsNullOrEmpty(rnaFolder)) return;

            foreach (string file in GetFiles(rnaFolder))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("_NA_"))
                {
                    string dir = Path.GetDirectoryName(file);
                    string ext = Path.GetExtension(file);
                    string newPath = Path.Combine(dir, name.Replace("_NA_", "") + ext);

                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Move(file, newPath);
                        Console.WriteLine($"{Path.GetFileName(file)} ==> {Path.GetFileName(newPath)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }

            Console.WriteLine("\nNormalization tags removed. Press any key to continue...");
            Console.ReadKey(true);
        }

        static void ProcessAppendPrependFilenamesMenu()
        {
            drawScreen('k');
            string klaFolder = PromptPath("Enter path to files:");
            if (string.IsNullOrEmpty(klaFolder)) return;

            List<string> klaFiles = GetFiles(klaFolder);

            Console.WriteLine("\nEnter string to APPEND to filenames (leave blank to skip):");
            Console.Write("> ");
            string appendStr = Console.ReadLine() ?? "";

            if (!string.IsNullOrEmpty(appendStr))
            {
                foreach (string file in klaFiles)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!name.Contains(appendStr))
                    {
                        string dir = Path.GetDirectoryName(file);
                        string ext = Path.GetExtension(file);
                        string newPath = Path.Combine(dir, name + appendStr + ext);

                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Move(file, newPath);
                            Console.WriteLine($"{Path.GetFileName(file)} ==> {Path.GetFileName(newPath)}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                    }
                }
            }

            Console.WriteLine("\nEnter string to PREPEND to filenames (leave blank to skip):");
            Console.Write("> ");
            string prependStr = Console.ReadLine() ?? "";

            if (!string.IsNullOrEmpty(prependStr))
            {
                // Re-fetch files in case they were renamed in the previous step
                foreach (string file in GetFiles(klaFolder))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!name.StartsWith(prependStr))
                    {
                        string dir = Path.GetDirectoryName(file);
                        string ext = Path.GetExtension(file);
                        string newPath = Path.Combine(dir, prependStr + name + ext);

                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Move(file, newPath);
                            Console.WriteLine($"{Path.GetFileName(file)} ==> {Path.GetFileName(newPath)}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                    }
                }
            }

            Console.WriteLine("\nRenaming complete. Press any key to continue...");
            Console.ReadKey(true);
        }

        static void ProcessFindReplaceFilenamesMenu()
        {
            drawScreen('l');
            string rlaFolder = PromptPath("Enter path to files:");
            if (string.IsNullOrEmpty(rlaFolder)) return;

            Console.WriteLine("\nEnter string to find:");
            Console.Write("> ");
            string findStr = Console.ReadLine() ?? "";
            if (string.IsNullOrEmpty(findStr)) return;

            Console.WriteLine("\nEnter string to replace with:");
            Console.Write("> ");
            string replaceStr = Console.ReadLine() ?? "";

            foreach (string file in GetFiles(rlaFolder))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains(findStr))
                {
                    string dir = Path.GetDirectoryName(file);
                    string ext = Path.GetExtension(file);

                    // Preserve _NA_ tag identity during replacement
                    string newName = name.Replace("_NA_", "|||||")
                                         .Replace(findStr, replaceStr)
                                         .Replace("|||||", "_NA_");

                    string newPath = Path.Combine(dir, newName + ext);
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Move(file, newPath);
                        Console.WriteLine($"{Path.GetFileName(file)} ==> {Path.GetFileName(newPath)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }

            Console.WriteLine($"\nReplaced '{findStr}' with '{replaceStr}'. Press any key to continue...");
            Console.ReadKey(true);
        }

        static void ProcessAddNormalizationMarkMenu()
        {
            drawScreen('m');
            string mrnaFolder = PromptPath("Enter path to files:");
            if (string.IsNullOrEmpty(mrnaFolder)) return;

            foreach (string file in GetFiles(mrnaFolder))
            {
                string dir = Path.GetDirectoryName(file);
                string ext = Path.GetExtension(file);
                string name = Path.GetFileNameWithoutExtension(file);

                if (!name.Contains("_NA_"))
                {
                    string targetName;
                    if (ext.Equals(".commercials", StringComparison.OrdinalIgnoreCase))
                    {
                        string innerExt = Path.GetExtension(name);
                        string innerBase = Path.GetFileNameWithoutExtension(name);
                        targetName = $"{innerBase}_NA_{innerExt}.commercials";
                    }
                    else
                    {
                        targetName = $"{name}_NA_{ext}";
                    }

                    string newPath = Path.Combine(dir, targetName);
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Move(file, newPath);
                        Console.WriteLine($"{Path.GetFileName(file)} ==> {targetName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }

            Console.WriteLine("\n_NA_ markers added. Press any key to continue...");
            Console.ReadKey(true);
        }

        static void ProcessCleanAsciiFilenamesMenu()
        {
            drawScreen('u');
            string urnaFolder = PromptPath("Enter path to files:");
            if (string.IsNullOrEmpty(urnaFolder)) return;

            foreach (string file in GetFiles(urnaFolder))
            {
                string dir = Path.GetDirectoryName(file);
                string ext = Path.GetExtension(file);
                string name = Path.GetFileNameWithoutExtension(file);
                string cleanName = ReturnCleanASCII(name);

                if (name != cleanName)
                {
                    string newPath = Path.Combine(dir, cleanName + ext);
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Move(file, newPath);
                        Console.WriteLine($"{name}{ext} ==> {cleanName}{ext}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }

            Console.WriteLine("\nClean ASCII filenames complete. Press any key to continue...");
            Console.ReadKey(true);
        }

        static void ProcessRemoveTimestampTokensMenu()
        {
            drawScreen('t');
            string tnaFolder = PromptPath("Enter path to files:");
            if (string.IsNullOrEmpty(tnaFolder)) return;

            foreach (string file in GetFiles(tnaFolder))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                int startToken = name.IndexOf("%T(");
                int endToken = startToken > -1 ? name.IndexOf(")%", startToken + 3) : -1;

                if (startToken > -1 && endToken > -1)
                {
                    string dir = Path.GetDirectoryName(file);
                    string ext = Path.GetExtension(file);
                    string cleanedName = name.Substring(0, startToken) + name.Substring(endToken + 2);
                    string newPath = Path.Combine(dir, cleanedName + ext);

                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Move(file, newPath);
                        Console.WriteLine($"{name}{ext} ==> {cleanedName}{ext}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }

            Console.WriteLine("\nTimestamp tokens removed. Press any key to continue...");
            Console.ReadKey(true);
        }

        static void Main(string[] args)
        {
            string appPath = AppDomain.CurrentDomain.BaseDirectory;
            string settingsFile = Path.Combine(appPath, "settings.txt");

            LoadSettings(settingsFile);
            ResetLog();

            if (HandleCommandLineArguments(args))
            {
                return; // Clean exit back to command prompt / shell script
            }

            System.Threading.Thread.Sleep(2000);

            while (true)
            {
                drawScreen(0);

                ConsoleKeyInfo ck = Console.ReadKey(true);
                if (ck.Key == ConsoleKey.Escape) Environment.Exit(0);

                char selected_option = ck.KeyChar;
                if (string.IsNullOrEmpty(ffmpeg_location)) selected_option = '9';

                switch (selected_option)
                {
                    case '1':
                        ProcessAudioNormalizationMenu();
                    break;
                    case '2':
                        ProcessPrintBreaksMenu();
                    break;
                    case '3':
                        ProcessSplitVideoMenu();
                    break;
                    case '5':
                        ProcessMoveTaggedFilesMenu();
                    break;
                    case '6':
                        ProcessTestSingleBreakScanMenu();
                    break;
                    case '7':
                        ProcessAddDurationToFilenamesMenu();
                    break;
                    case '9':
                        ProcessSettingsMenu();
                    break;
                    case 'n':
                        ProcessRemoveNormalizationMarkMenu();
                    break;
                    case 'k':
                        ProcessAppendPrependFilenamesMenu();
                    break;
                    case 'l':
                        ProcessFindReplaceFilenamesMenu();
                    break;
                    case 'm':
                        ProcessAddNormalizationMarkMenu();
                    break;
                    case 'u':
                        ProcessCleanAsciiFilenamesMenu();
                    break;
                    case 't':
                        ProcessRemoveTimestampTokensMenu();
                    break;
                    default:
                        Console.CursorVisible = false;
                        drawMessage("Invalid Entry!");
                        break;
                }
            }
        }
    }
}