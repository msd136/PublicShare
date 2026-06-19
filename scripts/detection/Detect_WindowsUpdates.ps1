# ============================================================
# Windows Update Remediation — End-User Aware Restart
# Melba School District | Intune Remediation Script
#
# Changes from previous version:
#   - Replaced Get-WmiObject with Get-CimInstance / Invoke-CimMethod
#   - User detection now uses explorer.exe ownership (more reliable
#     than Win32_ComputerSystem.UserName, especially from SYSTEM)
#   - Added file logging for Intune diagnostics
#   - Active-session filtering for multi-session machines (RDP, etc.)
# ============================================================

# --------------------------------------------------
# SECTION 1: Pre-flight
# --------------------------------------------------
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin) {
    Write-Output "ERROR: Must run as Administrator."
    exit 1
}

# Logging — Intune remediations are hard to debug without persisted logs
$LogPath = "C:\Windows\Temp\WU_Remediation.log"
function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "$ts  [$Level]  $Message"
    try { $line | Out-File -FilePath $LogPath -Append -Encoding UTF8 -ErrorAction SilentlyContinue } catch {}
    Write-Output $Message
}

Write-Log "=== WU Remediation started (PID $PID, user $env:USERNAME) ==="

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ((Get-ExecutionPolicy) -ne 'RemoteSigned') {
    Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Force
}

Set-PSRepository -Name "PSGallery" -InstallationPolicy Trusted

if (-not (Get-PackageProvider -Name "NuGet" -ErrorAction SilentlyContinue)) {
    Install-PackageProvider -Name "NuGet" -RequiredVersion 2.8.5.208 -Force -Scope AllUsers
}
Import-PackageProvider -Name "NuGet" -Force

if (-not (Get-Module -ListAvailable -Name PSWindowsUpdate)) {
    Install-Module -Name PSWindowsUpdate -Force
}
Import-Module PSWindowsUpdate

# --------------------------------------------------
# SECTION 2: Install updates (no auto-reboot)
# --------------------------------------------------
try {
    Write-Log "Starting Windows Update install pass."
    Get-WindowsUpdate -Install -AcceptAll -Verbose
} catch {
    Write-Log "Update failed — resetting WU components and retrying. Error: $($_.Exception.Message)" "WARN"
    Reset-WUComponents
    Get-WindowsUpdate -Install -AcceptAll -Verbose
}

# --------------------------------------------------
# SECTION 3: Reboot check
# --------------------------------------------------
$rebootRequired = $false
try {
    $rebootRequired = Get-WURebootStatus -Silent
} catch {
    Write-Log "Could not determine reboot status via Get-WURebootStatus." "WARN"
}

# Shared file paths (C:\Windows\Temp is writable by SYSTEM and readable by users)
$ResultFile   = "C:\Windows\Temp\wu_reboot_decision.txt"
$PostponeFlag = "C:\Windows\Temp\wu_postponed.flag"
$NotifyScript = "C:\Windows\Temp\WU_NotifyUser.ps1"
$TaskName     = "MelbaSD_WU_Notify"

if (-not $rebootRequired) {
    Write-Log "No reboot required. Ensuring BitLocker is active."
    try   { Resume-BitLocker -MountPoint "C:" }
    catch { manage-bde -protectors -enable C: | Out-Null }
    Remove-Item $PostponeFlag -Force -ErrorAction SilentlyContinue
    exit 0
}

Write-Log "Reboot required. Identifying interactive user."

