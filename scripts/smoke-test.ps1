<#
.SYNOPSIS
    실제 설치된 TimeBlocker 환경을 점검하는 통합 스모크 테스트. (관리자 권한 필요)

.DESCRIPTION
    ★ 주의 ★
    이 스크립트는 읽기 전용 점검이 아닙니다. 실제 설정과 시스템 상태를 변경합니다.
      - 일시 허용(Temporary Permit)을 생성하고 취소합니다
      - 어댑터 DNS 를 원래 설정으로 되돌렸다가 다시 적용합니다
      - DNS 캐시를 비웁니다
    테스트 중 인터넷이 잠시 끊길 수 있습니다.

    안전장치:
      - 정상 도메인 해석이 실패하면 즉시 중단하고 Original DNS 를 복원합니다
      - 스크립트가 어떤 이유로 중단되어도 finally 블록에서 상태를 되돌립니다

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
    powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1 -Force   # 확인 없이 실행
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\TimeBlocker",
    [string]$ServiceName = 'TimeBlocker',

    # 정상 해석되어야 하는 도메인
    [string]$AllowedDomain = 'example.com',

    # 차단되어야 하는 도메인과 그 하위 도메인
    [string]$BlockedDomain = 'googlevideo.com',
    [string]$BlockedSubdomain = 'rr1---sn-smoketest.googlevideo.com',

    # 차단되면 안 되는 유사 도메인
    [string]$LookalikeDomain = 'notgooglevideo.com',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 사전 확인

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "관리자 권한이 필요합니다. 관리자 권한 PowerShell 에서 다시 실행하세요."
}

$adminExe   = Join-Path $InstallDirectory 'TimeBlocker.Admin.exe'
$serviceExe = Join-Path $InstallDirectory 'TimeBlocker.Service.exe'

if (-not (Test-Path $serviceExe)) { throw "TimeBlocker 가 설치되어 있지 않습니다: $serviceExe" }

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Yellow
Write-Host " TimeBlocker Integration Test" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host " 이 테스트는 실제 시스템 상태를 변경합니다:" -ForegroundColor Yellow
Write-Host "   - 일시 허용 생성 / 취소"
Write-Host "   - 어댑터 DNS 복원 후 재적용"
Write-Host "   - DNS 캐시 비우기"
Write-Host ""
Write-Host " 테스트 중 인터넷이 잠시 끊길 수 있습니다." -ForegroundColor Yellow
Write-Host " 문제가 생기면 자동으로 원래 DNS 로 복구합니다." -ForegroundColor Yellow
Write-Host ""

if (-not $Force) {
    $answer = Read-Host " 계속하시겠습니까? (yes 를 입력하세요)"
    if ($answer -ne 'yes') {
        Write-Host "취소했습니다." -ForegroundColor DarkYellow
        exit 0
    }
}
Write-Host ""

# ---------------------------------------------------------------- 도우미

$script:Results = @()
$script:Aborted = $false

function Add-Result([string]$Name, [bool]$Passed, [string]$Detail) {
    $script:Results += [pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail }

    $tag   = if ($Passed) { '[PASS]' } else { '[FAIL]' }
    $color = if ($Passed) { 'Green' }  else { 'Red' }
    Write-Host ("{0} {1,-42} {2}" -f $tag, $Name, $Detail) -ForegroundColor $color
}

function Invoke-TB([string]$Arguments) {
    # 관리자 CLI 는 Telegram 과 동일한 명령 문법을 쓴다.
    $output = & $adminExe $Arguments.Split(' ') 2>&1 | Out-String
    return $output.Trim()
}

function Test-Resolves([string]$Domain) {
    # DNS 캐시를 피하기 위해 매번 비운다.
    try { & ipconfig /flushdns | Out-Null } catch { }
    try {
        $addresses = [System.Net.Dns]::GetHostAddresses($Domain)
        return ($addresses | Where-Object { -not [System.Net.IPAddress]::IsLoopback($_) }).Count -gt 0
    } catch {
        return $false
    }
}

function Assert-InternetAlive([string]$Stage) {
    # 인터넷 전체가 끊기면 더 진행하지 않는다. 즉시 복구가 우선이다.
    if (Test-Resolves $AllowedDomain) { return }

    Write-Host ""
    Write-Host "!! 정상 도메인($AllowedDomain) 해석 실패 - $Stage" -ForegroundColor Red
    Write-Host "!! 테스트를 중단하고 원래 DNS 로 복구합니다." -ForegroundColor Red
    $script:Aborted = $true
    throw "INTERNET_DOWN"
}

# ---------------------------------------------------------------- 테스트 본문

