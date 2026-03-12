using System;
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
Process.Start(new ProcessStartInfo
{
	FileName = playlistUri,
	UseShellExecute = true,
	CreateNoWindow = true
});

// 4. Wait for the playlist page to render
Thread.Sleep(Math.Max(playList.WaitForOpen, 3000));

// 5 & 6. Execute OS-Specific Automation
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
	// --- WINDOWS AUTOMATION (PowerShell) ---
	const string psCommand =
		"$wshell = New-Object -ComObject WScript.Shell; " +
		"Start-Sleep -Milliseconds 2000; " +
		"$wshell.AppActivate('Apple Music'); " +
		"Start-Sleep -Milliseconds 800; " +
		"$wshell.SendKeys('{TAB}'); " +
		"Start-Sleep -Milliseconds 200; " +
		"$wshell.SendKeys('{ENTER}')";

	using (Process? winProcess = Process.Start(new ProcessStartInfo
	{
		FileName = "powershell.exe",
		Arguments = $"-NoProfile -Command \"{psCommand}\"",
		UseShellExecute = false,
		CreateNoWindow = true
	}))
	{
		winProcess?.WaitForExit();
	}
}
else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
	// --- MACOS AUTOMATION (AppleScript via osascript) ---
	// This directly commands the Music app to play the active context
	const string appleScript = "tell application \"Music\" to play";

	ProcessStartInfo macStartInfo = new ProcessStartInfo
	{
		FileName = "osascript",
		UseShellExecute = false,
		CreateNoWindow = true
	};

	// ArgumentList safely handles spaces and quotes without needing shell escaping
	macStartInfo.ArgumentList.Add("-e");
	macStartInfo.ArgumentList.Add(appleScript);

	using (Process? macProcess = Process.Start(macStartInfo))
	{
		macProcess?.WaitForExit();
	}
}
else
{
	Console.WriteLine("Automation not supported on this Operating System.");
}
