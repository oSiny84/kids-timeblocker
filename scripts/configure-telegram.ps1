<#
.SYNOPSIS
    설치 후 Telegram Bot Token / 관리자 User ID 를 설정한다. (관리자 권한 필요)

.DESCRIPTION
    보안상 Bot Token 과 관리자 User ID 는 Telegram 명령으로 바꿀 수 없다.
    반드시 PC 앞에서 관리자 권한으로 설정해야 한다.
    Token 은 Windows DPAPI(LocalMachine) 로 암호화되어 저장되며 로그에 남지 않는다.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\configure-telegram.ps1 `
        -BotToken "123456789:AA..." -AdminUserId 123456789
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BotToken,
    [Parameter(Mandatory = $true)][long]$AdminUserId,
    [string]$InstallDirectory = "$env:ProgramFiles\TimeBlocker",
    [string]$ServiceName = 'TimeBlocker'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "관리자 권한이 필요합니다."
}

$exePath = Join-Path $InstallDirectory 'TimeBlocker.Service.exe'
if (-not (Test-Path $exePath)) { throw "실행파일을 찾을 수 없습니다: $exePath" }

& $exePath set-token $BotToken
& $exePath set-admin $AdminUserId
& $exePath enable-telegram

# 서비스가 새 설정을 즉시 읽도록 재시작한다.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -eq 'Running') {
    Write-Host "서비스를 재시작합니다..." -ForegroundColor Yellow
    Restart-Service -Name $ServiceName -Force
    $service.WaitForStatus('Running', '00:00:30')
}

Write-Host "`n설정 완료. Telegram 에서 'status' 를 보내 확인하세요." -ForegroundColor Green
& $exePath show-config
