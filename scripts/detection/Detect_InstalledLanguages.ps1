<#
Before using this, be sure to uncomment the exit codes and add any languages you may need to check for.
#>

# Define the language tags
$requiredLanguages = @("en-US", "es-MX")

# Get the list of installed languages
$installedLanguages = Get-WinUserLanguageList

# Get installed language tags
$installedLanguageTags = $installedLanguages.LanguageTag

# Check which required languages are installed
$missingLanguages = $requiredLanguages | Where-Object { $_ -notin $installedLanguageTags }

if ($missingLanguages.Count -eq 0) {
    Write-Output "Both English (United States) and Spanish (Mexico) languages are installed."
    # exit 0
}
elseif ($missingLanguages.Count -eq 1) {
    Write-Output "Only one of the required languages is installed: $($requiredLanguages | Where-Object { $_ -in $installedLanguageTags })"
    # exit 1
}
else {
    Write-Output "Neither English (United States) nor Spanish (Mexico) is installed."
    # exit 3
}
