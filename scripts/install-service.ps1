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
    [long]$AdminUserId = 0
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

Write-Host "=== TimeBlocker 서비스 설치 ===" -ForegroundColor Cyan

# 1. 기존 서비스가 있으면 정지 후 제거
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[1/6] 기존 서비스 정지 및 제거" -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', '00:00:30')
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
else {
    Write-Host "[1/6] 기존 서비스 없음" -ForegroundColor Yellow
}

# 2. 파일 복사
Write-Host "[2/6] 파일 복사 -> $InstallDirectory" -ForegroundColor Yellow

# 이전 설치가 잘못된 권한을 남겼을 수 있으므로 먼저 정상 상태로 되돌린다.
# (권한이 깨져 있으면 복사나 실행이 "액세스가 거부되었습니다" 로 실패한다)
if (Test-Path $InstallDirectory) {
    Write-Host "  기존 폴더 권한 초기화" -ForegroundColor DarkGray

    # takeown 의 /D 옵션은 OS 표시 언어에 따라 답변 글자가 달라지므로 쓰지 않는다.
    # icacls /setowner 는 SID 로 지정하고 프롬프트도 없어서 언어에 영향받지 않는다.
    icacls $InstallDirectory /setowner "*S-1-5-32-544" /T /C /Q 2>&1 | Out-Null
    icacls $InstallDirectory /reset /T /C /Q 2>&1 | Out-Null
}

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $SourceDirectory '*') -Destination $InstallDirectory -Recurse -Force

# 3. 설치 폴더 권한: SYSTEM/Administrators 만 쓰기, 일반 사용자는 읽기만
Write-Host "[3/6] 설치 폴더 권한 설정" -ForegroundColor Yellow

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
Write-Host "[4/6] Telegram 설정" -ForegroundColor Yellow
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

# 5. 서비스 등록
Write-Host "[5/6] 서비스 등록" -ForegroundColor Yellow
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

# 6. 시작
Write-Host "[6/6] 서비스 시작" -ForegroundColor Yellow
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')

Write-Host "`n설치 완료." -ForegroundColor Green
Get-Service -Name $ServiceName | Format-Table -AutoSize

Write-Host "상태 확인:  `"$InstallDirectory\TimeBlocker.Admin.exe`" status" -ForegroundColor Green
Write-Host "로그 위치:  $env:ProgramData\TimeBlocker\logs" -ForegroundColor Green
