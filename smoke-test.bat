@echo off
chcp 65001 >nul
setlocal

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

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\smoke-test.ps1"
set TEST_EXIT=%errorlevel%

echo.
pause
exit /b %TEST_EXIT%
