# PrepareForTesting

A small Windows utility that closes common applications (Outlook, Teams, Chrome, Edge, Office, etc.) before a student begins a proctored test. Run it once, all the listed apps and their helper processes get terminated, and a confirmation dialog appears.

## What it does

When run, the tool:

1. Iterates through a configurable list of process names (Outlook, new Outlook, Teams, Chrome, etc.)
2. Terminates each process **and its entire descendant tree** — this catches Teams' helper processes, Edge renderers, Office sub-processes, and other background helpers that simple "kill by name" tools leave behind
3. Retries up to three times for stragglers
4. Unmutes the default playback device and sets master volume to 60%
5. Shows a single confirmation dialog with an OK button: *"Applications closed. Speaker set to 60%. You're ready to begin testing."*

If anything fails (access denied, stubborn process, audio device unavailable, etc.), the dialog switches to a warning and lists what couldn't be done.

## Prerequisites

You need the **.NET 8 SDK** installed on the machine you're building from. The runtime alone isn't enough — you need the SDK, which includes the `dotnet` build tools.

### Install .NET 8 SDK

**Windows:** Download the SDK installer from <https://dotnet.microsoft.com/download/dotnet/8.0> (under "SDK x64 → Windows Installers"). Run the installer, accept defaults. Open a new terminal and verify:

```
dotnet --version
```

You should see `8.0.xxx`.

**macOS:** Download the macOS Arm64 (Apple Silicon) or x64 (Intel) `.pkg` installer from the same page. Run it, then open a new terminal and verify:

```
dotnet --version
```

If `dotnet` isn't found after install, your `PATH` wasn't updated. Add this line to `~/.zshrc`:

```
export PATH="$PATH:/usr/local/share/dotnet"
```

Then run `source ~/.zshrc` or restart your terminal.

**Linux:** Follow the distro-specific instructions at <https://learn.microsoft.com/en-us/dotnet/core/install/linux>. Most distros have a one-line install via the package manager.

## Build a distributable .exe

From the folder containing `PrepareForTesting.csproj`:

```
dotnet publish -c Release
```

The output will be at:

```
bin/Release/net8.0-windows/win-x64/publish/PrepareForTesting.exe
```

That's a **self-contained single-file executable** (~60 MB) — it bundles the .NET runtime inside, so it runs on any Windows 10 or Windows 11 machine without requiring .NET to be installed on the target.

### Building from macOS or Linux

Cross-compiling from non-Windows works, but the SDK requires you to opt in. The included `.csproj` already has `<EnableWindowsTargeting>true</EnableWindowsTargeting>` set, so the same `dotnet publish -c Release` command works.

If you ever see error `NETSDK1100` ("set the EnableWindowsTargeting property to true"), confirm that line is in your csproj, or pass it on the command line:

```
dotnet publish -c Release -p:EnableWindowsTargeting=true
```

The resulting `.exe` is a Windows binary and won't run natively on Mac or Linux. To test it, copy it to a Windows machine.

### Smaller exe (if .NET 8 is already on target machines)

If every machine you'll deploy to already has the **.NET 8 Desktop Runtime** installed, you can produce a much smaller executable (~150 KB) by changing this line in `PrepareForTesting.csproj`:

```xml
<SelfContained>true</SelfContained>
```

to:

```xml
<SelfContained>false</SelfContained>
```

Then rebuild. The framework-dependent exe is faster to copy and doesn't take 60 MB on disk, but it silently fails to launch on machines without the .NET 8 Desktop Runtime.

## Distribution

When you copy the `.exe` to a target Windows machine, two things may happen on first launch:

**SmartScreen warning.** Windows flags unsigned executables from unknown publishers. The user will see a "Windows protected your PC" dialog. They have to click **More info** → **Run anyway**. For wider deployment, look into code signing certificates.

**Mark of the Web.** Files transferred via email, ZIP, or browser download often get flagged as untrusted and may refuse to launch. Right-click the `.exe` → **Properties** → tick the **Unblock** checkbox at the bottom of the General tab → OK.

For mass deployment via Group Policy or MDM, both issues can be handled centrally.

## Customizing the process list

Open `Program.cs` and edit the `TargetProcesses` array near the top of the `Program` class:

```csharp
private static readonly string[] TargetProcesses =
{
    "teams", "chrome", "msedge", "winword", "excel",
    // ... add or remove entries here
};
```

Process names should be **without** the `.exe` extension and are matched case-insensitively. To find a process's name on Windows, open Task Manager → Details tab → look at the "Name" column. Strip the `.exe` to get the value to use here.

After editing, rebuild with `dotnet publish -c Release`.

### Customizing the speaker volume

The target volume is defined as a constant near the top of `Program.cs`:

```csharp
private const float SpeakerVolumeTarget = 0.60f;  // 60%
```

The value is a fraction from `0.0` (silent) to `1.0` (max). Change it and rebuild to apply.

## Project structure

```
PrepareForTesting/
├── Program.cs                    # All application logic
├── PrepareForTesting.csproj      # Project / build configuration
└── README.md                     # This file
```

## Troubleshooting

**The exe does nothing when double-clicked.** Most likely the target machine doesn't have the .NET 8 runtime and you built with `<SelfContained>false</SelfContained>`. Either install the .NET 8 Desktop Runtime on the target, or rebuild with `<SelfContained>true</SelfContained>`.

**"Access denied" warnings in the dialog.** Some processes run with higher privileges than the current user (typically system services or processes started by an administrator). Right-click the `.exe` and select **Run as administrator**, or build a manifest that requests elevation automatically.

**A process I added isn't being killed.** Confirm the exact process name in Task Manager → Details tab. Some apps run under unexpected names (Teams, for example, has historically run as `teams`, `msteams`, and `ms-teams` across different versions — all three are in the default list for that reason).

## License

Add your preferred license here.
