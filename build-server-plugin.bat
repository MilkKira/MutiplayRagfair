@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\CrossRagfair.Spt\CrossRagfair.Spt.csproj"
set "SOURCE_DIR=%ROOT%src\CrossRagfair.Spt\bin\Release\net9.0"
set "BUILD_DIR=%ROOT%Build"
set "OUTPUT_DIR=%BUILD_DIR%\ServerPlugin"
set "STAGING_DIR=%BUILD_DIR%\.ServerPlugin.tmp"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet was not found. Install the .NET 9 SDK and try again.
    exit /b 1
)

pushd "%ROOT%" >nul

echo [1/3] Building the SPT server plugin...
dotnet build "%PROJECT%" --configuration Release --nologo
if errorlevel 1 goto :build_failed

for %%F in (
    "CrossRagfair.Spt.dll"
    "CrossRagfair.Core.dll"
    "CrossRagfair.Contracts.dll"
) do (
    if not exist "%SOURCE_DIR%\%%~F" (
        echo [ERROR] Expected build output is missing: %SOURCE_DIR%\%%~F
        goto :package_failed
    )
)

echo [2/3] Creating the plugin package...
if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"
if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
mkdir "%STAGING_DIR%"
if errorlevel 1 goto :package_failed

copy /y "%SOURCE_DIR%\CrossRagfair.Spt.dll" "%STAGING_DIR%\" >nul
if errorlevel 1 goto :package_failed
copy /y "%SOURCE_DIR%\CrossRagfair.Core.dll" "%STAGING_DIR%\" >nul
if errorlevel 1 goto :package_failed
copy /y "%SOURCE_DIR%\CrossRagfair.Contracts.dll" "%STAGING_DIR%\" >nul
if errorlevel 1 goto :package_failed
copy /y "%ROOT%src\CrossRagfair.Spt\config.json" "%STAGING_DIR%\" >nul
if errorlevel 1 goto :package_failed
copy /y "%ROOT%LICENSE" "%STAGING_DIR%\" >nul
if errorlevel 1 goto :package_failed

if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
move "%STAGING_DIR%" "%OUTPUT_DIR%" >nul
if errorlevel 1 goto :package_failed

echo [3/3] Verifying the package...
for %%F in (
    "%OUTPUT_DIR%\SPTarkov.*"
    "%OUTPUT_DIR%\SPT.Server*"
    "%OUTPUT_DIR%\0Harmony.dll"
) do (
    if exist "%%~F" (
        echo [ERROR] Server-owned dependency must not be packaged: %%~F
        goto :package_failed
    )
)

echo [OK] Server plugin created at:
echo      %OUTPUT_DIR%
popd >nul
exit /b 0

:build_failed
set "EXIT_CODE=%ERRORLEVEL%"
echo [ERROR] The server plugin build failed.
popd >nul
exit /b %EXIT_CODE%

:package_failed
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
echo [ERROR] The server plugin package could not be created.
popd >nul
exit /b %EXIT_CODE%
