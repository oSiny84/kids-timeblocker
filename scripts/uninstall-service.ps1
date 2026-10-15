<#
.SYNOPSIS
    TimeBlocker 서비스를 제거하고 시스템을 원래 상태로 되돌린다. (관리자 권한 필요)

.DESCRIPTION
    순서가 중요하다.
    1. cleanup 실행 : hosts 차단 구간 / 방화벽 규칙 / 브라우저 정책 / 어댑터 DNS 설정 원복
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

function Wait-ServiceFullyRemoved([string]$Name, [int]$TimeoutSeconds = 20) {
    # sc.exe delete 는 비동기다. "삭제 대기(Pending Delete)" 상태로 들어가고,
    # 이 서비스를 들여다보는 핸들이 전부 닫혀야 실제로 사라진다.
    # 흔한 원인: Services.msc 창, 작업 관리자의 '서비스' 탭이 열려 있는 경우.
    # 그래서 sc.exe delete 직후 바로 성공이라고 믿으면 안 되고, 실제로 사라졌는지 확인해야 한다.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Seconds 1
    }
    return -not (Get-Service -Name $Name -ErrorAction SilentlyContinue)
}

function Remove-DirectorySafely([string]$Path, [string]$Label) {
    # 방금 종료한 프로세스가 DLL 핸들을 잠시 더 쥐고 있을 수 있으므로 몇 번 재시도한다.
    # 권한 때문에 막히는 경우도 있어 소유권과 ACL 을 먼저 정리한다.
    if (-not (Test-Path $Path)) { return $true }

    icacls $Path /setowner "*S-1-5-32-544" /T /C /Q 2>&1 | Out-Null
    icacls $Path /reset /T /C /Q 2>&1 | Out-Null

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return $true
        }
        catch {
            if ($attempt -eq 5) {
                Write-Host "  $Label 삭제 실패: $($_.Exception.Message)" -ForegroundColor Red
                return $false
            }
            Write-Host "  $Label 삭제 재시도 $attempt/4 (파일이 아직 사용 중)" -ForegroundColor DarkYellow
            Start-Sleep -Seconds 2
        }
    }
    return $false
}

Write-Host "=== TimeBlocker 서비스 제거 ===" -ForegroundColor Cyan

$exePath = Join-Path $InstallDirectory 'TimeBlocker.Service.exe'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

# 1. 서비스 정지 (차단 정리 전에 멈춰야 다시 적용되지 않는다)
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host "[1/5] 서비스 정지" -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force
    $service.WaitForStatus('Stopped', '00:00:30')
}
else {
    Write-Host "[1/5] 서비스가 실행 중이 아닙니다" -ForegroundColor Yellow
}

# 2. 차단 상태 정리 (cleanup 과 동일한 공통 로직 사용)
#    어느 단계가 실패해도 나머지는 계속 수행되고, 마지막에 요약이 출력된다.
Write-Host "[2/5] 시스템 원상복구 (DNS / hosts / 방화벽 / 브라우저 정책 / 상태파일)" -ForegroundColor Yellow
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
Write-Host "[3/5] 서비스 등록 해제" -ForegroundColor Yellow
$serviceRemoved = $true
if ($service) {
    sc.exe delete $ServiceName | Out-Null

    if (Wait-ServiceFullyRemoved $ServiceName 20) {
        Write-Host "  서비스 등록 삭제 완료"
    }
    else {
        $serviceRemoved = $false
        Write-Host "  서비스가 '삭제 대기(Pending Delete)' 상태로 남아 있습니다." -ForegroundColor Red
        Write-Host "  Services.msc 창이나 작업 관리자의 '서비스' 탭이 열려 있으면 닫고 다시 실행하세요." -ForegroundColor Red
        Write-Host "  그래도 남아 있으면 PC 를 재부팅한 뒤 이 스크립트를 다시 실행하세요." -ForegroundColor Red
    }
}

# 4. 알림 트레이 앱 정리
#    자동 실행 등록을 지우고, 지금 떠 있는 것도 닫는다.
#    여기서 안 지우면 로그인할 때마다 없는 프로그램을 실행하려 한다.
Write-Host "[4/5] 알림 트레이 앱 정리" -ForegroundColor Yellow

