@echo off
setlocal
cd /d "%~dp0"

call "%~dp0WOGHook\build.bat"
if errorlevel 1 (
    echo HOOK BUILD FAILED
    exit /b 1
)

dotnet publish WOG_Trainer\WOG_Trainer.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 (
    echo TRAINER BUILD FAILED
    exit /b 1
)

if not exist TrainerBuild mkdir TrainerBuild
copy /Y "publish\WOG Helper.exe" "TrainerBuild\WOG Helper.exe" >nul
if errorlevel 1 (
    echo COPY FAILED - "TrainerBuild\WOG Helper.exe" is running. Exit it ^(tray icon - Exit^) and build again.
    exit /b 1
)
rem The game keeps an injected WOGHook.dll loaded until it exits. The app still works with the
rem old copy as long as the hook source did not change.
copy /Y "WOGHook\bin\WOGHook.dll" "TrainerBuild\WOGHook.dll" >nul
if errorlevel 1 echo WARNING - TrainerBuild\WOGHook.dll is in use by the game and was not updated.

echo.
echo ALL BUILD OK
echo Run: TrainerBuild\WOG Helper.exe
endlocal
