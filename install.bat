@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

REM ---------------------------------------------------------------
REM  관리자 권한이 없으면 스스로 권한 상승해서 다시 실행한다.
REM  (권한 상승 후에는 작업 폴더가 System32 로 바뀌므로 cd /d 로 되돌린다)
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

echo ==========================================================
echo  TimeBlocker 설치
echo ==========================================================
echo.

REM --- publish 폴더 확인 ---
if not exist "%~dp0publish\TimeBlocker.Service.exe" (
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
)

echo.
echo ----------------------------------------------------------
echo  Telegram 설정
echo ----------------------------------------------------------
echo.
echo  [Bot Token]  @BotFather 에게 /newbot 으로 만든 토큰
echo               예) 123456789:AAE-xxxxxxxxxxxxxxxxxxxxxxxxx
echo.
echo  [User ID]    @userinfobot 이 알려준 "Id:" 뒤의 숫자
echo               예) 123456789
echo               (@username 이 아니라 숫자입니다)
echo.
echo  ※ 만든 봇에게 /start 를 한 번 보내두어야 합니다.
echo.
echo  둘 다 비워두고 Enter 를 누르면 나중에 설정할 수 있습니다.
echo.

set "BOT_TOKEN="
set "ADMIN_ID="
set /p BOT_TOKEN=" Bot Token : "
set /p ADMIN_ID=" User ID   : "

echo.
echo ----------------------------------------------------------

if "!BOT_TOKEN!"=="" (
    echo  Telegram 설정 없이 설치합니다.
    echo  나중에 configure-telegram.bat 으로 설정할 수 있습니다.
    echo.
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-service.ps1"
) else (
    if "!ADMIN_ID!"=="" (
        echo  User ID 가 비어 있습니다. 둘 다 입력해야 Telegram 이 켜집니다.
        echo  Telegram 설정 없이 설치합니다.
        echo.
        powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-service.ps1"
    ) else (
        powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-service.ps1" -BotToken "!BOT_TOKEN!" -AdminUserId !ADMIN_ID!
    )
)

set INSTALL_EXIT=%errorlevel%

if %INSTALL_EXIT% neq 0 (
    echo.
    echo ==========================================================
    echo  설치 실패. 위 오류 메시지를 확인하세요.
    echo ==========================================================
    echo.
    pause
    exit /b %INSTALL_EXIT%
)

REM ---------------------------------------------------------------
REM  설치 직후 자동 점검. 문제가 있으면 여기서 바로 드러난다.
REM ---------------------------------------------------------------
echo.
echo ==========================================================
echo  설치 후 자동 점검 (doctor)
echo ==========================================================
echo.

"%ProgramFiles%\TimeBlocker\TimeBlocker.Service.exe" doctor

echo.
echo ==========================================================
echo  다음 단계
echo ==========================================================
echo.
echo  1. 브라우저로 아무 사이트나 열어 인터넷이 되는지 확인
echo     -^> 안 되면 dns-restore.bat 을 실행하세요
echo.
echo  2. 텔레그램에서 봇에게 "status" 를 보내 응답 확인
echo.
echo  3. smoke-test.bat 으로 전체 동작 점검
echo.
pause
exit /b 0
