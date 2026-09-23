@echo off
chcp 65001 >nul
setlocal

REM 관리자 권한이 있어야 모든 항목을 점검할 수 있다.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo  관리자 권한으로 실행하면 더 많은 항목을 점검할 수 있습니다.
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
    echo  build.bat 또는 install.bat 을 먼저 실행하세요.
    echo.
    pause
    exit /b 1
)

"%EXE%" doctor
set DOCTOR_EXIT=%errorlevel%

echo.
pause
exit /b %DOCTOR_EXIT%