# --------------------------------------------------
# SECTION 4: Identify logged-in user via explorer.exe owner
# --------------------------------------------------
# Why explorer.exe and not Win32_ComputerSystem.UserName?
#   - explorer.exe only runs in interactive user sessions
#   - Win32_ComputerSystem.UserName is frequently $null from SYSTEM context
#   - Multiple explorer.exe instances means multiple users — we pick
#     the one whose session ID matches the "Active" console session.
function Get-InteractiveUser {
    try {
        $explorerProcs = Get-CimInstance -ClassName Win32_Process -Filter "Name='explorer.exe'" -ErrorAction Stop
        if (-not $explorerProcs) {
            Write-Log "No explorer.exe processes found — likely no interactive session."
            return $null
        }

        # Find the active console session ID (handles multi-session machines)
        $activeSessionId = $null
        try {
            $queryUser = & query.exe user 2>$null
            if ($queryUser) {
                $activeLine = $queryUser | Select-Object -Skip 1 | Where-Object { $_ -match '\sActive\s' } | Select-Object -First 1
                if ($activeLine -match '\s+(\d+)\s+Active') {
                    $activeSessionId = [int]$Matches[1]
                    Write-Log "Active console session ID: $activeSessionId"
                }
            }
        } catch {
            Write-Log "query.exe failed; will fall back to first explorer.exe owner." "WARN"
        }

        # Prefer explorer in the active session; fall back to any non-system explorer
        $candidates = @()
        foreach ($proc in $explorerProcs) {
            $owner = Invoke-CimMethod -InputObject $proc -MethodName GetOwner -ErrorAction SilentlyContinue
            if ($owner.ReturnValue -ne 0 -or -not $owner.User) { continue }
            if ($owner.User -in @('SYSTEM','LOCAL SERVICE','NETWORK SERVICE')) { continue }

            $candidates += [PSCustomObject]@{
                User      = $owner.User
                Domain    = $owner.Domain
                Full      = "$($owner.Domain)\$($owner.User)"
                SessionId = $proc.SessionId
            }
        }

        if (-not $candidates) { return $null }

        if ($activeSessionId -ne $null) {
            $match = $candidates | Where-Object { $_.SessionId -eq $activeSessionId } | Select-Object -First 1
            if ($match) { return $match }
        }
        return $candidates | Select-Object -First 1
    } catch {
        Write-Log "Interactive user lookup failed: $($_.Exception.Message)" "WARN"
        return $null
    }
}

$userInfo     = Get-InteractiveUser
$domainUser   = $null   # DOMAIN\username  — used for scheduled task principal
$loggedInUser = $null   # username only

if ($userInfo) {
    $loggedInUser = $userInfo.User
    $domainUser   = $userInfo.Full
    Write-Log "Active interactive user: $domainUser (session $($userInfo.SessionId))"
}

# --------------------------------------------------
# SECTION 5: Helper — suspend BitLocker and reboot
# --------------------------------------------------
function Invoke-SafeReboot {
    Write-Log "Suspending BitLocker and rebooting."
    try {
        if (Get-Command Suspend-BitLocker -ErrorAction SilentlyContinue) {
            Suspend-BitLocker -MountPoint "C:" -RebootCount 2
        } else {
            manage-bde -protectors -disable C: -RebootCount 2 | Out-Null
        }
    } catch {
        Write-Log "BitLocker suspension failed — recovery prompt may appear after restart." "WARN"
    }
    shutdown.exe /a 2>$null
    Restart-Computer -Force
}

# --------------------------------------------------
# SECTION 6: No user logged in — silent reboot
# --------------------------------------------------
if (-not $loggedInUser) {
    Write-Log "No active user session detected. Performing silent reboot."
    Invoke-SafeReboot
    exit 0
}

# --------------------------------------------------
# SECTION 7: Build WinForms notification script
# --------------------------------------------------
# NOTE: Single-quoted here-strings (@'...'@) are used intentionally.
#       No expansion happens here — the content is written as-is to a .ps1
#       file and evaluated fresh when PowerShell executes it in user context.

$alreadyPostponed = Test-Path $PostponeFlag

