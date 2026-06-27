using System.Diagnostics;
using System.Runtime.InteropServices;
using AppleMusicPlayer.Configurations;
using Microsoft.Extensions.Configuration;

// Setup log file paths in the application directory
string logPath = Path.Combine(AppContext.BaseDirectory, "app_log.txt");
string oldLogPath = Path.Combine(AppContext.BaseDirectory, "app_log_old.txt");

// 1. Manage Log File Size (Log Rotation)
try
{
    if (File.Exists(logPath))
    {
        FileInfo logFileInfo = new FileInfo(logPath);
        if (logFileInfo.Length > 2 * 1024 * 1024) // 2 MB Limit
        {
            if (File.Exists(oldLogPath))
            {
                File.Delete(oldLogPath);
            }
            File.Move(logPath, oldLogPath);
            Log("[SYSTEM] Log file reached 2MB. Rotated current log to 'app_log_old.txt' and started a new log stream.");
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[LOGGER WARNING] Failed rotating logs: {ex.Message}");
}

// Write execution divider
Log("====================================================================================================");
Log($"[STARTUP] Apple Music Playlist Runner execution initiated.");

try
{
    // 2. Log System and Runtime Information
    Log($"[ENV] OS Platform: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
    Log($"[ENV] .NET Runtime: {RuntimeInformation.FrameworkDescription}");
    Log($"[ENV] App Directory: {AppContext.BaseDirectory}");
    Log($"[ENV] Main Process ID (PID): {Environment.ProcessId}");
    Log($"[ENV] Command Line Arguments: {string.Join(" ", args)}");

    // 0. Parse Arguments
    bool isStopMusic = args.Any(a => a.Equals("isStopMusic=1", StringComparison.OrdinalIgnoreCase) || a == "1");
    if (isStopMusic)
    {
        Log("[ARGS] Argument isStopMusic=1 detected. Initiating termination sequence...");
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process[] processes = Process.GetProcessesByName("AppleMusic");
            Log($"[PROCESS] Found {processes.Length} running AppleMusic processes to terminate on Windows.");
            foreach (Process p in processes)
            {
                try 
                { 
                    p.Kill(); 
                    Log($"[PROCESS] Terminated process AppleMusic (PID: {p.Id}) successfully.");
                } 
                catch (Exception ex)
                { 
                    Log($"[PROCESS] Warning: Could not terminate process AppleMusic (PID: {p.Id}). Error: {ex.Message}");
                }
            }
            Log("[PROCESS] Apple Music termination completed on Windows.");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Log("[PROCESS] Sending killall request to macOS Music application...");
            using (Process? macKill = Process.Start(new ProcessStartInfo 
            { 
                FileName = "killall", 
                Arguments = "Music", 
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }))
            {
                macKill?.WaitForExit();
                string error = macKill?.StandardError.ReadToEnd() ?? "";
                if (!string.IsNullOrWhiteSpace(error))
                {
                     Log($"[PROCESS] killall error output: {error.Trim()}");
                }
            }
            Log("[PROCESS] Apple Music termination completed on macOS.");
        }
        
        Log("[SYSTEM] Stop command handled. Exiting execution cleanly.");
        return; // Exit completely
    }

    // 1. Setup Configuration
    string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    Log($"[CONFIG] Loading configuration. Target file: '{configPath}' (Exists: {File.Exists(configPath)})");
    
    IConfigurationRoot configurationRoot = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();

    // 2. Bind and Validate Playlist Section
    Log("[CONFIG] Parsing and binding 'Playlist' configuration section...");
    Playlist? playList = configurationRoot.GetSection("Playlist").Get<Playlist>();
    if (playList is null)
    {
        throw new Exception("Unable to bind section 'Playlist' to Playlist class configuration.");
    }

    Log($"[CONFIG] Successfully loaded settings: IsShuffleOverPlay={playList.IsShuffleOverPlay}, WaitForOpen={playList.WaitForOpen}ms");

    // 3. Resolve Playlist ID based on Date and Time
    string? selectedId = null;
    int tabsRequired = playList.IsShuffleOverPlay ? 2 : 1;

    if (playList.Schedules != null && playList.Schedules.Count > 0)
    {
        DateTime now = DateTime.Now;
        int currentMonthDay = now.Month * 100 + now.Day; // MMdd
        int currentHourMin = now.Hour * 100 + now.Minute; // HHmm

        Log($"[SOLVER] Active local clock parameters: DateValue={currentMonthDay:D4} (MMdd), TimeValue={currentHourMin:D4} (HHmm) | {now:f}");
        Log($"[SOLVER] Evaluating {playList.Schedules.Count} root calendar entries...");

        List<(int DateVal, string DateKey, Dictionary<string, string> Hours)> parsedDates = new List<(int DateVal, string DateKey, Dictionary<string, string> Hours)>();

        foreach (KeyValuePair<string, Dictionary<string, string>> dateKvp in playList.Schedules)
        {
            string dateStr = dateKvp.Key.Replace("date", "");
            if (dateStr.Length != 4 || !int.TryParse(dateStr, out int dateInt))
            {
                Log($"[SOLVER] Ignoring malformed schedule key: '{dateKvp.Key}'");
                continue;
            }
            parsedDates.Add((dateInt, dateKvp.Key, dateKvp.Value));
        }

        if (parsedDates.Count > 0)
        {
            // 1. Find the active date schedule (last passed date, or wrap around to latest in year)
            List<(int DateVal, string DateKey, Dictionary<string, string> Hours)> passedDates = parsedDates.Where(d => d.DateVal <= currentMonthDay).OrderByDescending(d => d.DateVal).ToList();
            
            (int DateVal, string DateKey, Dictionary<string, string> Hours) activeDate;
            if (passedDates.Any())
            {
                activeDate = passedDates.First();
                Log($"[SOLVER] Date Match: Found past/active schedules for today's date context. Chosen: '{activeDate.DateKey}' (Value: {activeDate.DateVal:D4})");
            }
            else
            {
                activeDate = parsedDates.OrderByDescending(d => d.DateVal).First();
                Log($"[SOLVER] Date Match: No passed schedules found for this year yet. Wrapping around to the latest date: '{activeDate.DateKey}' (Value: {activeDate.DateVal:D4})");
            }

            // 2. Find the active hour within that date's daily schedule
            Log($"[SOLVER] Parsing hours inside active date '{activeDate.DateKey}'...");
            List<(int HourVal, string HourKey, string PlaylistId)> parsedHours = new List<(int HourVal, string HourKey, string PlaylistId)>();
            foreach (KeyValuePair<string, string> hourKvp in activeDate.Hours)
            {
                string hourStr = hourKvp.Key.Replace("hour", "");
                if (hourStr.Length != 4 || !int.TryParse(hourStr, out int hourInt))
                {
                    Log($"[SOLVER] Ignoring malformed hour key: '{hourKvp.Key}'");
                    continue;
                }
                parsedHours.Add((hourInt, hourKvp.Key, hourKvp.Value));
            }

            if (parsedHours.Count > 0)
            {
                // Last passed hour, or wrap around to the latest hour of the previous day
                List<(int HourVal, string HourKey, string PlaylistId)> passedHours = parsedHours.Where(h => h.HourVal <= currentHourMin).OrderByDescending(h => h.HourVal).ToList();
                
                (int HourVal, string HourKey, string PlaylistId) activeHour;
                if (passedHours.Any())
                {
                    activeHour = passedHours.First();
                    Log($"[SOLVER] Time Match: Selected chronologically active slot. Chosen: '{activeHour.HourKey}' (Value: {activeHour.HourVal:D4})");
                }
                else
                {
                    activeHour = parsedHours.OrderByDescending(h => h.HourVal).First();
                    Log($"[SOLVER] Time Match: No passed hours in the active day. Wrapping around to latest hour: '{activeHour.HourKey}' (Value: {activeHour.HourVal:D4})");
                }
                
                selectedId = activeHour.PlaylistId;
                Log($"[SOLVER] Solver succeeded. Resolved Playlist ID: {selectedId}");
            }
            else
            {
                Log($"[SOLVER] Warning: Active date block '{activeDate.DateKey}' contains no parsed hour definitions.");
            }
        }
        else
        {
            Log("[SOLVER] Warning: Calendar dates container yielded 0 parsed schedules.");
        }
    }
    else
    {
        Log("[SOLVER] Warning: Schedules section in configuration is empty or null.");
    }

    if (string.IsNullOrEmpty(selectedId))
    {
        Log("[SYSTEM] Critical: No valid schedule could be resolved. Exiting execution.");
        return;
    }
    
    string playlistUri = $"music://music.apple.com/us/playlist/{selectedId}";
    Log($"[LAUNCHER] Opening Apple Music using protocol activation. Target URI: '{playlistUri}'");

    using (Process? launchProcess = Process.Start(new ProcessStartInfo
    {
        FileName = playlistUri,
        UseShellExecute = true,
        CreateNoWindow = true
    }))
    {
        Log($"[LAUNCHER] URI registration protocol activated. Launch Process Object created.");
    }

    // 4. Wait for the playlist page to render
    int waitTime = Math.Max(playList.WaitForOpen, 1000); // Enforce at least 1s wait safety
    Log($"[SCHEDULER] Delaying execution for {waitTime}ms to allow Apple Music UI to load and render...");
    Thread.Sleep(waitTime);
    Log("[SCHEDULER] Delay concluded. Resuming automation thread execution...");

    // 5 & 6. Execute OS-Specific Automation
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        Log("[AUTOMATION] Platform target confirmed as macOS. Constructing AppleScript...");
        const string appleScript = "tell application \"Music\" to play";

        ProcessStartInfo macStartInfo = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        macStartInfo.ArgumentList.Add("-e");
        macStartInfo.ArgumentList.Add(appleScript);

        Log($"[AUTOMATION] Launching AppleScript interpreter process...");
        using (Process? macProcess = Process.Start(macStartInfo))
        {
            if (macProcess != null)
            {
                macProcess.WaitForExit();
                string output = macProcess.StandardOutput.ReadToEnd();
                string error = macProcess.StandardError.ReadToEnd();
                
                Log($"[AUTOMATION] macOS osascript process completed. Exit Code: {macProcess.ExitCode}");
                if (!string.IsNullOrWhiteSpace(output)) Log($"[AUTOMATION] StdOut: {output.Trim()}");
                if (!string.IsNullOrWhiteSpace(error)) Log($"[AUTOMATION] StdErr: {error.Trim()}");
            }
            else
            {
                Log("[AUTOMATION] Error: Failed to initialize osascript process instance.");
            }
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        Log("[AUTOMATION] Platform target confirmed as Windows. Inspecting running tasks...");
        
        // Dynamically find the Apple Music process to get its exact Window Title.
        Process[] amProcesses = Process.GetProcessesByName("AppleMusic");
        Log($"[AUTOMATION] Diagnostics: Found {amProcesses.Length} processes matching name 'AppleMusic'.");
        
        foreach (var p in amProcesses)
        {
            Log($"[AUTOMATION]   - Process ID: {p.Id} | Has Window Handle: {p.MainWindowHandle != IntPtr.Zero} | Title: '{p.MainWindowTitle}'");
        }

        Process? mainProcess = amProcesses.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        string windowTitle = mainProcess != null ? mainProcess.MainWindowTitle : "Apple Music";
        Log($"[AUTOMATION] Target selection: Handle PID {mainProcess?.Id ?? 0} ('{windowTitle}')");

        // Force the window to the foreground using native Windows API
        if (mainProcess != null && mainProcess.MainWindowHandle != IntPtr.Zero)
        {
            Log("[AUTOMATION] Calling User32 ShowWindow(RESTORE)...");
            bool shown = NativeMethods.ShowWindow(mainProcess.MainWindowHandle, 9); // 9 = SW_RESTORE
            
            Log("[AUTOMATION] Calling User32 SetForegroundWindow...");
            bool targeted = NativeMethods.SetForegroundWindow(mainProcess.MainWindowHandle);
            
            Log($"[AUTOMATION] User32 API Invocation results: ShowWindow={shown}, SetForegroundWindow={targeted}");
        }
        else
        {
            Log("[AUTOMATION] Warning: No active, visible Windows main handles discovered for 'AppleMusic'. Keyboard injection may miss focus.");
        }

        // --- WINDOWS AUTOMATION (PowerShell) ---
        Log($"[AUTOMATION] Preparing key sequences: TabsRequired={tabsRequired}");
        string tabCommands = string.Concat(Enumerable.Repeat("$wshell.SendKeys('{TAB}'); Start-Sleep -Milliseconds 200; ", tabsRequired));

        string psCommand = 
            "$wshell = New-Object -ComObject WScript.Shell; " +
            "Start-Sleep -Milliseconds 800; " +
            tabCommands +
            "$wshell.SendKeys('{ENTER}')";

        // Encode the PowerShell script to Base64 (UTF-16LE) to completely bypass command-line parsing and escaping issues.
        byte[] scriptBytes = System.Text.Encoding.Unicode.GetBytes(psCommand);
        string encodedCommand = Convert.ToBase64String(scriptBytes);
        Log($"[AUTOMATION] Compiled PowerShell Script: {psCommand}");

        ProcessStartInfo winStartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Log("[AUTOMATION] Launching PowerShell keyboard injector on background thread...");
        using (Process? winProcess = Process.Start(winStartInfo))
        {
            if (winProcess != null)
            {
                winProcess.WaitForExit();
                string output = winProcess.StandardOutput.ReadToEnd();
                string error = winProcess.StandardError.ReadToEnd();
                
                Log($"[AUTOMATION] PowerShell process exited. Exit Code: {winProcess.ExitCode}");
                if (!string.IsNullOrWhiteSpace(output)) Log($"[AUTOMATION] PowerShell StdOut: {output.Trim()}");
                if (!string.IsNullOrWhiteSpace(error)) Log($"[AUTOMATION] PowerShell StdErr: {error.Trim()}");
            }
            else
            {
                Log("[AUTOMATION] Error: Could not launch PowerShell process instance.");
            }
        }
    }
    else
    {
        Log("[AUTOMATION] Execution platform is not supported. Skipping UI macro sequences.");
    }
}
catch (Exception ex)
{
    Log($"[CRITICAL ERROR] Execution failed: {ex.Message}");
    if (ex.InnerException != null)
    {
        Log($"[INNER ERROR] {ex.InnerException.Message}");
    }
    Log($"[STACK TRACE] {ex.StackTrace}");
    throw; // Rethrow to ensure correct process exit code handling
}
finally
{
    Log("[SYSTEM] Run sequence finished. Releasing resources.\n");
}

// Local helper logging method
void Log(string message)
{
    string formattedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
    Console.WriteLine(formattedMessage);

    try
    {
        File.AppendAllText(logPath, formattedMessage + Environment.NewLine);
    }
    catch (Exception ex)
    {
        // Fallback trace in case the log file is locked by another process
        Console.Error.WriteLine($"[LOGGER FALLBACK ERROR] Failed writing to file system: {ex.Message}");
    }
}

// Native Windows API methods for reliable window management
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}