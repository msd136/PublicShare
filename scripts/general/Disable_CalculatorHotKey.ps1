# Disables the calculator app key on the keyboard to prevent it from launching the calculator app when pressed. This is done by setting the ShellExecution value to null in the registry key for the calculator app key.

$PackageName = "TurnOffCalculatorAppKey"
$Path_local = "C:\ProgramData\Microsoft\IntuneManagementExtension\Logs"
$Path = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AppKey\18"
$Name = "ShellExecution"
$value = "$null"

Start-Transcript -Path "$Path_local\$PackageName-install.log" -Force
If (!(Test-Path $Path))
 {  New-Item -Path $Path -Force | Out-Null
    New-ItemProperty -Path $Path -Name $Name -Value $value -PropertyType String -Force | Out-Null
}ELSE{ New-ItemProperty -Path $Path -Name $Name -Value $value -PropertyType String -Force | Out-Null}
Stop-Transcript
#end
