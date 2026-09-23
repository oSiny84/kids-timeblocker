<#
.SYNOPSIS
    TimeBlocker 서비스를 제거하고 시스템을 원래 상태로 되돌린다. (관리자 권한 필요)

.DESCRIPTION
    순서가 중요하다.
    1. cleanup 실행 : hosts 차단 구간 / 방화벽 규칙 / 어댑터 DNS 설정 원복
    2. 서비스 정지 및 삭제
    3. (선택) 설치 폴더와 데이터 폴더 삭제

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1
    powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1 -RemoveData
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\TimeBlocker",
    [string]$ServiceName = 'TimeBlocker',

    # 설정/로그/허용 상태까지 모두 지운다.
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "관리자 권한이 필요합니다. 관리자 권한 PowerShell 에서 다시 실행하세요."
}

Write-Host "=== TimeBlocker 서비스 제거 ===" -ForegroundColor Cyan

$exePath = Join-Path $InstallDirectory 'TimeBlocker.Service.exe'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

# 1. 서비스 정지 (차단 정리 전에 멈춰야 다시 적용되지 않는다)
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host "[1/4] 서비스 정지" -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force
    $service.WaitForStatus('Stopped', '00:00:30')
}
else {
    Write-Host "[1/4] 서비스가 실행 중이 아닙니다" -ForegroundColor Yellow
}

# 2. 차단 상태 정리 (cleanup 과 동일한 공통 로직 사용)
#    어느 단계가 실패해도 나머지는 계속 수행되고, 마지막에 요약이 출력된다.
Write-Host "[2/4] 시스템 원상복구 (DNS / hosts / 방화벽 / 상태파일)" -ForegroundColor Yellow
$restoreFailed = $false
if (Test-Path $exePath) {
    & $exePath cleanup state
    if ($LASTEXITCODE -ne 0) {
        $restoreFailed = $true
        Write-Host "  일부 복구 단계가 실패했습니다. 위 출력을 확인하세요." -ForegroundColor Red
    }
}
else {
    Write-Host "  실행파일을 찾을 수 없어 수동으로 정리합니다." -ForegroundColor DarkYellow
    netsh advfirewall firewall delete rule name="TimeBlocker_Roblox_Block"  2>&1 | Out-Null
    netsh advfirewall firewall delete rule name="TimeBlocker_YouTube_Block" 2>&1 | Out-Null
    Write-Host "  hosts 파일의 # TIMEBLOCKER BEGIN ~ END 구간은 직접 확인하세요." -ForegroundColor DarkYellow
    Write-Host "  어댑터 DNS 가 127.0.0.1 로 남아 인터넷이 되지 않으면," -ForegroundColor DarkYellow
    Write-Host "  네트워크 설정에서 DNS 를 '자동으로 DNS 서버 주소 받기'로 바꾸세요." -ForegroundColor DarkYellow
}

# 3. 서비스 삭제
Write-Host "[3/4] 서비스 등록 해제" -ForegroundColor Yellow
if ($service) {
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# 4. 파일 삭제
Write-Host "[4/4] 파일 정리" -ForegroundColor Yellow
if (Test-Path $InstallDirectory) {
    Remove-Item -Recurse -Force $InstallDirectory
    Write-Host "  설치 폴더 삭제: $InstallDirectory"
}

$dataDirectory = Join-Path $env:ProgramData 'TimeBlocker'
if ($RemoveData) {
    if (Test-Path $dataDirectory) {
        # 설치 중 ACL 을 걸어두었으므로 먼저 상속을 복구해야 삭제할 수 있다.
        icacls $dataDirectory /reset /T /C | Out-Null
        Remove-Item -Recurse -Force $dataDirectory
        Write-Host "  데이터 폴더 삭제: $dataDirectory"
    }
}
else {
    Write-Host "  설정/로그는 유지됩니다: $dataDirectory" -ForegroundColor DarkYellow
    Write-Host "  모두 지우려면 -RemoveData 옵션을 사용하세요." -ForegroundColor DarkYellow
}

Write-Host ""
if ($restoreFailed) {
    Write-Host "Service removal   : OK" -ForegroundColor Green
    Write-Host ""
    Write-Host "제거는 끝났지만 일부 시스템 복구가 실패했습니다." -ForegroundColor Red
    Write-Host "인터넷이 되지 않으면 네트워크 설정에서 DNS 를 '자동으로 DNS 서버 주소 받기'로 바꾸세요." -ForegroundColor Red
    exit 2
}

Write-Host "Service removal   : OK" -ForegroundColor Green
Write-Host ""
Write-Host "System restored successfully." -ForegroundColor Green
