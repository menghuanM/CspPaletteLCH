@echo off
REM ============================================================================
REM  CspPaletteLCH build script
REM
REM  Compiles src\*.cs into build\CspPaletteLCH.exe using the C# compiler and
REM  WPF assemblies that ship with Windows (.NET Framework 4.x).
REM  No SDK, no NuGet, no network access, no admin rights required.
REM
REM  Usage:
REM    build.bat           compile
REM    build.bat run       compile and launch
REM    build.bat clean     delete build directory
REM    build.bat tests     compile and run the assertion tests
REM
REM  WARNING: this file must stay ASCII-only. cmd.exe reads .bat files with the
REM  system ANSI code page; non-ASCII bytes here corrupt the script.
REM ============================================================================

setlocal
setlocal EnableDelayedExpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "SRC=%ROOT%\src"
set "BUILD=%ROOT%\build"
set "OUT=%BUILD%\CspPaletteLCH.exe"
set "FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
set "CSC=%FW%\csc.exe"

REM ---------------------------------------------------------------- arguments
set "MODE=%~1"
if "%MODE%"=="" set "MODE=build"

REM Flags are decided here so nothing can be decided by "falling through" later.
set "RUNMODE="
set "TESTMODE="
set "SMOKEMODE="
set "STYLEMODE="

if /i "%MODE%"=="clean" goto :clean
if /i "%MODE%"=="run"   set "RUNMODE=1"
if /i "%MODE%"=="tests" set "TESTMODE=1"
if /i "%MODE%"=="smoke" set "SMOKEMODE=1"
if /i "%MODE%"=="style" set "STYLEMODE=1"
if /i not "%MODE%"=="build" if not defined RUNMODE if not defined TESTMODE if not defined SMOKEMODE if not defined STYLEMODE (
    echo Unknown argument: %MODE%
    echo Usage: build.bat [build^|run^|tests^|smoke^|style^|clean]
    exit /b 2
)

REM The style lab does not need the main exe at all - it compiles its own copy
REM of the widget sources. Jump straight there so a broken/undeletable
REM build\CspPaletteLCH.exe can never block style tuning.
if defined STYLEMODE goto :stylelab

goto :check

REM ------------------------------------------------------------- preflight
:check
if not exist "%CSC%" (
    echo [ERROR] C# compiler not found:
    echo         %CSC%
    echo         .NET Framework 4.x is a Windows component; if it is missing,
    echo         enable it in "Turn Windows features on or off".
    exit /b 1
)
if not exist "%WPF%\PresentationFramework.dll" (
    echo [ERROR] WPF assemblies not found under:
    echo         %WPF%
    exit /b 1
)
if not exist "%SRC%" (
    echo [ERROR] source folder not found: %SRC%
    exit /b 1
)

REM Jump over the clean block: without this, control falls straight through
REM into :clean and prints "Nothing to clean." on every normal build.
cd /d "%ROOT%" >nul 2>&1
goto :sources

REM ---------------------------------------------------------------- clean
:clean
if exist "%BUILD%" (
    rmdir /s /q "%BUILD%"
    echo Cleaned %BUILD%
) else (
    echo Nothing to clean.
)
exit /b 0

REM ---------------------------------------------------------------- sources
:sources
if not exist "%BUILD%" mkdir "%BUILD%" >nul 2>&1

REM Collect every .cs under src\ (the tests folder has its own entry points).
set "SOURCES="
set "COUNT=0"
for /r "%SRC%" %%F in (*.cs) do (
    set "SOURCES=!SOURCES! "%%F""
    set /a COUNT+=1
)
if %COUNT%==0 (
    echo [ERROR] no .cs files found under %SRC%
    exit /b 1
)

REM ---------------------------------------------------------------- icon
if not exist "%SRC%\App.ico" (
    echo [WARN] src\App.ico is missing; building without an application icon.
    set "ICONARGS="
) else (
    set "ICONARGS=/win32icon:"%SRC%\App.ico" /resource:"%SRC%\App.ico",CspPaletteLCH.App.ico"
)

REM ---------------------------------------------------------------- manifest
if exist "%SRC%\App.manifest" (
    set "MANIFEST=/win32manifest:"%SRC%\App.manifest""
) else (
    set "MANIFEST="
)

