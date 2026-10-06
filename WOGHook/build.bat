@echo off
REM Build WOGHook.dll (x64) with MinGW-w64 g++ (preferred) or MSVC cl.
setlocal
cd /d "%~dp0"

where g++ >nul 2>&1
if not errorlevel 1 goto :mingw

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo ERROR: neither g++ nor Visual Studio found
    exit /b 1
)
for /f "usebackq delims=" %%i in (`
    "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat
`) do set "VCVARS=%%i"
if not defined VCVARS (
    echo ERROR: vcvars64.bat not found
    exit /b 1
)
call "%VCVARS%" >nul
if not exist bin mkdir bin
cl /nologo /LD /O2 /MT dllmain.cpp /Fe:bin\WOGHook.dll /link kernel32.lib user32.lib
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
del /Q dllmain.obj bin\WOGHook.exp bin\WOGHook.lib 2>nul
goto :done

:mingw
if not exist bin mkdir bin
g++ -shared -O2 -std=c++17 -mcx16 -static -s -o bin\WOGHook.dll dllmain.cpp -lkernel32 -luser32
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

:done
echo HOOK BUILD OK
endlocal
