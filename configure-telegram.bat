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
echo  Telegram 설정 변경
echo ==========================================================
echo.
echo  [Bot Token]  @BotFather 에게 /newbot 으로 만든 토큰
echo  [User ID]    숫자로 된 내 Telegram ID. 확인법:
echo               봇에게 메시지 하나 보낸 뒤 브라우저에서
echo               api.telegram.org/bot[Bot Token]/getUpdates 를 열어
echo               "from":{"id":숫자...} 의 숫자를 확인
echo.

set "BOT_TOKEN="
set "ADMIN_ID="
set /p BOT_TOKEN=" Bot Token : "
set /p ADMIN_ID=" User ID   : "

if "!BOT_TOKEN!"=="" goto :missing
if "!ADMIN_ID!"=="" goto :missing

echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\configure-telegram.ps1" -BotToken "!BOT_TOKEN!" -AdminUserId !ADMIN_ID!
echo.
pause
exit /b 0

:missing
echo.
echo  Bot Token 과 User ID 를 모두 입력해야 합니다.
echo.
pause
exit /b 1
