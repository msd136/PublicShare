# =============================================================================
# Set-StudentEmails.ps1
# 
# Purpose:  Finds students in PowerSchool who have no email address, generates
#           one in the format s{DCID}@districtdomain.org, and writes it back to
#           PowerSchool via the API plugin.
#
# Schedule: Run daily via Windows Task Scheduler (recommended: early morning,
#           before downstream sync jobs such as SIS → IdM or LMS syncs)
#
# Requirements:
#   - PowerSchool "Powershell Email Data Access Plugin" must be installed
#   - Network access to your PowerSchool SIS instance
#   - PowerShell 5.1 or later
#
# Setup:    Fill in your Client ID and Client Secret below before first run.
#           Get these from PowerSchool:
#           System > System Settings > Plugin Management Configuration >
#           Powershell Email Data Access Plugin > Data Provider Configuration
# =============================================================================

# -----------------------------------------------------------------------------
# CONFIGURATION — fill these in before running
# -----------------------------------------------------------------------------
$PS_URL        = "https://yourdistrict.powerschool.com"
$CLIENT_ID     = "YOUR-CLIENT-ID-GUID-HERE"
$CLIENT_SECRET = "YOUR-CLIENT-SECRET-HERE"
$EMAIL_DOMAIN  = "districtdomain.org"
$EMAIL_PREFIX  = "s"                          # Produces s{DCID}@districtdomain.org
$LOG_FILE      = "C:\Scripts\Logs\StudentEmailProvisioning.log"
$DRY_RUN       = $false  # Set to $true to test without writing changes
# -----------------------------------------------------------------------------

# -----------------------------------------------------------------------------
# LOGGING
# -----------------------------------------------------------------------------
function Write-Log {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )

    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $entry = "[$timestamp] [$Level] $Message"
    Write-Host $entry
    Add-Content -Path $LOG_FILE -Value $entry
}

# Ensure log directory exists
$logDir = Split-Path $LOG_FILE -Parent
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

Write-Log "======================================================"
Write-Log "Starting Student Email Provisioning Job"
if ($DRY_RUN) {
    Write-Log "*** DRY RUN MODE - no changes will be written ***" "WARN"
}

# -----------------------------------------------------------------------------
# STEP 1: Authenticate with PowerSchool API
# -----------------------------------------------------------------------------
Write-Log "Authenticating with PowerSchool API..."

try {
    # Base64 encode the Client ID and Secret for Basic Auth
    $credentials = [Convert]::ToBase64String(
        [Text.Encoding]::ASCII.GetBytes("${CLIENT_ID}:${CLIENT_SECRET}")
    )

    $authResponse = Invoke-RestMethod `
        -Uri "$PS_URL/oauth/access_token" `
        -Method POST `
        -Headers @{ Authorization = "Basic $credentials" } `
        -Body "grant_type=client_credentials" `
        -ContentType "application/x-www-form-urlencoded"

    $accessToken = $authResponse.access_token
    Write-Log "Authentication successful."
}
catch {
    Write-Log "Authentication FAILED: $_" "ERROR"
    Write-Log "Verify Client ID / Secret and that the PowerSchool plugin is enabled." "ERROR"
    exit 1
}

$authHeader = @{ Authorization = "Bearer $accessToken" }

# -----------------------------------------------------------------------------
# STEP 2: Query PowerSchool for active students missing an email
# -----------------------------------------------------------------------------
Write-Log "Querying PowerSchool for active students without an email address..."

$pageSize    = 100
$pageNumber  = 0
$allStudents = @()

try {
    do {
        $pageNumber++

        $uri = "$PS_URL/ws/v1/district/student" +
               "?expansions=contact_info" +
               "&pagesize=$pageSize" +
               "&page=$pageNumber"

        $response = Invoke-RestMethod `
            -Uri $uri `
            -Method GET `
            -Headers $authHeader

        $batch = $response.students.student

        if ($batch) {
            $allStudents += $batch
            Write-Log "  Retrieved page $pageNumber ($($batch.Count) students)..."
        }

    } while ($batch -and $batch.Count -eq $pageSize)
}
catch {
    Write-Log "Failed to retrieve students from PowerSchool: $_" "ERROR"
    exit 1
}

Write-Log "Total students retrieved: $($allStudents.Count)"

# Filter students missing an email address
$studentsWithoutEmail = $allStudents | Where-Object {
    -not $_.contact_info.email -or $_.contact_info.email.Trim() -eq ""
}

Write-Log "Students missing email: $($studentsWithoutEmail.Count)"

if ($studentsWithoutEmail.Count -eq 0) {
    Write-Log "No students require email provisioning. Exiting."
    Write-Log "======================================================"
    exit 0
}

# -----------------------------------------------------------------------------
# STEP 3: Generate and write email addresses
# -----------------------------------------------------------------------------
$successCount = 0
$failCount    = 0

foreach ($student in $studentsWithoutEmail) {

    $dcid           = $student.id
    $studentNumber  = $student.local_id
    $firstName      = $student.name.first_name
    $lastName       = $student.name.last_name
    $generatedEmail = "${EMAIL_PREFIX}${dcid}@${EMAIL_DOMAIN}"

    Write-Log "Processing: $firstName $lastName | Student#: $studentNumber | DCID: $dcid | Email: $generatedEmail"

    if ($DRY_RUN) {
        Write-Log "  [DRY RUN] Would write $generatedEmail" "WARN"
        $successCount++
        continue
    }

    $body = @{
        students = @{
            student = @(
                @{
                    client_uid   = $studentNumber
                    action       = "UPDATE"
                    id           = $dcid
                    contact_info = @{
                        email = $generatedEmail
                    }
                }
            )
        }
    } | ConvertTo-Json -Depth 5

    try {
        $updateResponse = Invoke-RestMethod `
            -Uri "$PS_URL/ws/v1/student" `
            -Method POST `
            -Headers $authHeader `
            -Body $body `
            -ContentType "application/json"

        $rawResponse = $updateResponse | ConvertTo-Json -Depth 10
        Write-Log "  Raw response: $rawResponse"

        $resultStudent = $null
        if ($updateResponse.students.student) {
            if ($updateResponse.students.student -is [array]) {
                $resultStudent = $updateResponse.students.student[0]
            }
            else {
                $resultStudent = $updateResponse.students.student
            }
        }

        if ($resultStudent -and $resultStudent.action -eq "UPDATE") {
            Write-Log "  SUCCESS"
            $successCount++
        }
        else {
            Write-Log "  WARNING: Unexpected response structure" "WARN"
            $failCount++
        }
    }
    catch {
        Write-Log "  FAILED to update DCID $dcid : $_" "ERROR"
        $failCount++
    }

    Start-Sleep -Milliseconds 200
}

# -----------------------------------------------------------------------------
# STEP 4: Summary
# -----------------------------------------------------------------------------
Write-Log "------------------------------------------------------"
Write-Log "Run complete. Succeeded: $successCount | Failed: $failCount"
Write-Log "======================================================"
``
