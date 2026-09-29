<#
.SYNOPSIS
    배포용 릴리즈 패키지(zip)를 만든다.

.DESCRIPTION
    빌드 -> 테스트 -> publish -> 패키지 구성 -> 압축 순서로 진행한다.
    만들어진 zip 을 자녀 PC 로 옮겨 압축만 풀면 바로 설치할 수 있다.
    (.NET SDK 없이 .NET 8 Desktop Runtime 만 있으면 동작한다)

    패키지에는 소스와 빌드 도구를 넣지 않는다.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make-release.ps1
    powershell -ExecutionPolicy Bypass -File scripts\make-release.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$Version,

    # 이미 빌드/게시가 끝나 있으면 다시 하지 않는다.
    [switch]$SkipBuild,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

# Windows PowerShell 5.1 은 param() 기본값에서 $PSScriptRoot 가 비어 있다.
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'release' }

# 버전은 Directory.Build.props 를 기준으로 삼는다. (한 곳에서만 관리)
if (-not $Version) {
    $propsPath = Join-Path $root 'Directory.Build.props'
    $propsText = Get-Content $propsPath -Raw
    if ($propsText -match '<Version>([^<]+)</Version>') { $Version = $Matches[1].Trim() }
    else { $Version = '1.0.0' }
}

$packageName = "TimeBlocker-$Version"
$stagingDirectory = Join-Path $OutputDirectory $packageName
$zipPath = Join-Path $OutputDirectory "$packageName.zip"

Write-Host "=== TimeBlocker 릴리즈 패키지 생성 ($Version) ===" -ForegroundColor Cyan
Write-Host ""

# ------------------------------------------------------------ 1. 빌드 / 게시

$publishDirectory = Join-Path $root 'publish'

if ($SkipBuild) {
    Write-Host "[1/4] 빌드 건너뜀 (-SkipBuild)" -ForegroundColor Yellow
    if (-not (Test-Path (Join-Path $publishDirectory 'TimeBlocker.Service.exe'))) {
        throw "게시 결과가 없습니다. -SkipBuild 없이 다시 실행하세요."
    }
}
else {
    Write-Host "[1/4] 빌드 + 테스트 + 게시" -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }
}

# ------------------------------------------------------------ 2. 패키지 구성

Write-Host ""
Write-Host "[2/4] 패키지 구성" -ForegroundColor Yellow