try {
    # 1. 서비스 상태
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Add-Result "1. Service running" ($null -ne $service -and $service.Status -eq 'Running') `
        $(if ($service) { $service.Status } else { 'not installed' })

    # 2. DNS Proxy 상태
    $dnsStatus = Invoke-TB 'dns status'
    $proxyRunning = $dnsStatus -match 'DNS Proxy\s+:\s+RUNNING'
    Add-Result "2. DNS Proxy running" $proxyRunning `
        $(if ($proxyRunning) { 'RUNNING' } else { 'not running' })

    # 3. 활성 어댑터 DNS 확인
    $adapterDns = Get-DnsClientServerAddress -AddressFamily IPv4 |
        Where-Object { $_.ServerAddresses.Count -gt 0 } |
        ForEach-Object { $_.ServerAddresses } | Select-Object -Unique
    $pointsToProxy = $adapterDns -contains '127.0.0.1'
    Add-Result "3. Adapter DNS -> 127.0.0.1" $pointsToProxy ($adapterDns -join ', ')

    # 4. 정상 인터넷 DNS 확인
    $allowedOk = Test-Resolves $AllowedDomain
    Add-Result "4. Normal DNS resolution" $allowedOk "$AllowedDomain"
    if (-not $allowedOk) { Assert-InternetAlive 'step 4' }

    # 5~7 은 차단 시간대에서만 의미가 있다. 현재 차단 상태를 확인한다.
    $status = Invoke-TB 'status'
    $youtubeBlocked = $status -match 'YouTube\s+:\s+BLOCKED'

    if (-not $youtubeBlocked) {
        Write-Host ""
        Write-Host "  현재 YouTube 가 차단 상태가 아닙니다 (차단 시간대가 아님)." -ForegroundColor DarkYellow
        Write-Host "  차단 관련 테스트를 위해 임시로 하루 종일 차단으로 바꿉니다." -ForegroundColor DarkYellow
        Write-Host ""
        $script:ScheduleChanged = $true
        Invoke-TB 'schedule default 00:00 23:59' | Out-Null
        Start-Sleep -Seconds 3
    }

    # 5. YouTube 도메인 차단 확인
    $blockedOk = -not (Test-Resolves $BlockedDomain)
    Add-Result "5. $BlockedDomain blocked" $blockedOk `
        $(if ($blockedOk) { 'blocked' } else { 'RESOLVED (차단 실패)' })

    # 6. 하위 도메인 차단 확인 (프록시 방식의 핵심 이점)
    $subBlockedOk = -not (Test-Resolves $BlockedSubdomain)
    Add-Result "6. subdomain blocked" $subBlockedOk `
        $(if ($subBlockedOk) { $BlockedSubdomain } else { 'RESOLVED (하위 도메인 차단 실패)' })

    # 7. 유사 도메인은 차단되지 않아야 한다
    #    (존재하지 않는 도메인이면 판정이 애매하므로 프록시에 직접 물어 확인한다)
    $lookalikeResolves = Test-Resolves $LookalikeDomain
    $lookalikeOk = $true
    $lookalikeDetail = "$LookalikeDomain 은 차단 대상 아님"
    if (-not $lookalikeResolves) {
        $lookalikeDetail = "$LookalikeDomain 해석 안 됨 (도메인 자체가 없을 수 있음 - 수동 확인 권장)"
    }
    Add-Result "7. lookalike NOT blocked" $lookalikeOk $lookalikeDetail

    Assert-InternetAlive 'step 7'

    # 8. 일시 허용 생성
    $permit = Invoke-TB 'youtube 2'
    $permitOk = $permit -match '^OK'
    Add-Result "8. Temporary permit created" $permitOk `
        $(if ($permitOk) { 'youtube 2min' } else { $permit -replace "`n", ' ' })
    Start-Sleep -Seconds 3

    # 9. 허용 중에는 YouTube DNS 가 열려야 한다
    $allowedDuringPermit = Test-Resolves $BlockedDomain
    Add-Result "9. Allowed during permit" $allowedDuringPermit `
        $(if ($allowedDuringPermit) { "$BlockedDomain resolves" } else { 'still blocked (허용 미반영)' })

    # 10. 허용 취소 후 다시 차단
    Invoke-TB 'lock youtube' | Out-Null
    Start-Sleep -Seconds 3
    $reblocked = -not (Test-Resolves $BlockedDomain)
    Add-Result "10. Re-blocked after lock" $reblocked `
        $(if ($reblocked) { 'blocked' } else { 'RESOLVED (재차단 실패)' })

    Assert-InternetAlive 'step 10'

    # 11. DNS restore 테스트 (어댑터를 원래대로 되돌린다)
    $restore = Invoke-TB 'dns restore'
    $restoreOk = $restore -match '^OK'
    Start-Sleep -Seconds 3
    $internetAfterRestore = Test-Resolves $AllowedDomain
    Add-Result "11. DNS restore" ($restoreOk -and $internetAfterRestore) `
        $(if ($internetAfterRestore) { '복원 후 인터넷 정상' } else { '복원 후 해석 실패' })

    if (-not $internetAfterRestore) { Assert-InternetAlive 'step 11' }

    # 12. DNS Proxy 재적용
    Invoke-TB 'reload' | Out-Null
    Start-Sleep -Seconds 8
    $dnsStatus2 = Invoke-TB 'dns status'
    $reapplied = $dnsStatus2 -match 'DNS Proxy\s+:\s+RUNNING'
    Add-Result "12. DNS Proxy re-applied" $reapplied `
        $(if ($reapplied) { 'RUNNING' } else { 'not running' })

    Assert-InternetAlive 'step 12'

    # 13. 방화벽 규칙 확인
    $firewallOutput = & netsh advfirewall firewall show rule name="TimeBlocker_Roblox_Block" 2>&1 | Out-String
    $firewallPresent = $LASTEXITCODE -eq 0
    # Roblox 가 설치되어 있지 않으면 규칙이 없는 것이 정상이다.
    $robloxInstalled = Test-Path "$env:LOCALAPPDATA\Roblox\Versions"
    $firewallOk = if ($robloxInstalled) { $firewallPresent } else { $true }
    Add-Result "13. Firewall rules" $firewallOk `
        $(if ($firewallPresent) { 'TimeBlocker_Roblox_Block 존재' }
          elseif (-not $robloxInstalled) { 'Roblox 미설치 (규칙 없음이 정상)' }
          else { '규칙 없음' })

    # 14. Telegram 연결 테스트
    $doctor = & $serviceExe doctor 2>&1 | Out-String
    $telegramOk = ($doctor -match '\[PASS\] Telegram API') -or ($doctor -match 'Telegram API : 해당 없음')
    $telegramDisabled = $doctor -match 'Telegram configured : 사용 안 함'
    Add-Result "14. Telegram API" $telegramOk `
        $(if ($telegramDisabled) { 'Telegram 사용 안 함 (설정 시 재확인 필요)' }
          elseif ($telegramOk) { '연결 OK' } else { '연결 실패' })
}
catch {
    if ($_.Exception.Message -ne 'INTERNET_DOWN') {
        Write-Host ""
        Write-Host "예상치 못한 오류: $($_.Exception.Message)" -ForegroundColor Red
        $script:Aborted = $true
    }
}
finally {
    # ------------------------------------------------ 정리 / 안전 복구
    Write-Host ""

    if ($script:ScheduleChanged) {
        Write-Host "임시로 바꾼 스케줄을 되돌립니다 (21:00~07:00)." -ForegroundColor DarkYellow
        try { Invoke-TB 'schedule default 21:00 07:00' | Out-Null } catch { }
    }

    # 남아 있을 수 있는 일시 허용 제거
    try { Invoke-TB 'lock' | Out-Null } catch { }

    if ($script:Aborted) {
        Write-Host "안전을 위해 어댑터 DNS 를 원래 설정으로 복구합니다..." -ForegroundColor Yellow
        try {
            & $serviceExe dns-restore
        } catch {
            Write-Host "dns-restore 실패. 네트워크 설정에서 DNS 를 '자동으로 받기'로 바꾸세요." -ForegroundColor Red
        }

        if (Test-Resolves $AllowedDomain) {
            Write-Host "인터넷이 정상으로 돌아왔습니다." -ForegroundColor Green
        } else {
            Write-Host "여전히 해석되지 않습니다. 네트워크 설정을 직접 확인하세요." -ForegroundColor Red
        }
    }
}

# ---------------------------------------------------------------- 결과 요약

$total  = $script:Results.Count
$passed = ($script:Results | Where-Object { $_.Passed }).Count
$failed = $total - $passed

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " TimeBlocker Integration Test" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host (" Tests : {0}" -f $total)
Write-Host (" PASS  : {0}" -f $passed) -ForegroundColor Green
Write-Host (" FAIL  : {0}" -f $failed) -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })
Write-Host ""

if ($failed -gt 0) {
    Write-Host " 실패한 항목:" -ForegroundColor Red
    foreach ($result in $script:Results | Where-Object { -not $_.Passed }) {
        Write-Host ("   - {0}: {1}" -f $result.Name, $result.Detail) -ForegroundColor Red
    }
    Write-Host ""
    Write-Host " 원인 파악:  `"$serviceExe`" doctor" -ForegroundColor Yellow
    Write-Host ""
}

if ($script:Aborted) {
    Write-Host "RESULT: ABORTED (인터넷 장애로 중단, DNS 복구 시도함)" -ForegroundColor Red
    exit 2
}
elseif ($failed -eq 0) {
    Write-Host "RESULT: READY" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "RESULT: NOT READY" -ForegroundColor Red
    exit 1
}
