using System.Diagnostics;
using System.Runtime.InteropServices;
using AppleMusicPlayer.Configurations;
using Microsoft.Extensions.Configuration;

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

// 3. Launch Apple Music Playlist (This works on both Windows and Mac)
string playlistUri = $"music://music.apple.com/us/playlist/{playList.Id}";
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
    int tabsRequired = 1; 
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