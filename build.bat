@echo off
chcp 65001 >nul
setlocal

cd /d "%~dp0"

echo ==========================================================
echo  TimeBlocker 빌드
echo ==========================================================
echo.
echo  솔루션을 빌드하고, 단위 테스트를 돌리고,
echo  publish 폴더에 실행파일을 만듭니다.
echo.
echo  (관리자 권한은 필요 없습니다)
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
set BUILD_EXIT=%errorlevel%

echo.
if %BUILD_EXIT% neq 0 (
    echo ==========================================================
    echo  빌드 실패. 위 오류 메시지를 확인하세요.
    echo ==========================================================
) else (
    echo ==========================================================
    echo  빌드 완료.
    echo  다음 단계: install.bat 을 실행하세요.
    echo ==========================================================
)

echo.
pause
exit /b %BUILD_EXIT%
