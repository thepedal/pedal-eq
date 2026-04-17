@echo off
REM ============================================================
REM  Pedal EQ — one-click build for ReBuzz
REM ============================================================
REM
REM  Requirements:
REM    .NET 10 SDK (x64)  https://dotnet.microsoft.com/download/dotnet/10.0
REM    ReBuzz installed at the default location, OR edit REBUZZ_DIR below.
REM
REM  Output:
REM    "Pedal EQ.NET.dll"  →  %REBUZZ_DIR%\Gear\Effects\
REM
REM  Then restart ReBuzz — "Pedal EQ" will appear under Effects.
REM ============================================================

set REBUZZ_DIR=C:\Program Files\ReBuzz

echo.
echo Building Pedal EQ.NET ...
echo Target: %REBUZZ_DIR%\Gear\Effects\
echo.

dotnet build PedalEQ.csproj -c Release /p:BuzzDir="%REBUZZ_DIR%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo BUILD FAILED. Check the errors above.
    echo   - Make sure the .NET 10 SDK is installed
    echo   - Make sure REBUZZ_DIR points to your ReBuzz installation
    pause
    exit /b 1
)

echo.
echo Build succeeded.
echo "Pedal EQ.NET.dll" is now in %REBUZZ_DIR%\Gear\Effects\
echo Restart ReBuzz and the effect will appear under Effects.
echo.
pause
