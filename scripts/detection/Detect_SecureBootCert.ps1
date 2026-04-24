$path = "HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot"
$servicingPath = "HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing"

$expected = @{
    "EnableSecureBootUpdates" = 1
    "HighConfidenceOptOut"    = 0
}

Write-Output "Checking Secure Boot settings on $(Get-Date)"

if (-not (Test-Path $path)) {
    Write-Output "Path $path does not exist"
    #exit 1
}

$result = $true
$issues = @()

foreach ($key in $expected.Keys) {
    $currentValue = (Get-ItemProperty -Path $path -Name $key -ErrorAction SilentlyContinue).$key
    if ($null -eq $currentValue) {
        Write-Output "MISSING: $key"
        $issues += "Missing: $key"
        $result = $false
    } elseif ($currentValue -ne $expected[$key]) {
        Write-Output "WRONG: $key = $currentValue (expected $($expected[$key]))"
        $issues += "Wrong: $key = $currentValue"
        $result = $false
    } else {
        Write-Output "OK: $key = $currentValue"
    }
}

# Flag if old MicrosoftUpdateManagedOptIn is still present
$optIn = (Get-ItemProperty -Path $path -Name "MicrosoftUpdateManagedOptIn" -ErrorAction SilentlyContinue).MicrosoftUpdateManagedOptIn
if ($null -ne $optIn) {
    Write-Output "CLEANUP NEEDED: MicrosoftUpdateManagedOptIn still present (value: $optIn)"
    $issues += "MicrosoftUpdateManagedOptIn needs removal"
    $result = $false
}

# Report deployment progress
$avail = (Get-ItemProperty -Path $path -Name "AvailableUpdates" -ErrorAction SilentlyContinue).AvailableUpdates
$availPolicy = (Get-ItemProperty -Path $path -Name "AvailableUpdatesPolicy" -ErrorAction SilentlyContinue).AvailableUpdatesPolicy
Write-Output "AvailableUpdates: $(if ($null -ne $avail) { '0x{0:X}' -f $avail } else { 'not set' })"
Write-Output "AvailableUpdatesPolicy: $(if ($null -ne $availPolicy) { '0x{0:X}' -f $availPolicy } else { 'not set' })"

if (Test-Path $servicingPath) {
    $status = (Get-ItemProperty -Path $servicingPath -Name "UEFICA2023Status" -ErrorAction SilentlyContinue).UEFICA2023Status
    $capable = (Get-ItemProperty -Path $servicingPath -Name "WindowsUEFICA2023Capable" -ErrorAction SilentlyContinue).WindowsUEFICA2023Capable
    $error2023 = (Get-ItemProperty -Path $servicingPath -Name "UEFICA2023Error" -ErrorAction SilentlyContinue).UEFICA2023Error
    Write-Output "UEFICA2023Status: $status"
    Write-Output "WindowsUEFICA2023Capable: $capable"
    if ($null -ne $error2023) { Write-Output "UEFICA2023Error: $error2023" }
} else {
    Write-Output "Servicing path not found - updates may not have started processing yet"
}

if ($result) {
    Write-Output "All settings compliant"
    exit 0
} else {
    Write-Output "Issues found: $($issues -join '; ')"
    exit 1
}