if ($alreadyPostponed) {

# --- FINAL NOTICE (postpone already used — no postpone button) ---
$notifyContent = @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$rf = 'C:\Windows\Temp\wu_reboot_decision.txt'

$form                  = New-Object System.Windows.Forms.Form
$form.Text             = 'IT Notice — Final Restart Warning'
$form.Size             = New-Object System.Drawing.Size(460, 220)
$form.StartPosition    = 'CenterScreen'
$form.TopMost          = $true
$form.FormBorderStyle  = 'FixedDialog'
$form.MaximizeBox      = $false
$form.MinimizeBox      = $false
$form.BackColor        = [System.Drawing.Color]::WhiteSmoke

$pic          = New-Object System.Windows.Forms.PictureBox
$pic.Image    = [System.Drawing.SystemIcons]::Warning.ToBitmap()
$pic.Size     = New-Object System.Drawing.Size(32, 32)
$pic.Location = New-Object System.Drawing.Point(12, 12)
$pic.SizeMode = 'StretchImage'
$form.Controls.Add($pic)

$lbl          = New-Object System.Windows.Forms.Label
$lbl.Text     = "FINAL NOTICE: Your postponement period has ended.`r`n`r`nThis computer will restart automatically to finish installing Windows updates.`r`nPlease save your work immediately."
$lbl.Location = New-Object System.Drawing.Point(52, 12)
$lbl.Size     = New-Object System.Drawing.Size(390, 90)
$lbl.Font     = New-Object System.Drawing.Font('Segoe UI', 9)
$form.Controls.Add($lbl)

$btn          = New-Object System.Windows.Forms.Button
$btn.Text     = 'Restart Now'
$btn.Location = New-Object System.Drawing.Point(165, 135)
$btn.Size     = New-Object System.Drawing.Size(130, 38)
$btn.BackColor = [System.Drawing.Color]::FromArgb(196, 43, 28)
$btn.ForeColor = [System.Drawing.Color]::White
$btn.FlatStyle = 'Flat'
$btn.Add_Click({ 'REBOOT' | Out-File $rf -Force; $form.Close() })
$form.Controls.Add($btn)

$script:cd = 120   # 2-minute auto-close on final notice
$t = New-Object System.Windows.Forms.Timer
$t.Interval = 1000
$t.Add_Tick({
    $script:cd--
    $form.Text = "IT Notice — Final Warning (restarting in $($script:cd)s)"
    if ($script:cd -le 0) { $t.Stop(); 'TIMEOUT' | Out-File $rf -Force; $form.Close() }
})
$t.Start()
[void]$form.ShowDialog()
'@

} else {

# --- FIRST NOTICE (postpone available) ---
$notifyContent = @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$rf = 'C:\Windows\Temp\wu_reboot_decision.txt'

$form                  = New-Object System.Windows.Forms.Form
$form.Text             = 'IT Notice — Restart Required'
$form.Size             = New-Object System.Drawing.Size(490, 260)
$form.StartPosition    = 'CenterScreen'
$form.TopMost          = $true
$form.FormBorderStyle  = 'FixedDialog'
$form.MaximizeBox      = $false
$form.MinimizeBox      = $false
$form.BackColor        = [System.Drawing.Color]::WhiteSmoke

$pic          = New-Object System.Windows.Forms.PictureBox
$pic.Image    = [System.Drawing.SystemIcons]::Information.ToBitmap()
$pic.Size     = New-Object System.Drawing.Size(32, 32)
$pic.Location = New-Object System.Drawing.Point(12, 12)
$pic.SizeMode = 'StretchImage'
$form.Controls.Add($pic)

$lbl          = New-Object System.Windows.Forms.Label
$lbl.Text     = "Windows updates have been installed and require a restart.`r`n`r`nYou may postpone this restart ONCE for up to 60 minutes.`r`nIf no action is taken within 5 minutes, this computer will restart automatically.`r`n`r`nPlease save your work now."
$lbl.Location = New-Object System.Drawing.Point(52, 10)
$lbl.Size     = New-Object System.Drawing.Size(425, 115)
$lbl.Font     = New-Object System.Drawing.Font('Segoe UI', 9)
$form.Controls.Add($lbl)

$btnP          = New-Object System.Windows.Forms.Button
$btnP.Text     = 'Postpone 60 min'
$btnP.Location = New-Object System.Drawing.Point(70, 170)
$btnP.Size     = New-Object System.Drawing.Size(150, 40)
$btnP.BackColor = [System.Drawing.Color]::FromArgb(0, 120, 212)
$btnP.ForeColor = [System.Drawing.Color]::White
$btnP.FlatStyle = 'Flat'
$btnP.Add_Click({ 'POSTPONE' | Out-File $rf -Force; $form.Close() })
$form.Controls.Add($btnP)

$btnR          = New-Object System.Windows.Forms.Button
$btnR.Text     = 'Restart Now'
$btnR.Location = New-Object System.Drawing.Point(270, 170)
$btnR.Size     = New-Object System.Drawing.Size(150, 40)
$btnR.BackColor = [System.Drawing.Color]::FromArgb(196, 43, 28)
$btnR.ForeColor = [System.Drawing.Color]::White
$btnR.FlatStyle = 'Flat'
$btnR.Add_Click({ 'REBOOT' | Out-File $rf -Force; $form.Close() })
$form.Controls.Add($btnR)

$script:cd = 300   # 5-minute auto-close on first notice
$t = New-Object System.Windows.Forms.Timer
$t.Interval = 1000
$t.Add_Tick({
    $script:cd--
    $form.Text = "IT Notice — Restart Required (auto-restart in $($script:cd)s)"
    if ($script:cd -le 0) { $t.Stop(); 'TIMEOUT' | Out-File $rf -Force; $form.Close() }
})
$t.Start()
[void]$form.ShowDialog()
'@

}

