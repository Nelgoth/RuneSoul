# How to Get Unity Crash Logs

## Unity Editor Logs (Most Important)

### Location
```
C:\Users\joshu\AppData\Local\Unity\Editor\Editor.log
```

This is the **main log file** that shows what happened before the crash.

### How to Access
1. Press `Win + R` to open Run dialog
2. Paste this path: `%LOCALAPPDATA%\Unity\Editor`
3. Press Enter
4. Look for `Editor.log` (most recent) and `Editor-prev.log` (previous session)

## Unity Console Output

Before the crash, check the Unity Console window:
1. In Unity Editor: `Window → General → Console` (or Ctrl+Shift+C)
2. Look for red error messages
3. Right-click on errors → "Copy"
4. Send those messages

## Player Crash Dumps (If running a build)

### Location
```
C:\Users\joshu\AppData\LocalLow\<CompanyName>\<ProductName>\
```

Look for:
- `Player.log` - Runtime log
- `Player-prev.log` - Previous session
- Crash dump files (`.dmp` extension)

## Quick Access via PowerShell

Run these commands to quickly open the log folders:

```powershell
# Open Unity Editor logs
explorer "%LOCALAPPDATA%\Unity\Editor"

# Open Player logs (replace with your company/product name)
explorer "%LOCALAPPDATA%Low\DefaultCompany\Rune Soul"

# Open temp crash dumps
explorer "%TEMP%"
```

## What to Send

1. **Last 100-200 lines of Editor.log** (the part right before crash)
2. **Any error messages from Unity Console** (red errors)
3. **Stack traces** (if visible)
4. **What you were doing when it crashed** (e.g., "Loading world X")

## Quick Log Extraction

Open PowerShell and run:

```powershell
# Get last 200 lines of editor log
Get-Content "$env:LOCALAPPDATA\Unity\Editor\Editor.log" -Tail 200 | Out-File "$env:USERPROFILE\Desktop\unity_crash_log.txt"
```

This will create a file on your Desktop called `unity_crash_log.txt` with the relevant part of the log.