REM ---------------------------------------------------------------- compile
echo Compiling %COUNT% source files...
"%CSC%" /nologo /target:winexe /platform:anycpu /langversion:5 /codepage:65001 /optimize+ /debug- ^
    /out:"%OUT%" ^
    %ICONARGS% %MANIFEST% ^
    /r:"%WPF%\PresentationFramework.dll" ^
    /r:"%WPF%\PresentationCore.dll" ^
    /r:"%WPF%\WindowsBase.dll" ^
    /r:"%FW%\System.Xaml.dll" ^
    /r:"%FW%\System.dll" ^
    /r:"%FW%\System.Core.dll" ^
    /r:"%FW%\System.Drawing.dll" ^
    /r:"%FW%\System.Windows.Forms.dll" ^
    /r:"%FW%\System.Web.Extensions.dll" ^
    %SOURCES%

if errorlevel 1 (
    echo.
    echo [FAILED] compilation failed. See the errors above.
    exit /b 1
)
if not exist "%OUT%" (
    echo [FAILED] compiler reported success but %OUT% is missing.
    exit /b 1
)

REM ---------------------------------------------------------------- app.config
if exist "%SRC%\App.config" copy /y "%SRC%\App.config" "%BUILD%\CspPaletteLCH.exe.config" >nul

echo.
echo [OK] Build succeeded
for %%F in ("%OUT%") do echo      output: %%~fF
for %%F in ("%OUT%") do echo      size  : %%~zF bytes

REM ---------------------------------------------------------------- dispatch
REM Every mode exits here. Nothing may fall through into the subroutines below.
if defined TESTMODE goto :do_tests
if defined SMOKEMODE goto :do_smoke
if defined STYLEMODE goto :do_style
if defined RUNMODE goto :do_run
exit /b 0

:do_style
call :stylelab
if errorlevel 1 exit /b 1
exit /b 0

:do_tests
call :tests
if errorlevel 1 exit /b 1
exit /b 0

:do_smoke
call :smoke
if errorlevel 1 exit /b 1
exit /b 0

:do_run
call :launch
if errorlevel 1 exit /b 1
exit /b 0

REM ===========================================================================
REM  Launch. "start" is deliberately not used: it hands the image to Explorer,
REM  which runs the SmartScreen check on an unsigned exe. When that check blocks
REM  the launch there is no error code - the process just never appears. Going
REM  through WScript.Shell.Run calls CreateProcess directly, and does not block
REM  this script. The .vbs is written next to the exe (not %TEMP%) because TEMP
REM  is not always writable/healty and a missing script only shows up as
REM  "There is no script file specified".
REM ===========================================================================
:launch
set "LAUNCHVBS=%BUILD%\_launch.vbs"
del "%LAUNCHVBS%" >nul 2>&1
> "%LAUNCHVBS%" echo Set sh = CreateObject("WScript.Shell")
>> "%LAUNCHVBS%" echo sh.CurrentDirectory = "%BUILD%"
>> "%LAUNCHVBS%" echo sh.Run """" ^& "%OUT%" ^& """", 1, False

if not exist "%LAUNCHVBS%" (
    echo [WARN] could not write %LAUNCHVBS%
    echo        Run this directly instead:
    echo        "%OUT%"
    exit /b 1
)

echo.
echo Launching %OUT%
cscript //nologo "%LAUNCHVBS%"
set "LAUNCHRC=%ERRORLEVEL%"
del "%LAUNCHVBS%" >nul 2>&1
if not "%LAUNCHRC%"=="0" (
    echo [WARN] launch failed.
    echo        Run this directly instead:
    echo        "%OUT%"
    exit /b 1
)
echo      OK
exit /b 0

REM ===========================================================================
REM  Assertion tests. Each suite is a standalone console exe whose Main returns
REM  non-zero on failure. Suites need their dependency .cs compiled in, which is
REM  why they are listed explicitly instead of globbing the folder.
REM ===========================================================================
:tests
set "TDIR=%BUILD%\tests"
if not exist "%TDIR%" mkdir "%TDIR%" >nul 2>&1
set "FAILED=0"

call :suite ColorMath        "%ROOT%\tests\ColorMathTest\Program.cs"       "%SRC%\Color\ColorMath.cs"
call :suite SummonPlacement  "%ROOT%\tests\SummonPlacementTest\Program.cs" "%SRC%\UI\SummonPlacement.cs"
call :suite TraySupport      "%ROOT%\tests\TraySupportTest\Program.cs"     "%SRC%\UI\TraySupport.cs"

