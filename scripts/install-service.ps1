<#
.SYNOPSIS
    TimeBlocker 를 Windows Service 로 설치한다. (관리자 권한 필요)

.DESCRIPTION
    - publish 폴더의 내용을 %ProgramFiles%\TimeBlocker 로 복사
    - 설치 폴더 권한 정리 (일반 사용자는 읽기만)
    - sc.exe 로 서비스 등록 (자동 시작, 실패 시 자동 재시작)
    - Telegram Bot Token / 관리자 User ID 설정
    - 서비스 시작

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 `
        -BotToken "123456789:AA..." -AdminUserId 123456789
#>
[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$InstallDirectory = "$env:ProgramFiles\TimeBlocker",
    [string]$ServiceName = 'TimeBlocker',

    # Telegram Bot Token. 생략하면 나중에 set-token 으로 설정할 수 있다.
    [string]$BotToken,

    # 명령을 허용할 Telegram 숫자 User ID. 생략하면 나중에 set-admin 으로 설정한다.
    [long]$AdminUserId = 0,

    # 쇼츠만 차단하는 기능(브라우저 정책)을 설치 시점에 켠다.
    # 생략하면 꺼진 상태로 설치되고, 나중에 텔레그램 'policy on' 으로 켤 수 있다.
    [switch]$BlockShorts
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "관리자 권한이 필요합니다. 관리자 권한 PowerShell 에서 다시 실행하세요."
    }
}

Assert-Administrator

# Windows PowerShell 5.1 에서는 param() 기본값에서 $PSScriptRoot 가 비어 있으므로 여기서 채운다.
if (-not $SourceDirectory) { $SourceDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'publish' }
if (-not (Test-Path $SourceDirectory)) { throw "게시 폴더가 없습니다. 먼저 scriptsuild.ps1 을 실행하세요: $SourceDirectory" }
$SourceDirectory = Resolve-Path $SourceDirectory
$exePath = Join-Path $InstallDirectory 'TimeBlocker.Service.exe'

function Wait-ServiceFullyRemoved([string]$Name, [int]$TimeoutSeconds = 20) {
    # sc.exe delete 는 비동기다. "삭제 대기(Pending Delete)" 상태로 들어가고,
    # 이 서비스를 들여다보는 핸들이 전부 닫혀야 실제로 사라진다.
    # 흔한 원인: Services.msc 창, 작업 관리자의 '서비스' 탭이 열려 있는 경우.
    # 삭제 대기 상태에서 같은 이름으로 새 서비스를 만들면 나중에 sc.exe create 가 실패한다.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Seconds 1
    }
    return -not (Get-Service -Name $Name -ErrorAction SilentlyContinue)
}

Write-Host "=== TimeBlocker 서비스 설치 ===" -ForegroundColor Cyan

# 1. 기존 서비스가 있으면 정지 후 제거
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[1/7] 기존 서비스 정지 및 제거" -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', '00:00:30')
    }
    sc.exe delete $ServiceName | Out-Null

    if (-not (Wait-ServiceFullyRemoved $ServiceName 20)) {
        throw "기존 TimeBlocker 서비스가 '삭제 대기(Pending Delete)' 상태로 남아 있어 설치를 진행할 수 없습니다.`n" +
              "  Services.msc 창이나 작업 관리자의 '서비스' 탭이 열려 있으면 닫고 다시 실행하세요.`n" +
              "  그래도 안 되면 PC 를 재부팅한 뒤 install.bat 을 다시 실행하세요."
    }
}
else {
    Write-Host "[1/7] 기존 서비스 없음" -ForegroundColor Yellow
}

# 2. 파일 복사
Write-Host "[2/7] 파일 복사 -> $InstallDirectory" -ForegroundColor Yellow