$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
try {
    if (Get-ItemProperty -Path $runKey -Name 'TimeBlockerNotifier' -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name 'TimeBlockerNotifier' -Force -ErrorAction Stop
        Write-Host "  자동 실행 등록 삭제"
    }
    else {
        Write-Host "  자동 실행 등록 없음"
    }
}
catch {
    Write-Host "  자동 실행 등록을 지우지 못했습니다: $($_.Exception.Message)" -ForegroundColor Red
}

# 실행 중이면 파일을 잠그고 있어 폴더 삭제가 실패한다. 먼저 닫는다.
try {
    $running = Get-Process -Name 'TimeBlocker.Notifier' -ErrorAction SilentlyContinue
    if ($running) {
        $running | Stop-Process -Force -ErrorAction Stop
        Start-Sleep -Seconds 1
        Write-Host "  실행 중이던 트레이 앱 종료 ($($running.Count)개)"
    }
}
catch {
    Write-Host "  트레이 앱을 닫지 못했습니다: $($_.Exception.Message)" -ForegroundColor DarkYellow
}

# 4. 파일 삭제
#    여기서 실패해도 스크립트를 중단하지 않는다. 시스템 복구는 이미 [2/5] 에서 끝났고,
#    남은 파일은 나중에 지워도 되기 때문이다. 결과만 정확히 알려준다.
Write-Host "[5/5] 파일 정리" -ForegroundColor Yellow

$filesRemoved = $true

if (Test-Path $InstallDirectory) {
    if (Remove-DirectorySafely $InstallDirectory '설치 폴더') {
        Write-Host "  설치 폴더 삭제: $InstallDirectory"
    }
    else {
        $filesRemoved = $false
    }
}

$dataDirectory = Join-Path $env:ProgramData 'TimeBlocker'
if ($RemoveData) {
    if (Test-Path $dataDirectory) {
        if (Remove-DirectorySafely $dataDirectory '데이터 폴더') {
            Write-Host "  데이터 폴더 삭제: $dataDirectory"
        }
        else {
            $filesRemoved = $false
        }
    }
}
else {
    Write-Host "  설정/로그는 유지됩니다: $dataDirectory" -ForegroundColor DarkYellow
    Write-Host "  모두 지우려면 -RemoveData 옵션을 사용하세요." -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host ("Service removal   : " + $(if ($serviceRemoved) { 'OK' } else { 'PENDING (reboot needed)' })) `
    -ForegroundColor $(if ($serviceRemoved) { 'Green' } else { 'Red' })
Write-Host ("File cleanup      : " + $(if ($filesRemoved) { 'OK' } else { 'FAILED' })) `
    -ForegroundColor $(if ($filesRemoved) { 'Green' } else { 'Red' })
Write-Host ""

if ($restoreFailed) {
    Write-Host "제거는 끝났지만 일부 시스템 복구가 실패했습니다." -ForegroundColor Red
    Write-Host "인터넷이 되지 않으면 네트워크 설정에서 DNS 를 '자동으로 DNS 서버 주소 받기'로 바꾸세요." -ForegroundColor Red
    exit 2
}

if (-not $serviceRemoved) {
    # DNS/hosts/방화벽 복구는 이미 [2/5] 에서 끝났으므로 인터넷은 안전하다.
    # 남은 건 서비스 등록뿐이고, 재부팅하면 핸들이 강제로 풀려 사라진다.
    Write-Host "시스템은 정상 복구되었습니다. 다만 서비스 등록이 '삭제 대기' 상태로 남아 있습니다." -ForegroundColor Yellow
    Write-Host "PC 를 재부팅하면 사라집니다. 재부팅 전에는 같은 이름으로 재설치할 수 없습니다." -ForegroundColor Yellow
    exit 4
}

if (-not $filesRemoved) {
    # 시스템 상태(DNS/hosts/방화벽)는 이미 복구됐다. 남은 것은 파일뿐이라 위험하지 않다.
    Write-Host "시스템은 정상 복구되었습니다. 다만 일부 파일이 삭제되지 않았습니다." -ForegroundColor Yellow
    Write-Host "잠시 후(또는 재부팅 후) 아래 폴더를 직접 지우면 됩니다:" -ForegroundColor Yellow
    Write-Host "  $InstallDirectory" -ForegroundColor Yellow
    exit 3
}

Write-Host "System restored successfully." -ForegroundColor Green
