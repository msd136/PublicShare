# GAM Workbench

A Windows (WPF, .NET 8) desktop front end for [GAM7](https://github.com/GAM-team/GAM).

GAM Workbench doesn't reimplement GAM. Every action runs your installed `gam.exe`, so results match the command line exactly, and upgrading GAM upgrades the app. The command behind every action is shown in the strip at the bottom of the window, and anything that changes data shows its exact command and asks before it runs.

## Build

Requirements: Windows 10 or 11 and the .NET 8 SDK (or newer).

```
dotnet publish src\GamGui\GamGui.csproj -c Release -r win-x64 --self-contained true
```

The app is written to `src\GamGui\bin\Release\net8.0-windows\win-x64\publish\GamWorkbench.exe`.

Publish the `src\GamGui` project rather than the solution; `-r` on a solution applies the runtime to every project. `publish.cmd` in this folder runs the command above.

For a single .exe, add `-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.

To build with .NET 9, change `net8.0-windows` to `net9.0-windows` in `src\GamGui\GamGui.csproj` and `net8.0` to `net9.0` in `src\GamGui.Core\GamGui.Core.csproj`.

## Set up GAM

1. Install GAM7. The app looks for `gam.exe` in this order: the path in Settings; a `gam7` or `gam` folder next to `GamWorkbench.exe`; next to `GamWorkbench.exe`; `C:\GAM7`; `C:\GAM`; Program Files\GAM7; %LOCALAPPDATA%\GAM7; then your PATH.
2. If GAM isn't set up yet, open **Settings → First-time setup** and run the three steps in order. They open in a console window because GAM asks questions and opens your browser.
3. Use **Settings → Check your setup** to confirm the connection and authorization.

The Drive & Gmail screen needs GAM's service account with domain-wide delegation (step 3 checks it).

If you keep GAM's files outside its default location, or switch between domains, set **Config folder (GAMCFGDIR)** in Settings.

## Using the app

The app opens on **Start**, which runs a setup checklist (GAM installed, network, admin authorization, directory access) and links to every area. Each screen has a **How to use this screen** guide under its title; hide it once you know your way around and bring it back with **Show guide**.

Navigation is grouped by what you're doing:

- **Start**: the setup checklist and shortcuts.
- **Manage**
  - **Users**: search, create, edit name, move org unit, reset password, add to group, suspend, restore, delete.
  - **Groups**: groups and their members; add, remove, change role, create, delete.
  - **Org units**: tree view; create, rename, describe, move users, delete, list users.
  - **Devices**: ChromeOS (move, disable, re-enable, deprovision) and mobile (approve, block, wipe account, wipe device, delete).
  - **Drive & Gmail**: a user's files, shared drives, messages, labels, filters, send-as, delegates, forwarding, vacation and signature; change vacation, forwarding and delegates; transfer Drive ownership.
- **Tasks**
  - **Quick tasks**: ready-made jobs such as offboarding a user, finding inactive accounts, 2-Step Verification status, sharing a mailbox and finding a Chromebook by serial number. Each card says what it does and which GAM command it runs.
- **Advanced**
  - **All commands**: every GAM command (1,049 command forms) with a generated form. See below.
  - **Console**: type GAM commands directly, with history and answers to GAM's prompts.
- **Settings**: paths, checks and first-time setup.

Every button has a tooltip saying what it does and which GAM command it runs. Anything that changes data opens a **Check before running** window with a plain-language summary and the exact command(s); Cancel is the default button, so pressing Enter never runs a change by accident.

## When something goes wrong

GAM's output is checked against known Google Workspace and GAM problems. When one matches, the app shows what it means, numbered steps to fix it, a link to the relevant Google or GAM help page, and where possible a button that runs the fix (for example **Re-authorize GAM**, **Enable GAM's APIs** or **Check the service account**). It also shows the line of output it recognized, so you can see why.

Recognized problems include: Classroom data access or service turned off, missing domain-wide delegation scopes, APIs disabled in GAM's Cloud project, expired or revoked authorization, missing setup files, missing OAuth scopes, missing admin privileges, a service not enabled for a user, quota limits, network and proxy errors, a wrong system clock, items that don't exist or already exist, password policy rejections, and command-line mistakes.

The rules live in `src/GamGui.Core/ErrorAdvisor.cs`. Each one pairs patterns taken from GAM's own messages with the explanation and fix, so adding a new one is a single entry.

## How All commands works

GAM ships `GamCommands.txt`, a reference that describes every command in a modified BNF notation. GAM Workbench parses that file and turns each command into a form:

- Keywords with synonyms (`create|add`) become dropdowns.
- `[optional]` parts become check boxes that reveal their contents.
- Alternatives become a dropdown that switches the form below it.
- `item*` and `item+` become lists with Add and Remove.
- Values like `<Boolean>`, `<CrOSAction>` or `<Locale>` whose definitions are keyword lists become dropdowns.
- Structured values like `<UserTypeEntity>` have a **Guided** button that builds a sub-form from their definition. Or type them directly, for example `ou /Sales` or `users a@x.com,b@x.com`.
- Hover over any value to see its definition.

The command line updates as you fill in the form. **Edit in console** hands it to the Console for manual changes, and **Open in terminal** runs it in a console window.

The reference used is the `GamCommands.txt` next to `gam.exe` when there is one, so forms track your GAM version. A copy from GAM 7 is built in as a fallback.

## Project layout

```
src/GamGui.Core    No UI and no NuGet dependencies.
  ErrorAdvisor.cs    Recognizes known errors and explains the fix
  Syntax.cs          GAM BNF parser (keyword synonyms bind tightly; tolerant of the
                     reference file's few unbalanced brackets)
  CommandCatalog.cs  Reads GamCommands.txt into sections, commands and definitions
  GamRunner.cs       Runs gam.exe: streaming output, cancel, stdin, GAMCFGDIR, terminal mode
  CsvTable.cs        GAM CSV output to DataTable
  ArgUtil.cs         Argument splitting and quoting
  AppSettings.cs     Settings (%APPDATA%\GamGui\settings.json), gam.exe locator, passwords

src/GamGui          WPF app
  App.xaml           Palette and control styles
  MainWindow         Navigation and command strip
  ResultView.cs      Grid or text output with filter, copy, export and error advice
  Dialogs.cs         Confirmation, results and advice windows
  Guides.cs          The "How to use this screen" text for every screen
  QuickTasks.cs      The Quick tasks catalog: what each task does and the commands it builds
  Builder/           Form generator for All commands
  Views/             One file per screen

tests/GamGui.Core.Tests   xUnit tests for the parser, error advisor and helpers (dotnet test)
```

Screens are built in C# rather than XAML so that mistakes surface as compile errors instead of runtime XAML errors.

## Adding a quick task

Quick tasks are data, not code paths. Add an entry to `QuickTasks.All` with a category, title, a one-sentence description, a "how it works" sentence naming the GAM command, the questions to ask, and a function that turns the answers into commands. Set `Changes: true` if it modifies anything; the confirmation window and progress report come for free.

## Notes

- Passwords entered in the app are passed to GAM as command-line arguments (as they would be at a command prompt) and are masked in the app's displays and history.
- Settings and console history are stored in `%APPDATA%\GamGui\settings.json`.
- GAM is licensed under Apache 2.0; its license is included as `GAM-LICENSE.txt`.