# --------------------------------------------------
# SECTION 8: Write script and launch in user session
# --------------------------------------------------
$notifyContent | Out-File -FilePath $NotifyScript -Encoding UTF8 -Force
Remove-Item $ResultFile -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

$action    = New-ScheduledTaskAction -Execute "powershell.exe" `
               -Argument "-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$NotifyScript`""
$principal = New-ScheduledTaskPrincipal -UserId $domainUser -RunLevel Limited
$settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Log "Notification scheduled task launched for $domainUser."

# Poll for result — allow up to 7 minutes (dialog max is 5 min + buffer)
$pollEnd = (Get-Date).AddSeconds(420)
while ((Get-Date) -lt $pollEnd) {
    if (Test-Path $ResultFile) { break }
    Start-Sleep -Seconds 5
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item $NotifyScript -Force -ErrorAction SilentlyContinue

$decision = $null
if (Test-Path $ResultFile) {
    $decision = (Get-Content $ResultFile -Raw).Trim()
    Remove-Item $ResultFile -Force -ErrorAction SilentlyContinue
}
if (-not $decision) { $decision = "TIMEOUT" }
Write-Log "User decision: $decision"

# --------------------------------------------------
# SECTION 9: Act on user decision
# --------------------------------------------------
switch ($decision) {

    "POSTPONE" {
        Write-Log "User postponed restart. Scheduling OS-level restart in 60 minutes."
        "postponed" | Out-File $PostponeFlag -Force

        try {
            if (Get-Command Suspend-BitLocker -ErrorAction SilentlyContinue) {
                Suspend-BitLocker -MountPoint "C:" -RebootCount 2
            } else {
                manage-bde -protectors -disable C: -RebootCount 2 | Out-Null
            }
        } catch {
            Write-Log "BitLocker suspension failed." "WARN"
        }

        shutdown.exe /a 2>$null
        shutdown.exe /r /t 3600 /c "Melba SD IT: Your computer will restart in 60 minutes to complete Windows updates. Please save your work."

        Write-Log "Restart scheduled in 60 minutes. Script exiting cleanly."
        exit 0
    }

    { $_ -in "REBOOT", "TIMEOUT" } {
        Write-Log "Proceeding with immediate restart (decision: $decision)."
        Remove-Item $PostponeFlag -Force -ErrorAction SilentlyContinue
        Invoke-SafeReboot
    }
}

exit 0