if "%FAILED%"=="1" (
    echo.
    echo [FAILED] %FAILED% test suite^(s^) failed.
    exit /b 1
)
echo.
echo [OK] All test suites passed.
exit /b 0

:suite
set "NAME=%~1"
set "MAIN=%~2"
set "DEP=%~3"
if not exist "%MAIN%" (
    echo [skip] %NAME% - %MAIN% not present
    exit /b 0
)
if not exist "%DEP%" (
    echo [skip] %NAME% - %DEP% not present
    exit /b 0
)
echo.
echo -- %NAME%
"%CSC%" /nologo /target:exe /out:"%TDIR%\%NAME%.exe" "%MAIN%" "%DEP%" >nul
if errorlevel 1 (
    echo    compile FAILED
    set "FAILED=1"
    exit /b 0
)
"%TDIR%\%NAME%.exe"
if errorlevel 1 set "FAILED=1"
exit /b 0

REM ===========================================================================
REM  Smoke test: the real panel exe driven by real mouse input against a fake
REM  CSP window. This one moves the actual cursor for ~90 seconds, so it is a
REM  separate mode and never runs as part of `tests`.
REM
REM  FakeCsp is compiled as CLIPStudioPaint.exe on purpose: the panel finds the
REM  CSP window by process name.
REM ===========================================================================
:smoke
set "TDIR=%BUILD%\tests"
if not exist "%TDIR%" mkdir "%TDIR%" >nul 2>&1
set "SREF=/r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll""

if not exist "%ROOT%\tests\SmokeTest\SmokeDriver.cs" (
    echo [skip] smoke test sources not present
    exit /b 0
)

echo.
echo -- building smoke harness
"%CSC%" /nologo /target:exe   /out:"%TDIR%\SmokeDriver.exe" %SREF% "%ROOT%\tests\SmokeTest\SmokeDriver.cs"
if errorlevel 1 exit /b 1
"%CSC%" /nologo /target:winexe /out:"%TDIR%\CLIPStudioPaint.exe" %SREF% "%ROOT%\tests\SmokeTest\FakeCsp.cs"
if errorlevel 1 exit /b 1
"%CSC%" /nologo /target:winexe /out:"%TDIR%\OtherApp.exe" %SREF% "%ROOT%\tests\SmokeTest\OtherApp.cs"
if errorlevel 1 exit /b 1

echo.
echo -- running smoke test ^(the cursor will move^)
"%TDIR%\SmokeDriver.exe" "%TDIR%\CLIPStudioPaint.exe" "%OUT%" "%TDIR%\OtherApp.exe"
if errorlevel 1 (
    echo.
    echo [FAILED] smoke test failed.
    exit /b 1
)
echo.
echo [OK] smoke test passed.
exit /b 0

REM ===========================================================================
REM  Style lab: a small GUI where the panel's spacing/size constants can be
REM  dragged around with live preview, so tuning does not need a rebuild each
REM  time. It compiles the real GradientSlider / ValueBox sources, so the
REM  preview is the actual widget, not an approximation.
REM ===========================================================================
:stylelab
set "LAB=%BUILD%\stylelab"
if not exist "%LAB%" mkdir "%LAB%" >nul 2>&1

echo.
echo -- building style lab
"%CSC%" /nologo /target:winexe /out:"%LAB%\StyleLab.exe" ^
    /r:"%WPF%\PresentationFramework.dll" ^
    /r:"%WPF%\PresentationCore.dll" ^
    /r:"%WPF%\WindowsBase.dll" ^
    /r:"%FW%\System.Xaml.dll" ^
    /r:"%FW%\System.dll" ^
    /r:"%FW%\System.Core.dll" ^
    /r:"%FW%\System.Drawing.dll" ^
    /r:"%FW%\System.Windows.Forms.dll" ^
    "%ROOT%\tools\StyleLab\StyleLab.cs" ^
    "%SRC%\Color\ColorMath.cs" ^
    "%SRC%\UI\Theme.cs" ^
    "%SRC%\UI\Magnifier.cs" ^
    "%SRC%\UI\GradientSlider.cs" ^
    "%SRC%\UI\ValueBox.cs"
if errorlevel 1 exit /b 1

echo -- launching style lab
echo    (drag the sliders, then press the copy button and send me the values)
"%LAB%\StyleLab.exe"
echo.
echo [OK] style lab closed.
exit /b 0