# 기존 설치 폴더를 통째로 지우고 새로 넣는다.
# 덮어쓰기만 하면 구버전에만 있던 파일이 남아, 나중에 엉뚱한 DLL 이 로드될 수 있다.
#
# 설정/로그/상태는 %ProgramData%\TimeBlocker 에 있으므로 여기서 지워도 보존된다.
if (Test-Path $InstallDirectory) {
    # 이전 설치가 잘못된 권한을 남겼을 수 있으므로 먼저 정상 상태로 되돌린다.
    # (권한이 깨져 있으면 삭제도 복사도 "액세스가 거부되었습니다" 로 실패한다)
    # takeown 의 /D 옵션은 OS 표시 언어에 따라 답변 글자가 달라지므로 쓰지 않는다.
    # icacls /setowner 는 SID 로 지정하고 프롬프트도 없어서 언어에 영향받지 않는다.
    Write-Host "  기존 폴더 권한 초기화" -ForegroundColor DarkGray
    icacls $InstallDirectory /setowner "*S-1-5-32-544" /T /C /Q 2>&1 | Out-Null
    icacls $InstallDirectory /reset /T /C /Q 2>&1 | Out-Null

    # 방금 정지한 서비스가 DLL 핸들을 잠시 더 쥐고 있을 수 있어 몇 번 재시도한다.
    $removed = $false
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $InstallDirectory -Recurse -Force -ErrorAction Stop
            $removed = $true
            break
        }
        catch {
            if ($attempt -lt 5) {
                Write-Host "  기존 파일 삭제 재시도 $attempt/4 (아직 사용 중)" -ForegroundColor DarkYellow
                Start-Sleep -Seconds 2
            }
        }
    }

    if ($removed) {
        Write-Host "  기존 설치 제거 완료" -ForegroundColor DarkGray
    }
    else {
        # 지우지 못해도 설치 자체는 진행한다. 덮어쓰기로도 대부분 정상 동작한다.
        Write-Host "  기존 파일을 지우지 못해 덮어쓰기로 진행합니다." -ForegroundColor DarkYellow
    }
}

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $SourceDirectory '*') -Destination $InstallDirectory -Recurse -Force

# 3. 설치 폴더 권한: SYSTEM/Administrators 만 쓰기, 일반 사용자는 읽기만
Write-Host "[3/7] 설치 폴더 권한 설정" -ForegroundColor Yellow

# 주의: (OI)(CI) 상속 플래그는 "폴더"에만 유효하다.
# /T 로 파일에까지 같은 ACE 를 적용하려 하면 파일에서 실패하고,
# /inheritance:r 로 상속 ACE 는 이미 지워진 뒤라 DACL 이 비어버린다.
# => 그러면 관리자조차 그 파일을 실행할 수 없게 된다.
# 따라서 폴더에만 상속 가능한 ACE 를 걸고, 하위 항목은 상속으로 받게 한다.
icacls $InstallDirectory /inheritance:r /grant:r `
    "*S-1-5-18:(OI)(CI)F" `
    "*S-1-5-32-544:(OI)(CI)F" `
    "*S-1-5-32-545:(OI)(CI)RX" | Out-Null

# 하위 파일/폴더는 자기 DACL 을 버리고 부모에게서 상속받게 한다.
icacls $InstallDirectory /reset /T /C /Q | Out-Null

# 권한을 잘못 걸면 이후 단계가 전부 "액세스 거부" 로 실패하므로 여기서 바로 확인한다.
$aclProbe = cmd /c "icacls `"$exePath`"" 2>&1 | Out-String
if ($aclProbe -match 'Access is denied|액세스가 거부') {
    throw "설치 폴더 권한 설정에 실패했습니다. 실행파일에 접근할 수 없습니다: $exePath"
}

# 4. Telegram 설정 (서비스 시작 전에 먼저 넣는다)
Write-Host "[4/7] Telegram 설정" -ForegroundColor Yellow
if ($BotToken) {
    & $exePath set-token $BotToken
}
else {
    Write-Host "  BotToken 미지정 - 나중에 다음 명령으로 설정하세요:" -ForegroundColor DarkYellow
    Write-Host "    `"$exePath`" set-token <BotToken>" -ForegroundColor DarkYellow
}

if ($AdminUserId -ne 0) {
    & $exePath set-admin $AdminUserId
}
else {
    Write-Host "  AdminUserId 미지정 - 나중에 다음 명령으로 설정하세요:" -ForegroundColor DarkYellow
    Write-Host "    `"$exePath`" set-admin <TelegramUserId>" -ForegroundColor DarkYellow
}

