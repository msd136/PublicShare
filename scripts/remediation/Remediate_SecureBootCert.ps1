$path = "HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot"

# Clean up MicrosoftUpdateManagedOptIn from previous script
$path = "HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot"
$existingOptIn = (Get-ItemProperty $path -Name "MicrosoftUpdateManagedOptIn" -ErrorAction SilentlyContinue).MicrosoftUpdateManagedOptIn
if ($null -ne $existingOptIn) {
    Remove-ItemProperty -Path $path -Name "MicrosoftUpdateManagedOptIn" -Force
    Write-Output "Removed MicrosoftUpdateManagedOptIn (was $existingOptIn)"
}

Write-Output "Starting Secure Boot registry check on $(Get-Date)"

try {
    if (-not (Test-Path $path)) {
        New-Item -Path $path -Force | Out-Null
    }

    # Only set the policy knobs — AvailableUpdatesPolicy is handled by Intune
    $settings = @{
        "EnableSecureBootUpdates" = 1
        "HighConfidenceOptOut"    = 0
    }

    foreach ($key in $settings.Keys) {
        $oldValue = (Get-ItemProperty $path -Name $key -ErrorAction SilentlyContinue).$key
        Set-ItemProperty -Path $path -Name $key -Value $settings[$key] -Type DWord -Force
        $newValue = (Get-ItemProperty $path -Name $key).$key
        Write-Output "Set $key : $oldValue -> $newValue"
    }

    # Kick the Secure Boot scheduled task instead of general Windows Update
    Start-ScheduledTask -TaskName "\Microsoft\Windows\PI\Secure-Boot-Update" -ErrorAction SilentlyContinue
    Write-Output "Triggered Secure-Boot-Update scheduled task"

    # Report current status
    $servicing = "HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing"
    if (Test-Path $servicing) {
        $status = (Get-ItemProperty $servicing -Name "UEFICA2023Status" -ErrorAction SilentlyContinue).UEFICA2023Status
        $capable = (Get-ItemProperty $servicing -Name "WindowsUEFICA2023Capable" -ErrorAction SilentlyContinue).WindowsUEFICA2023Capable
        Write-Output "UEFICA2023Status: $status"
        Write-Output "WindowsUEFICA2023Capable: $capable"
    }

    $avail = (Get-ItemProperty $path -Name "AvailableUpdates" -ErrorAction SilentlyContinue).AvailableUpdates
    Write-Output "AvailableUpdates: 0x$("{0:X}" -f $avail)"

    Write-Output "Completed at $(Get-Date)"
    exit 0
} catch {
    Write-Output "ERROR: $($_.Exception.Message)"
    exit 1
}
