param(
    [string]$HostUrl = "http://localhost:8080",
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null,
        [int[]]$ExpectedStatus = @(200)
    )

    $uri = "$HostUrl$Path"
    $parameters = @{
        Uri = $uri
        Method = $Method
        ErrorAction = "Stop"
    }

    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = ($Body | ConvertTo-Json -Depth 20)
    }

    try {
        $response = Invoke-WebRequest @parameters
    }
    catch {
        $statusCode = $null
        $responseBody = ""
        if ($_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
            try {
                $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
                $responseBody = $reader.ReadToEnd()
            }
            catch {
                $responseBody = $_.Exception.Message
            }
        }

        throw "$Method $uri failed with status $statusCode. Body: $responseBody"
    }

    if ($ExpectedStatus -notcontains [int]$response.StatusCode) {
        throw "$Method $uri returned $($response.StatusCode), expected $($ExpectedStatus -join ', '). Body: $($response.Content)"
    }

    if ([string]::IsNullOrWhiteSpace($response.Content)) {
        return $null
    }

    return $response.Content | ConvertFrom-Json
}

function Wait-For {
    param(
        [string]$Name,
        [scriptblock]$Condition
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($value) {
            return $value
        }

        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for $Name"
}

Write-Host "Waiting for API health..."
Wait-For -Name "API health" -Condition {
    try {
        Invoke-Api -Method GET -Path "/health" | Out-Null
        return $true
    }
    catch {
        return $null
    }
} | Out-Null

Write-Host "Checking Swagger..."
Invoke-Api -Method GET -Path "/swagger/v1/swagger.json" | Out-Null

Write-Host "Checking empty list endpoints..."
Invoke-Api -Method GET -Path "/api/dashboard/today" | Out-Null
Invoke-Api -Method GET -Path "/api/notes" | Out-Null
Invoke-Api -Method GET -Path "/api/tasks" | Out-Null
Invoke-Api -Method GET -Path "/api/appointments" | Out-Null
Invoke-Api -Method GET -Path "/api/reminders" | Out-Null

Write-Host "Creating conversation..."
$conversation = Invoke-Api -Method POST -Path "/api/conversations" -ExpectedStatus @(201) -Body @{
    title = "Smoke test hospital visit"
}
$conversationId = $conversation.id
if (-not $conversationId) {
    throw "Create conversation did not return id"
}

Invoke-Api -Method GET -Path "/api/conversations/$conversationId" | Out-Null

Write-Host "Submitting transcript..."
Invoke-Api -Method POST -Path "/api/conversations/$conversationId/transcripts" -ExpectedStatus @(202) -Body @{
    speaker = "HearingUser"
    originalText = "Ngay mai luc 8 gio toi co lich kham o benh vien Cho Ray, hay nhac toi truoc 30 phut."
    language = "vi"
    startedAt = "2026-07-11T10:00:00+07:00"
    endedAt = "2026-07-11T10:00:05+07:00"
    sequenceNumber = 1
} | Out-Null

$transcripts = Invoke-Api -Method GET -Path "/api/conversations/$conversationId/transcripts"
if (@($transcripts).Count -lt 1) {
    throw "Transcript was not persisted"
}

Write-Host "Waiting for analysis..."
$analyses = Wait-For -Name "completed analysis" -Condition {
    $items = @(Invoke-Api -Method GET -Path "/api/conversations/$conversationId/analyses")
    $completed = @($items | Where-Object { $_.status -eq "Completed" })
    if ($completed.Count -gt 0) { return $items }
    return $null
}

$analysisId = @($analyses)[0].id
Invoke-Api -Method GET -Path "/api/analyses/$analysisId" | Out-Null

Write-Host "Waiting for proposed actions..."
$actions = Wait-For -Name "proposed appointment and reminder actions" -Condition {
    $items = @(Invoke-Api -Method GET -Path "/api/conversations/$conversationId/proposed-actions")
    $appointment = @($items | Where-Object { $_.actionType -eq "CreateAppointment" })
    $reminder = @($items | Where-Object { $_.actionType -eq "ScheduleReminder" })
    if ($appointment.Count -gt 0 -and $reminder.Count -gt 0) { return $items }
    return $null
}

foreach ($action in @($actions)) {
    Invoke-Api -Method GET -Path "/api/proposed-actions/$($action.id)" | Out-Null
}

Write-Host "Confirming proposed actions..."
foreach ($action in @($actions | Where-Object { $_.actionType -in @("CreateAppointment", "ScheduleReminder") })) {
    Invoke-Api -Method POST -Path "/api/proposed-actions/$($action.id)/confirm" -Body @{
        title = $action.title
        scheduledAt = $action.scheduledAt
        location = "Cho Ray Hospital"
        recipientEmail = "demo@example.com"
    } | Out-Null
}

Write-Host "Waiting for appointment and reminder persistence..."
$appointments = Wait-For -Name "appointment persistence" -Condition {
    $items = @(Invoke-Api -Method GET -Path "/api/appointments")
    if ($items.Count -gt 0) { return $items }
    return $null
}

$reminders = Wait-For -Name "reminder persistence" -Condition {
    $items = @(Invoke-Api -Method GET -Path "/api/reminders")
    if ($items.Count -gt 0) { return $items }
    return $null
}

Invoke-Api -Method GET -Path "/api/appointments/$(@($appointments)[0].id)" | Out-Null
Invoke-Api -Method GET -Path "/api/reminders/$(@($reminders)[0].id)" | Out-Null

Write-Host "Checking dashboard and timeline..."
Invoke-Api -Method GET -Path "/api/dashboard/today" | Out-Null
Invoke-Api -Method GET -Path "/api/conversations/$conversationId/timeline" | Out-Null
Invoke-Api -Method POST -Path "/api/conversations/$conversationId/complete" | Out-Null

Write-Host "Cancelling first reminder..."
Invoke-Api -Method POST -Path "/api/reminders/$(@($reminders)[0].id)/cancel" | Out-Null

Write-Host "Smoke test passed."
