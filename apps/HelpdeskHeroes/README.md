# Helpdesk Heroes

A small WinForms launcher for end-user helpdesk tickets. Three buttons: email the helpdesk, browse the FAQs, or get unstuck via the **Get Help** wizard.

Outgoing tickets automatically include a small device fingerprint (computer name, BIOS serial, signed-in username, Wi-Fi SSID, IPv4, OS, local time, app version, open browser tabs, foreground window + visible dialogs, open apps) so the helpdesk can identify the device without making the user go hunting for it.

## Before you build: configure for your organization

This repo ships with placeholder values everywhere your organization, domain, recipient email, and FAQ URL would normally go. Replace them before publishing or distributing the EXE. Search for the `TODO` markers, or use the table below.

| Where                                      | What                                                            | Placeholder                              |
|--------------------------------------------|-----------------------------------------------------------------|------------------------------------------|
| `MainForm.cs` → `FaqUrl`                   | URL opened by the "Browse the FAQs" button                      | `https://example.com/helpdesk-faqs`      |
| `GetHelpForm.cs` → `FaqUrl`                | Same URL, used inside the Get Help wizard                       | `https://example.com/helpdesk-faqs`      |
| `Configuration.cs` → `DefaultSenderDomain` | Domain appended to bare SAM names to build the From address     | `example.com`                            |
| `Configuration.cs` → `DefaultHelpdeskTo`   | Recipient address tickets are sent to                           | `helpdesk@example.com`                   |
| `HelpdeskHeroes.csproj` → `<Company>` / `<Copyright>` | Embedded in the EXE header                           | `Your Organization`                      |

The `Default*` values in `Configuration.cs` are fallbacks only — at runtime the app reads the real values from the registry (see "Runtime configuration" below), so the production deployment never has to commit real values to source. The defaults exist so a dev build still launches without a registry key in place.

You'll also want to provide your own SMTP2GO API key (see "How email actually leaves the device") and a `HelpdeskHeroes.ico` for branding (the csproj references it as `<ApplicationIcon>` and as an `EmbeddedResource`).

## Attachments

Both the wizard and the Quick Ticket flow let users attach files (file picker) or paste an image from the clipboard (e.g. a snip taken with **Win + Shift + S**). Constraints:

- **15 MB per file** ceiling
- **18 MB total** across all attachments — sized so the base64-inflated MIME message stays under Exchange Online's 25 MB inbound limit with headroom for body + headers
- **10 file** maximum
- Pasted screenshots are written to `%TEMP%` as PNG and cleaned up automatically when the wizard / quick dialog closes

## Links open in Microsoft Edge — strictly

Every URL the app opens (FAQ, helpdesk site, etc.) is launched explicitly through `msedge.exe`. The launcher resolves Edge in this order:

1. `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe`
2. `HKLM\SOFTWARE\Clients\StartMenuInternet\Microsoft Edge\shell\open\command`
3. Well-known install paths under `Program Files (x86)` and `Program Files`

If none of those resolve, the user gets a small dialog with the URL in a copy-friendly textbox and a note to ask the helpdesk about a missing Edge install. **There is no silent fallback to the default browser** — the typical reason to use this app is that you rely on Edge for SSO, content filtering, and your M365 tenant, and falling back to whatever happens to be the default could land users in a wrong-tenant Chrome session. If that constraint doesn't apply to your environment, swap `EdgeLauncher.Open(...)` for `Process.Start` with `UseShellExecute = true` and you'll get the system default browser instead.

## How email actually leaves the device