if (Test-Path $stagingDirectory) { Remove-Item -LiteralPath $stagingDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null

# 실행파일
Copy-Item -Path $publishDirectory -Destination (Join-Path $stagingDirectory 'publish') -Recurse -Force

# 설치/운영 스크립트 (빌드 스크립트는 소스가 없으므로 제외한다)
$scriptsTarget = Join-Path $stagingDirectory 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsTarget | Out-Null
foreach ($name in @('install-service.ps1', 'uninstall-service.ps1', 'configure-telegram.ps1', 'smoke-test.ps1')) {
    Copy-Item (Join-Path $PSScriptRoot $name) $scriptsTarget -Force
}

# .bat 실행 파일 (build.bat 은 소스가 없으므로 제외)
foreach ($name in @('install.bat', 'doctor.bat', 'dns-restore.bat', 'smoke-test.bat',
                    'uninstall.bat', 'configure-telegram.bat')) {
    Copy-Item (Join-Path $root $name) $stagingDirectory -Force
}

# 문서
Copy-Item (Join-Path $root 'README.md') $stagingDirectory -Force
$sampleConfig = Join-Path $root 'config\timeblocker.config.sample.json'
if (Test-Path $sampleConfig) {
    New-Item -ItemType Directory -Force -Path (Join-Path $stagingDirectory 'config') | Out-Null
    Copy-Item $sampleConfig (Join-Path $stagingDirectory 'config') -Force
}

# 배포본에는 소스가 없으므로, install.bat 의 "지금 빌드할까요?" 분기를 안내 문구로 바꾼다.
$installBat = Join-Path $stagingDirectory 'install.bat'
$installText = [System.IO.File]::ReadAllText($installBat, [System.Text.Encoding]::UTF8)
$oldBlock = @'
    echo  publish 폴더가 없습니다. 먼저 빌드가 필요합니다.
    echo.
    set /p DOBUILD=" 지금 빌드할까요? (Y/N): "
    if /i "!DOBUILD!"=="Y" (
        echo.
        powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
        if !errorlevel! neq 0 (
            echo.
            echo  빌드 실패. 설치를 중단합니다.
            pause
            exit /b 1
        )
    ) else (
        echo  build.bat 을 먼저 실행하세요.
        pause
        exit /b 1
    )
'@
$newBlock = @'
    echo  publish 폴더가 없습니다. 압축이 제대로 풀리지 않았습니다.
    echo  zip 을 다시 풀고 install.bat 을 실행하세요.
    pause
    exit /b 1
'@
if ($installText.Contains($oldBlock)) {
    $installText = $installText.Replace($oldBlock, $newBlock)
    [System.IO.File]::WriteAllText($installBat, $installText, (New-Object System.Text.UTF8Encoding($false)))
}
else {
    Write-Host "  경고: install.bat 의 빌드 분기를 찾지 못했습니다. 원본 그대로 포함합니다." -ForegroundColor DarkYellow
}

# 짧은 설치 안내 (README 는 길어서 별도로 요약본을 넣는다)
$quickStart = @"
TimeBlocker $Version 설치 안내
==========================================================

[준비물]
 1. .NET 8 Desktop Runtime
    https://dotnet.microsoft.com/download/dotnet/8.0
 2. Telegram Bot Token
    텔레그램에서 @BotFather -> /newbot -> 토큰 받기
 3. 본인의 Telegram 숫자 User ID
    텔레그램에서 @userinfobot -> /start -> "Id:" 뒤의 숫자
 4. 만든 봇에게 /start 를 한 번 보내둘 것 (안 하면 봇이 메시지를 못 받음)

[설치]
 install.bat 을 실행합니다. (권한 상승 창이 뜨면 "예")
 Bot Token 과 User ID 를 물어보면 입력하세요.
 설치가 끝나면 자동으로 점검(doctor)이 실행됩니다.

[설치 후 확인]
 1. 브라우저로 아무 사이트나 열어 인터넷이 되는지 확인
    -> 안 되면 dns-restore.bat 실행
 2. 텔레그램에서 봇에게 "status" 전송 -> 응답 확인
 3. 텔레그램에서 "msg 테스트" 전송 -> PC 화면에 창이 뜨는지 확인
 4. smoke-test.bat 으로 전체 동작 점검 (14개 항목)

[문제가 생기면]
 doctor.bat        상태 종합 점검 (원인과 조치 방법 출력)
 dns-restore.bat   인터넷이 안 될 때 DNS 응급 복구
 uninstall.bat     제거 + PC 원상복구

[자녀 PC 에 설치할 때 꼭 확인]
 - 자녀 계정을 "표준 사용자"로 만들 것
   (관리자 계정이면 서비스를 꺼버릴 수 있어 의미가 없습니다)
 - 브라우저의 "보안 DNS(DoH)" 를 끌 것
   Chrome/Edge 설정 -> 개인정보 보호 -> 보안 DNS 사용 해제
   (켜져 있으면 DNS 차단이 우회됩니다)

[주요 텔레그램 명령]
 status              현재 상태 (막혔는지 + 왜 그런지)
 list                전체 명령 목록 (help 와 같음)

 대상 상태 - 셋 중 하나로만 정해집니다
 auto youtube        스케줄대로 (기본값)
 block youtube       스케줄 무시하고 계속 막음
 unblock youtube     스케줄 무시하고 계속 열어둠

 잠깐만 열어주기 - 시간이 지나면 원래대로 돌아갑니다
 youtube 30          YouTube 30분 허용
 roblox 60           Roblox 60분 허용

 schedule mon-thu 21:00 07:00
 schedule sat off

 msg 밥 먹고 하자    PC 화면에 메시지 띄우기 (답장도 받을 수 있음)

[차단 시간에 게임이 돌고 있으면]
 PC 화면에 경고가 뜨고 5분 뒤 자동으로 종료됩니다.
 (1분 남았을 때 한 번 더 알려줍니다)
 그 전에 "roblox 30" 으로 허용해 주면 종료가 취소됩니다.

[알림 트레이 앱]
 TimeBlocker.Notifier.exe 가 함께 설치되어 로그인 시 자동 실행됩니다.
 작업표시줄 오른쪽 아래 방패 아이콘으로 보입니다.
 - 경고창을 띄우고, 아이가 답장을 보낼 수 있게 해줍니다
 - 아이가 이 앱을 꺼도 차단은 그대로 동작합니다
   (경고가 Windows 기본 창으로 바뀔 뿐입니다)

자세한 내용은 README.md 를 참고하세요.
"@
[System.IO.File]::WriteAllText(
    (Join-Path $stagingDirectory '설치안내.txt'),
    $quickStart,
    (New-Object System.Text.UTF8Encoding($true)))   # 메모장에서 한글이 깨지지 않도록 BOM 포함

$fileCount = (Get-ChildItem $stagingDirectory -Recurse -File).Count
Write-Host "  파일 $fileCount 개 구성 완료"

# ------------------------------------------------------------ 3. 압축

Write-Host ""
Write-Host "[3/4] 압축" -ForegroundColor Yellow

if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

# zip 을 직접 구성한다.
#
# Compress-Archive 와 ZipFile.CreateFromDirectory(.NET Framework) 는 둘 다
# 경로 구분자로 역슬래시를 써서 zip 표준을 어긴다. Windows 탐색기는 문제없지만
# 7-Zip / macOS / Linux 에서 풀면 폴더 구조가 깨질 수 있다.
# 그래서 항목 이름을 직접 슬래시로 만들어 넣고, UTF-8 로 열어 한글 파일명도 보존한다.
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$archive = [System.IO.Compression.ZipFile]::Open(
    $zipPath, [System.IO.Compression.ZipArchiveMode]::Create, [System.Text.Encoding]::UTF8)
try {
    # 최상위 폴더(TimeBlocker-<버전>)까지 포함되도록 부모 기준으로 상대 경로를 만든다.
    $baseLength = (Split-Path $stagingDirectory -Parent).Length + 1

    foreach ($file in (Get-ChildItem $stagingDirectory -Recurse -File)) {
        # [char]92 = 역슬래시. 따옴표 안에 직접 쓰면 이스케이프 단계에서 사라지기 쉬워 코드로 지정한다.
        $entryName = $file.FullName.Substring($baseLength).Replace([char]92, '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}

# 구성 폴더는 지우고 zip 만 남긴다.
Remove-Item -LiteralPath $stagingDirectory -Recurse -Force

# ------------------------------------------------------------ 4. 결과

Write-Host ""
Write-Host "[4/4] 완료" -ForegroundColor Yellow

$zipInfo = Get-Item $zipPath
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " 릴리즈 패키지 생성 완료" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host ("  파일   : {0}" -f $zipInfo.FullName)
Write-Host ("  크기   : {0:N2} MB" -f ($zipInfo.Length / 1MB))
Write-Host ("  SHA256 : {0}" -f $hash)
Write-Host ""
Write-Host "  자녀 PC 로 옮겨 압축을 푼 뒤 install.bat 을 실행하세요." -ForegroundColor Green
Write-Host ""
