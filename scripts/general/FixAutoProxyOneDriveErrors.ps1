<# 
This will correct problems with OneDrive prompting with proxy or network connection errors where you don't have proxy configured (errors 2606 and 2603, respectively)
Run in system context.  If users are not logged off, their hives are already present in HKU\<sid>
#>

# Get all user profile paths from the registry (skips system profiles)

$profiles = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\*" |
    Where-Object { $_.ProfileImagePath -like "C:\Users\*" } |
    Select-Object -ExpandProperty ProfileImagePath

foreach ($profile in $profiles) {
    $ntuser = Join-Path $profile "NTUSER.DAT"
    $username = Split-Path $profile -Leaf

    if (!(Test-Path $ntuser)) {
        Write-Host "Skipping $username - no NTUSER.DAT found"
        continue
    }

    Write-Host "Processing $username..."

    # Load the hive
    $result = reg load "HKU\TempHive" "$ntuser" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Could not load hive for $username (probably logged in) - skipping"
        continue
    }

    # Delete the Connections key
    reg delete "HKU\TempHive\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections" /f 2>$null

    # Enable TLS 1.0, 1.1, 1.2
    reg add "HKU\TempHive\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v SecureProtocols /t REG_DWORD /d 0xA80 /f

    # Enable auto-detect, disable manual proxy
    reg add "HKU\TempHive\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v AutoDetect /t REG_DWORD /d 1 /f
    reg add "HKU\TempHive\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v ProxyEnable /t REG_DWORD /d 0 /f
    reg delete "HKU\TempHive\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v ProxyServer /f 2>$null
    reg delete "HKU\TempHive\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v AutoConfigURL /f 2>$null

    # Unload the hive
    [gc]::Collect()
    Start-Sleep -Seconds 1
    reg unload "HKU\TempHive"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  WARNING: Failed to unload hive for $username - may need manual cleanup"
    } else {
        Write-Host "  Done with $username"
    }
}

Write-Host "`nAll profiles processed. Rebooting in 10 seconds..."
shutdown /r /t 10 /f
