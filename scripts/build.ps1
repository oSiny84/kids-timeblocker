<#
.SYNOPSIS
    TimeBlocker 전체 빌드 및 게시(publish).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 게시 결과가 들어갈 폴더. 생략하면 저장소 루트의 publish 폴더.
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

# 주의: Windows PowerShell 5.1 에서는 param() 기본값에서 $PSScriptRoot 가 비어 있다.
# 반드시 본문에서 채워야 한다.
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'publish' }

Write-Host "=== TimeBlocker 빌드 ($Configuration) ===" -ForegroundColor Cyan

Push-Location $root
try {
    Write-Host "`n[1/3] 솔루션 빌드" -ForegroundColor Yellow
    dotnet build TimeBlocker.sln -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

    Write-Host "`n[2/3] 단위 테스트" -ForegroundColor Yellow
    dotnet test tests\TimeBlocker.Tests\TimeBlocker.Tests.csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "테스트 실패" }

    Write-Host "`n[3/3] 게시 -> $OutputDirectory" -ForegroundColor Yellow
    if (Test-Path $OutputDirectory) { Remove-Item -Recurse -Force $OutputDirectory }

    dotnet publish src\TimeBlocker.Service\TimeBlocker.Service.csproj `
        -c $Configuration -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw "Service 게시 실패" }

    dotnet publish src\TimeBlocker.Admin\TimeBlocker.Admin.csproj `
        -c $Configuration -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw "Admin 게시 실패" }

    Write-Host "`n완료. 게시 폴더: $OutputDirectory" -ForegroundColor Green
    Write-Host "다음 단계: 관리자 권한으로 scripts\install-service.ps1 실행" -ForegroundColor Green
}
finally {
    Pop-Location
}