The app posts to the [SMTP2GO HTTP API](https://apidoc.smtp2go.com/) — there's **no Outlook profile, no Office, no SMTP client config required on the user device**. The send happens silently in the background (typical: <1 s; hard timeout: 15 s). If the network is down or the API rejects the request, the user is shown a "couldn't send" page with the rendered ticket in a copy-paste box and instructions to email the helpdesk manually.

The user sees nothing about API keys. The From line is the user's own email address (resolved via UPN with a `username@<SenderDomain>` fallback), so helpdesk replies go straight to the user's inbox.

You'll need an SMTP2GO account and an API key. Get one at [smtp2go.com](https://www.smtp2go.com/) and provision it via the registry (see below). If you'd rather use a different transport (SendGrid, Postmark, Graph API, raw SMTP), `EmailSender.cs` is the only file that needs to change.

### HTML body

Emails are sent with both an HTML body (`html_body`) and a plain-text body (`text_body`) in the same SMTP2GO request — multipart/alternative wire format, recipient's mail client picks. The HTML version uses inline-styled tables (the only layout primitive Outlook reliably renders) and the brand-blue accent (`#005A9E`) used throughout the app UI. The on-screen snapshot section — foreground window + visible dialogs at send time — is rendered inside a tinted callout box because that's the section helpdesk staff use most.

The plain-text body is what the user sees in the SentPage failure-mode "copy this manually" textbox if the SMTP2GO POST fails. We always build both because pasting HTML source into a Gmail compose window is brutal.

## Runtime configuration (the registry, not the EXE)

Three values are read from `HKLM\SOFTWARE\HelpdeskHeroes` at every launch:

| Value name          | What it is                                                       |
|---------------------|------------------------------------------------------------------|
| `Smtp2GoApiKey`     | Your SMTP2GO API key                                             |
| `SenderDomain`      | Domain to append to bare SAM names (e.g. `example.com`)          |
| `HelpdeskRecipient` | The To address (e.g. `helpdesk@example.com`)                     |
| `InstalledVersion`  | Stamped by your deployment script for auditing (optional)        |

The intended pattern is to write these via a deployment script running as SYSTEM (Intune remediation, SCCM, GPO, etc.) so the API key never lives in the EXE bytes and rotation is a config-policy redeploy — no rebuild.

**Threat-model note:** `HKLM\SOFTWARE` is readable by all logged-in users by default. A determined user CAN read these values via regedit. The real defense lives on the SMTP2GO side: lock the API key to a single sender domain, IP-allowlist your egress ranges if known, and set a tight per-day rate limit so abuse trips alarms.

## Prerequisites

- Windows machine for building (WinForms requires Windows)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)
- VS Code with the **C# Dev Kit** extension (or Visual Studio)

Verify the SDK after install:

```
dotnet --version
```

## Open in VS Code

`File → Open Folder…` and select the `HelpdeskHeroes` folder. The C# Dev Kit will auto-restore packages.

## Build (debug)

```
dotnet build
```

Output: `bin\Debug\net8.0-windows\HelpdeskHeroes.exe`

A debug build with no `HKLM\SOFTWARE\HelpdeskHeroes` registry key in place will launch fine, but the email send will surface a "config missing" error in the SentPage. Either run your deployment script locally to populate the registry, or add the key manually for dev:

```powershell
New-Item -Path 'HKLM:\SOFTWARE\HelpdeskHeroes' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\HelpdeskHeroes' -Name 'Smtp2GoApiKey'     -Value 'api-XXXXXXXX'         -Type String
Set-ItemProperty -Path 'HKLM:\SOFTWARE\HelpdeskHeroes' -Name 'SenderDomain'      -Value 'example.com'          -Type String
Set-ItemProperty -Path 'HKLM:\SOFTWARE\HelpdeskHeroes' -Name 'HelpdeskRecipient' -Value 'helpdesk@example.com' -Type String
```

Replace the placeholder values with your real API key, domain, and recipient.

## Run / debug

Press `F5` in VS Code. Choose **C#** the first time it prompts for a debugger.

## Publish a deployable single-file .exe

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin\Release\net8.0-windows\win-x64\publish\HelpdeskHeroes.exe`

This is one self-contained executable (~150 MB) with no .NET runtime required on the user device. Upload to your secure document store and reference that URL from your deployment script.

## Releasing a new version

1. Edit `<Version>` and `<FileVersion>` in `HelpdeskHeroes.csproj` (e.g. `1.1.0` → `1.2.0` and `1.1.0.0` → `1.2.0.0`).
2. `dotnet publish` (command above).
3. Upload the new `HelpdeskHeroes.exe` to your secure document store.
4. Bump the matching baseline version in your deployment / detection scripts before pushing the new release.

If you use Intune-style detection + remediation scripts (not included in this repo — write your own), the detection script typically fails (and triggers remediation) when:
- the EXE is missing,
- the EXE FileVersion is older than the expected baseline,
- the desktop shortcut is missing or points to the wrong target,
- any of the required registry values is missing or empty.

A robust remediation script will also verify the downloaded EXE's FileVersion matches the expected version **before** overwriting the installed copy — so a stale CDN or a half-uploaded release can't corrupt the install.

## Common gotchas

- **`NETSDK1100: Windows is required`** — you're not on Windows. WinForms must be built on Windows.
- **Red squiggles everywhere after restore** — `Ctrl+Shift+P → .NET: Restart Language Server`.
- **`type 'Form' could not be found`** — delete `bin` and `obj`, reopen VS Code, build again.
- **Send always fails with "API key not configured"** — the registry values aren't set. Run your deployment script locally as Administrator, or set them manually (see Build section above).
- **Detection script reports OUTDATED on a fresh install** — `<FileVersion>` in the csproj didn't get bumped, or you bumped it but didn't republish. Verify with: `(Get-Item 'C:\Program Files\HelpdeskHeroes\HelpdeskHeroes.exe').VersionInfo.FileVersion`

