@echo off
chcp 65001 >nul
setlocal

cd /d "%~dp0"

echo ==========================================================
echo  TimeBlocker 릴리즈 패키지 생성
echo ==========================================================
echo.
echo  빌드 + 테스트 + 게시 후 배포용 zip 을 만듭니다.
echo  (관리자 권한은 필요 없습니다)
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\make-release.ps1"
set REL_EXIT=%errorlevel%

echo.
pause
exit /b %REL_EXIT%
