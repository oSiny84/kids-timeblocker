@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

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

echo ==========================================================
echo  TimeBlocker 제거
echo ==========================================================
echo.
echo  서비스를 제거하고 PC 를 원래 상태로 되돌립니다.
echo    - 어댑터 DNS 복원
echo    - hosts 차단 구간 제거
echo    - 방화벽 규칙 제거
echo.
set "REMOVEDATA="
set /p REMOVEDATA=" 설정과 로그까지 모두 지울까요? (Y/N, 기본 N): "

echo.
if /i "!REMOVEDATA!"=="Y" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall-service.ps1" -RemoveData
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall-service.ps1"
)
set UNINSTALL_EXIT=%errorlevel%

echo.
echo  현재 DNS 설정:
powershell -NoProfile -Command "Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses.Count -gt 0 } | ForEach-Object { '   ' + $_.InterfaceAlias + ' : ' + ($_.ServerAddresses -join ', ') }"

echo.
pause
exit /b %UNINSTALL_EXIT%
