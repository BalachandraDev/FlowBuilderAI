# Makes two ordinary users and a client for them, so you can try the app. Projects are made by the users themselves, so nothing else is created.
# Run it once, after the API is started:   .\scripts\demo-setup.ps1
# Development only: the passwords below are public in this repository.

param(
    [string]$Api = "http://localhost:5199",
    [string]$AdminEmail = "admin@flowcapture.local",
    [string]$AdminPassword = "ChangeMe-Dev-12345",
    [string]$UserPassword = "Demo-Passw0rd-123"
)

$ErrorActionPreference = "Stop"
$base = "$Api/api/v1"

function Call($method, $path, $body, $token) {
    $headers = @{}
    if ($token) { $headers.Authorization = "Bearer $token" }
    $args = @{ Method = $method; Uri = "$base/$path"; Headers = $headers; ContentType = "application/json" }
    if ($null -ne $body) { $args.Body = ($body | ConvertTo-Json -Depth 6) }
    Invoke-RestMethod @args
}

$admin = (Call Post "auth/login" @{ email = $AdminEmail; password = $AdminPassword } $null).accessToken

foreach ($u in @(
        @{ email = "asha@demo.local"; name = "Asha Kumar" },
        @{ email = "ravi@demo.local"; name = "Ravi Shankar" })) {
    try {
        Call Post "users" @{ email = $u.email; displayName = $u.name; password = $UserPassword; roles = @("ImplementationLead") } $admin | Out-Null
        Write-Host "created user $($u.email)"
    }
    catch {
        Write-Host "user $($u.email) already exists (or could not be created): $($_.Exception.Message)"
    }
}

# A client both users work for, so they can start projects straight away (an administrator makes clients and assigns people in the app)
$clients = Call Get "clients" $null $admin
$demo = $clients | Where-Object { $_.name -eq "Demo Hospital Group" } | Select-Object -First 1
if (-not $demo) { $demo = Call Post "clients" @{ name = "Demo Hospital Group"; location = "Chennai" } $admin; Write-Host "created client Demo Hospital Group" }
$ids = @((Call Get "users" $null $admin) | Where-Object { $_.email -in @("asha@demo.local", "ravi@demo.local") } | ForEach-Object { $_.id })
Call Put "clients/$($demo.id)/users" @{ userIds = $ids } $admin | Out-Null
Write-Host "assigned asha and ravi to Demo Hospital Group"

Write-Host ""
Write-Host "Done. Sign in to the app:"
Write-Host "  $AdminEmail / $AdminPassword   the administrator: makes users, sets up the AI"
Write-Host "  asha@demo.local  and  ravi@demo.local   (password for both: $UserPassword)   ordinary users: make projects, see all documents"
