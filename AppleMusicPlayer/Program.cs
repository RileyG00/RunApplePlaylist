using System.Diagnostics;
using System.Runtime.InteropServices;
using AppleMusicPlayer.Configurations;
using Microsoft.Extensions.Configuration;

// 0. Parse Arguments
bool isStopMusic = args.Any(a => a.Equals("isStopMusic=1", StringComparison.OrdinalIgnoreCase) || a == "1");
if (isStopMusic)
{
    Console.WriteLine("Argument isStopMusic=1 provided. Terminating Apple Music...");
    
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
		Process[] processes = Process.GetProcessesByName("AppleMusic");
        foreach (Process p in processes)
        {
            try { p.Kill(); } catch { /* Ignore if it's already dead or inaccessible */ }
        }
        Console.WriteLine("Apple Music terminated on Windows.");
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        Process.Start(new ProcessStartInfo { FileName = "killall", Arguments = "Music", CreateNoWindow = true });
        Console.WriteLine("Apple Music terminated on macOS.");
    }
    
    return; // Exit completely
}

// 1. Setup Configuration
IConfigurationRoot configurationRoot = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// 2. Bind and Validate Playlist Section
Playlist? playList = configurationRoot.GetSection("Playlist").Get<Playlist>();
if (playList is null)
{
    throw new Exception("Unable to bind section 'Playlist' to Playlist class configuration.");
}

// 3. Resolve Playlist ID based on Date and Time
string? selectedId = null;
int tabsRequired = playList.IsShuffleOverPlay ? 2 : 1;


if (playList.Schedules != null && playList.Schedules.Count > 0)
{
	DateTime now = DateTime.Now;
    int currentMonthDay = now.Month * 100 + now.Day; // MMdd
    int currentHourMin = now.Hour * 100 + now.Minute; // HHmm

    List<(int DateVal, string DateKey, Dictionary<string, string> Hours)> parsedDates = new List<(int DateVal, string DateKey, Dictionary<string, string> Hours)>();

    foreach (KeyValuePair<string, Dictionary<string, string>> dateKvp in playList.Schedules)
    {
        string dateStr = dateKvp.Key.Replace("date", "");
        if (dateStr.Length != 4 || !int.TryParse(dateStr, out int dateInt)) continue;
        parsedDates.Add((dateInt, dateKvp.Key, dateKvp.Value));
    }

    if (parsedDates.Count > 0)
    {
        // 1. Find the active date schedule (last passed date, or wrap around to latest in year)
        List<(int DateVal, string DateKey, Dictionary<string, string> Hours)> passedDates = parsedDates.Where(d => d.DateVal <= currentMonthDay).OrderByDescending(d => d.DateVal).ToList();
        (int DateVal, string DateKey, Dictionary<string, string> Hours) activeDate = passedDates.Any() ? passedDates.First() : parsedDates.OrderByDescending(d => d.DateVal).First();

        // 2. Find the active hour within that date's daily schedule
        List<(int HourVal, string HourKey, string PlaylistId)> parsedHours = new List<(int HourVal, string HourKey, string PlaylistId)>();
        foreach (KeyValuePair<string, string> hourKvp in activeDate.Hours)
        {
            string hourStr = hourKvp.Key.Replace("hour", "");
            if (hourStr.Length != 4 || !int.TryParse(hourStr, out int hourInt)) continue;
            parsedHours.Add((hourInt, hourKvp.Key, hourKvp.Value));
        }

        if (parsedHours.Count > 0)
        {
            // Last passed hour, or wrap around to the latest hour of the previous day
            List<(int HourVal, string HourKey, string PlaylistId)> passedHours = parsedHours.Where(h => h.HourVal <= currentHourMin).OrderByDescending(h => h.HourVal).ToList();
            (int HourVal, string HourKey, string PlaylistId) activeHour = passedHours.Any() ? passedHours.First() : parsedHours.OrderByDescending(h => h.HourVal).First();
            
            selectedId = activeHour.PlaylistId;
            Console.WriteLine($"Matched Schedule: Date ({activeDate.DateKey}) and Hour ({activeHour.HourKey}) -> {selectedId}");
        }
    }
}

if (string.IsNullOrEmpty(selectedId))
{
    Console.WriteLine("No valid schedule found. Exiting.");
    return;
}
string playlistUri = $"music://music.apple.com/us/playlist/{selectedId}";
Console.WriteLine($"Opening Apple Music with URI: {playlistUri}");

Process.Start(new ProcessStartInfo
{
    FileName = playlistUri,
    UseShellExecute = true,
    CreateNoWindow = true
});

// 4. Wait for the playlist page to render
int waitTime = Math.Max(playList.WaitForOpen, 3000);
Console.WriteLine($"Waiting {waitTime}ms for the app to render the playlist...");
Thread.Sleep(waitTime);

// 5 & 6. Execute OS-Specific Automation
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.WriteLine("Executing Windows UI Automation...");
    
    // Dynamically find the Apple Music process to get its exact Window Title.
    // We look for the process that actually has an active Window Handle.
    Process[] amProcesses = Process.GetProcessesByName("AppleMusic");
    Process? mainProcess = amProcesses.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
    
    string windowTitle = mainProcess != null ? mainProcess.MainWindowTitle : "Apple Music";
    Console.WriteLine($"Targeting Window Title: '{windowTitle}'");

    // Force the window to the foreground using native Windows API
    if (mainProcess != null && mainProcess.MainWindowHandle != IntPtr.Zero)
    {
        NativeMethods.ShowWindow(mainProcess.MainWindowHandle, 9); // 9 = SW_RESTORE
        NativeMethods.SetForegroundWindow(mainProcess.MainWindowHandle);
        Console.WriteLine("Window brought to foreground natively.");
    }
    else
    {
        Console.WriteLine("Warning: Could not find a valid window handle to activate.");
    }

    // --- WINDOWS AUTOMATION (PowerShell) ---
    string tabCommands = string.Concat(Enumerable.Repeat("$wshell.SendKeys('{TAB}'); Start-Sleep -Milliseconds 200; ", tabsRequired));

    string psCommand = 
        "$wshell = New-Object -ComObject WScript.Shell; " +
        "Start-Sleep -Milliseconds 800; " +
        tabCommands +
        "$wshell.SendKeys('{ENTER}')";

    // Encode the PowerShell script to Base64 (UTF-16LE) to completely bypass command-line parsing and escaping issues.
    byte[] scriptBytes = System.Text.Encoding.Unicode.GetBytes(psCommand);
    string encodedCommand = Convert.ToBase64String(scriptBytes);

    using (Process? winProcess = Process.Start(new ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -EncodedCommand {encodedCommand}",
        UseShellExecute = false,
        CreateNoWindow = true
    }))
    {
        winProcess?.WaitForExit();
        Console.WriteLine("Windows automation sequence completed.");
    }
}
else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    Console.WriteLine("Executing macOS AppleScript Automation...");
    
    // --- MACOS AUTOMATION (AppleScript via osascript) ---
    const string appleScript = "tell application \"Music\" to play";

    ProcessStartInfo macStartInfo = new ProcessStartInfo
    {
        FileName = "osascript",
        UseShellExecute = false,
        CreateNoWindow = true
    };

    macStartInfo.ArgumentList.Add("-e");
    macStartInfo.ArgumentList.Add(appleScript);

    using (Process? macProcess = Process.Start(macStartInfo))
    {
        macProcess?.WaitForExit();
        Console.WriteLine("macOS automation sequence completed.");
    }
}
else
{
    Console.WriteLine("Automation not supported on this Operating System.");
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