if ($BotToken -and $AdminUserId -ne 0) {
    & $exePath enable-telegram
}

# 쇼츠 차단은 레지스트리 정책을 건드리므로 기본은 꺼짐이다.
# 설치할 때 물어본 결과만 반영한다.
if ($BlockShorts) {
    & $exePath enable-shorts
}
else {
    Write-Host "  쇼츠 차단 미사용 - 나중에 텔레그램에서 켤 수 있습니다: policy on" -ForegroundColor DarkYellow
}

# 5. 서비스 등록
Write-Host "[5/7] 서비스 등록" -ForegroundColor Yellow
sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto obj= "LocalSystem" `
    DisplayName= "TimeBlocker Access Control Service" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "서비스 등록 실패" }

sc.exe description $ServiceName "시간대별로 YouTube / Roblox 접근을 차단합니다. Telegram 으로 원격 관리됩니다." | Out-Null

# 서비스 복구(Recovery) 설정.
#   First failure      : Restart the Service (10초 후)
#   Second failure     : Restart the Service (30초 후)
#   Subsequent failures: Restart the Service (60초 후)
#   실패 카운터는 하루(86400초)마다 초기화한다.
#
# 이 설정이 중요한 이유:
# DNS 프록시 모드에서 서비스가 죽은 채로 남으면 어댑터 DNS 가 127.0.0.1 을 가리킨 상태가 될 수 있다.
# 서비스가 다시 살아나면 시작 시 비정상 종료를 감지해 어댑터 DNS 를 원래대로 되돌린다.
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

# 정상 종료가 아닌 모든 종료(0이 아닌 종료 코드 포함)에 복구 동작을 적용한다.
sc.exe failureflag $ServiceName 1 | Out-Null

# 6. 알림 트레이 앱을 로그인 시 자동 실행하도록 등록
#
# 서비스는 세션 0 에서 돌기 때문에 화면에 창을 띄울 수 없다.
# 이 앱이 사용자 세션에서 경고창과 답장 칸을 보여준다.
#
# HKLM Run 에 넣으면 이 PC 에 로그인하는 모든 계정에서 실행된다.
# 숨기지 않는다. 작업 관리자의 시작프로그램 탭에 그대로 보인다.
Write-Host "[6/7] 알림 트레이 앱 등록" -ForegroundColor Yellow

$notifierPath = Join-Path $InstallDirectory 'TimeBlocker.Notifier.exe'
if (Test-Path $notifierPath) {
    $runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    New-ItemProperty -Path $runKey -Name 'TimeBlockerNotifier' `
        -Value "`"$notifierPath`"" -PropertyType String -Force | Out-Null
    Write-Host "  등록 완료. 다음 로그인부터 자동 실행됩니다." -ForegroundColor DarkGray

    # 지금 로그인해 있는 사용자를 위해 한 번 띄워 준다.
    # 서비스 계정(SYSTEM)에서 실행하면 보이지 않으므로 실패해도 넘어간다.
    try {
        Start-Process -FilePath $notifierPath -ErrorAction Stop
        Write-Host "  지금 세션에서도 실행했습니다." -ForegroundColor DarkGray
    }
    catch {
        Write-Host "  지금은 실행하지 못했습니다. 다시 로그인하면 자동으로 뜹니다." -ForegroundColor DarkYellow
    }
}
else {
    Write-Host "  TimeBlocker.Notifier.exe 가 없어 건너뜁니다." -ForegroundColor DarkYellow
    Write-Host "  (build.ps1 을 다시 실행해 게시하세요)" -ForegroundColor DarkYellow
}

# 7. 시작
Write-Host "[7/7] 서비스 시작" -ForegroundColor Yellow
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')

Write-Host "`n설치 완료." -ForegroundColor Green
Get-Service -Name $ServiceName | Format-Table -AutoSize

Write-Host "상태 확인:  `"$InstallDirectory\TimeBlocker.Admin.exe`" status" -ForegroundColor Green
Write-Host "로그 위치:  $env:ProgramData\TimeBlocker\logs" -ForegroundColor Green
