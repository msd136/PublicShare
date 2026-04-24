# This script resolves issues with MS Teams caching old information.  Best to be used as a log off script as it deletes appdata information.

$users = Get-ChildItem C:\Users |?{$_.name -notlike '*Public*' -and $_.name -notlike '*Default*'}
$kill = Stop-Process -Name "*teams" -Force
ForEach($user in $users) {
Remove-Item "$($user.fullname)\AppData\Roaming\\Microsoft\Teams" -Recurse
}
