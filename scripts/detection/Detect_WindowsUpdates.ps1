<#
Detection script for weekly Windows Update + BitLocker workflow

Exit 0 = Compliant (no remediation needed)
Exit 1 = Non-compliant (run remediation)

Triggers remediation if:
- A reboot is pending (WU)
- Updates are available
- BitLocker protection is currently OFF (suspended)
#>

$needsRemediation = $false
$reasons = @()

function Test-RebootPending {
    # Prefer WU SystemInfo COM (same approach used by Get-WURebootStatus internally) [1](https://learn.microsoft.com/en-us/windows-hardware/test/hlk/testref/954cf796-a640-4134-b742-eaf0ed2663ff)
    try {
        $sysInfo = New-Object -ComObject "Microsoft.Update.SystemInfo"
        if ($sysInfo.RebootRequired) { return $true }
    } catch {
        # Fallback: registry key commonly used to detect WU pending reboot [5](https://4sysops.com/archives/avoid-bitlocker-recovery-mode-by-customizing-the-tpm-validation-profile/)
        if (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") {
            return $true
        }
    }
    return $false
}

function Get-AvailableUpdateCount {
    # Uses Windows Update Agent API. Works without PSWindowsUpdate module.
    try {
        $session  = New-Object -ComObject "Microsoft.Update.Session"
        $searcher = $session.CreateUpdateSearcher()

        # Common criteria for "available updates"
        $criteria = "IsInstalled=0 and IsHidden=0"
        $result = $searcher.Search($criteria)

        if ($null -ne $result -and $null -ne $result.Updates) {
            return [int]$result.Updates.Count
        }
    } catch {
        # If WU service is disabled/broken, don’t force remediation solely on this.
        return -1
    }
    return 0
}

function Test-BitLockerSuspended {
    # If BitLocker is suspended, integrity validation is off until resume/reboots [3](https://learn.microsoft.com/en-us/answers/questions/364178/bitlocker-is-suspended-during-windows-updates-why)
    try {
        if (Get-Command -Name Get-BitLockerVolume -ErrorAction SilentlyContinue) {
            $blv = Get-BitLockerVolume -MountPoint "C:" -ErrorAction Stop
            # ProtectionStatus: On/Off
            return ($blv.ProtectionStatus -eq "Off")
        } else {
            $status = & manage-bde -status C: 2>$null
            if ($status -match "Protection Status:\s+Protection Off") { return $true }
        }
    } catch { }
    return $false
}

# 1) Reboot pending?
if (Test-RebootPending) {
    $needsRemediation = $true
    $reasons += "Pending reboot detected"
}

# 2) Updates available?
$updateCount = Get-AvailableUpdateCount
if ($updateCount -gt 0) {
    $needsRemediation = $true
    $reasons += "Updates available: $updateCount"
} elseif ($updateCount -eq -1) {
    $reasons += "Could not query updates (WU API error) — not forcing remediation on this alone"
}

# 3) BitLocker suspended?
if (Test-BitLockerSuspended) {
    # If BitLocker is suspended, we generally want remediation to run
    # because suspension persists until restart count is consumed or you manually resume [3](https://learn.microsoft.com/en-us/answers/questions/364178/bitlocker-is-suspended-during-windows-updates-why)[4](https://thewindowsupdate.com/2023/04/13/bitlocker-is-not-resuming-after-reboot-count-has-been-reached/)
    $needsRemediation = $true
    $reasons += "BitLocker protection is OFF (suspended)"
}

if ($needsRemediation) {
    Write-Output ("NON-COMPLIANT: " + ($reasons -join "; "))
    exit 1
} else {
    Write-Output "COMPLIANT: No updates available, no reboot pending, BitLocker protection ON."
    exit 0
}
