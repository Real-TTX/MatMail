#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rebuilds the MatMail container and redeploys the dev stack (the project's live-reload / test workflow).
.DESCRIPTION
    Always rebuilds the image from the current source and recreates the container; the database and the data volume stay.
    The version of a local build is "local-<builddate>".
.PARAMETER Fresh
    Also deletes the data and database volumes first (clean start, setup page appears again).
.PARAMETER Tools
    Also starts pgAdmin on http://localhost:9934.
.EXAMPLE
    ./scripts/redeploy.ps1
.EXAMPLE
    ./scripts/redeploy.ps1 -Fresh
#>
param(
    [switch]$Fresh,
    [switch]$Tools
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:MATMAIL_CHANNEL = 'local'
    $env:MATMAIL_BUILD_DATE = (Get-Date).ToUniversalTime().ToString('yyyyMMdd')

    $compose = @('compose', '-f', 'docker-compose.dev.yml')
    if ($Tools) { $compose += @('--profile', 'tools') }

    # Windows PowerShell 5.1 turns the progress lines docker writes to stderr into errors; judge by the exit code instead.
    $ErrorActionPreference = 'Continue'

    if ($Fresh) {
        Write-Host 'Removing the stack and its volumes ...' -ForegroundColor Yellow
        docker @compose down -v
    }

    docker @compose up -d --build --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'docker compose failed.' }

    Write-Host ''
    Write-Host "MatMail local-$($env:MATMAIL_BUILD_DATE) is running on http://localhost:9933" -ForegroundColor Green
    Write-Host 'Mail: SMTP 2525 | Submission 2587 | SMTPS 2465 | IMAP 2143 | IMAPS 2993' -ForegroundColor DarkGray
    if ($Tools) { Write-Host 'pgAdmin: http://localhost:9934 (admin@matmail.example.com / matmail)' -ForegroundColor DarkGray }
}
finally {
    Pop-Location
}
