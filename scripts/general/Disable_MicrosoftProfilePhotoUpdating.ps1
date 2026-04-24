<# 

Use Powershell 7+ for better performance and compatibility

#>
# Install Powershell 7+ if not already installed.
winget install --id Microsoft.Powershell --source winget -e

# Install EXO V2 module if not already installed
if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
    Install-Module -Name ExchangeOnlineManagement -Force
}
# Connect to Exchange Online
Connect-ExchangeOnline

# Connect MG Graph
Connect-MgGraph -Scopes "GroupMember.Read.All", "User.Read.All"

# Create OWA Policy or edit your current policy. Only 1 policy can be assigned at any given time. You should at the least have a policy for staff and a policy for students
New-OwaMailboxPolicy -Name "NoPhotoChangePolicy"
Set-OwaMailboxPolicy -Identity "NoPhotoChangePolicy" -SetPhotoEnabled $false

# Apply to M365 group or Security Group Members one time.  To automate, consider using a scheduled task or Azure Automation to run this script periodically.

$groupName = "Students"
$group = Get-MgGroup -Filter "displayName eq '$groupName'"
$members = Get-MgGroupMember -GroupId $group.Id -All

foreach ($member in $members) {
    $user = Get-MgUser -UserId $member.Id -ErrorAction SilentlyContinue
    if ($user -and $user.UserPrincipalName) {
        try {
            Set-CASMailbox -Identity $user.UserPrincipalName -OwaMailboxPolicy "NoPhotoChangePolicy"
            Write-Host "Applied policy to $($user.UserPrincipalName)" -ForegroundColor Green
        } catch {
            Write-Host "Failed for $($user.UserPrincipalName): $_" -ForegroundColor Red
        }
    }
}

<# OR ------------------------------------------------------------
# Apply to distribution group or mail-enabled security group members
$members = Get-DistributionGroupMember -Identity "Your Group Name" -ResultSize Unlimited

foreach ($member in $members) {
    try {
        Set-CASMailbox -Identity $member.PrimarySmtpAddress -OwaMailboxPolicy "NoPhotoChangePolicy"
        Write-Host "Applied policy to $($member.PrimarySmtpAddress)" -ForegroundColor Green
    } catch {
        Write-Host "Failed for $($member.PrimarySmtpAddress): $_" -ForegroundColor Red
    }
}

#Verify
Get-CASMailbox -ResultSize Unlimited | Where-Object { $_.OwaMailboxPolicy -eq "NoPhotoChangePolicy" } | Select-Object Name, PrimarySmtpAddress
#>
#----------------------------------------------------------------------------

#Remove photos from existing group users.

$groupName = "Students"
$group = Get-MgGroup -Filter "displayName eq '$groupName'"
$members = Get-MgGroupMember -GroupId $group.Id -All

foreach ($member in $members) {
    $user = Get-MgUser -UserId $member.Id -ErrorAction SilentlyContinue
    if ($user) {
        try {
            Remove-MgUserPhoto -UserId $user.Id -ErrorAction Stop
            Write-Host "Removed photo for $($user.UserPrincipalName)" -ForegroundColor Green
        } catch {
            if ($_.Exception.Message -like "*not found*" -or $_.Exception.Message -like "*404*") {
                Write-Host "No photo to remove for $($user.UserPrincipalName)" -ForegroundColor Yellow
            } else {
                Write-Host "Failed for $($user.UserPrincipalName): $_" -ForegroundColor Red
            }
        }
    }
}
