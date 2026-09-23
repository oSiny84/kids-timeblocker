@echo off
chcp 65001 >nul
setlocal

REM ---------------------------------------------------------------
REM  응급 복구용. 어댑터 DNS 를 원래 설정으로 되돌린다.
REM  서비스가 죽어서 인터넷이 안 될 때 이것만 실행하면 된다.
REM ---------------------------------------------------------------
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo  관리자 권한이 필요합니다.
    echo  권한 상승 창이 뜨면 "예" 를 눌러주세요.
    echo.
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

set "EXE=%ProgramFiles%\TimeBlocker\TimeBlocker.Service.exe"
if not exist "%EXE%" set "EXE=%~dp0publish\TimeBlocker.Service.exe"

if not exist "%EXE%" (
    echo  TimeBlocker 실행파일을 찾을 수 없습니다.
    echo  네트워크 설정에서 DNS 를 "자동으로 DNS 서버 주소 받기" 로 직접 바꾸세요.
    echo.
    pause
    exit /b 1
)

echo ==========================================================
echo  어댑터 DNS 원상복구
echo ==========================================================
echo.

"%EXE%" dns-restore

echo.
echo  현재 DNS 설정:
powershell -NoProfile -Command "Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses.Count -gt 0 } | ForEach-Object { '   ' + $_.InterfaceAlias + ' : ' + ($_.ServerAddresses -join ', ') }"

echo.
pause
