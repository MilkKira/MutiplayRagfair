@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\CrossRagfair.Hub\CrossRagfair.Hub.csproj"
set "CERTIFICATE_SOURCE=%ROOT%certs\certificate.pfx"
set "BUILD_DIR=%ROOT%Build"
set "OUTPUT_DIR=%BUILD_DIR%\WindowsHub"
set "STAGING_DIR=%BUILD_DIR%\.WindowsHub.tmp"
set "RUNTIME_ID=win-x64"
if not "%~1"=="" set "RUNTIME_ID=%~1"

if /i not "%RUNTIME_ID%"=="win-x64" if /i not "%RUNTIME_ID%"=="win-arm64" (
    echo [ERROR] Unsupported Windows runtime: %RUNTIME_ID%
    echo         Use win-x64 or win-arm64.
    exit /b 2
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet was not found. Install the .NET 9 SDK and try again.
    exit /b 1
)

if not exist "%CERTIFICATE_SOURCE%" (
    echo [ERROR] Hub certificate was not found:
    echo         %CERTIFICATE_SOURCE%
    exit /b 1
)

pushd "%ROOT%" >nul

echo [1/2] Publishing the Windows Hub for %RUNTIME_ID%...
if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"
if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"

dotnet publish "%PROJECT%" ^
    --configuration Release ^
    --runtime "%RUNTIME_ID%" ^
    --self-contained true ^
    --nologo ^
    -p:UseAppHost=true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:NuGetAudit=false ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    --output "%STAGING_DIR%"
if errorlevel 1 goto :publish_failed

copy /y "%ROOT%LICENSE" "%STAGING_DIR%\" >nul
if errorlevel 1 goto :package_failed
copy /y "%CERTIFICATE_SOURCE%" "%STAGING_DIR%\certificate.pfx" >nul
if errorlevel 1 goto :package_failed

if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
move "%STAGING_DIR%" "%OUTPUT_DIR%" >nul
if errorlevel 1 goto :package_failed

echo [2/2] Publish complete.
echo [OK] Self-contained single-file Windows Hub created at:
echo      %OUTPUT_DIR%
popd >nul
exit /b 0

:publish_failed
set "EXIT_CODE=%ERRORLEVEL%"
if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
echo [ERROR] The Windows Hub publish failed.
popd >nul
exit /b %EXIT_CODE%

:package_failed
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
echo [ERROR] The Windows Hub package could not be created.
popd >nul
exit /b %EXIT_CODE%
