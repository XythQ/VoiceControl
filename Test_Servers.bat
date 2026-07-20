@echo off
title XNPCVoiceControl - Server Health Check
color 07
cd /d "%~dp0"

echo.
echo ==========================================
echo   XNPCVoiceControl Server Health Check
echo ==========================================
echo.

REM --- Check server executables exist ---
set LlamaExe=MISSING
set KokoroExe=MISSING
set WhisperExe=MISSING

if exist "bin\LlamaServer\llama-server.exe" set LlamaExe=FOUND
if exist "bin\KokoroServer\kokoro-server.exe" set KokoroExe=FOUND
if exist "bin\WhisperServer\whisper-server.exe" set WhisperExe=FOUND

REM --- Check ports are listening ---
set LlamaPort=NOT LISTENING
set KokoroPort=NOT LISTENING
set WhisperPort=NOT LISTENING

:: Pipe through two findstr filters to avoid the space-as-OR trap
for /f "tokens=5" %%a in ('netstat -ano ^| findstr ":8080 " ^| findstr "LISTENING"') do if not "%%a"=="" set LlamaPort=LISTENING
for /f "tokens=5" %%a in ('netstat -ano ^| findstr ":5050 " ^| findstr "LISTENING"') do if not "%%a"=="" set KokoroPort=LISTENING
for /f "tokens=5" %%a in ('netstat -ano ^| findstr ":5052 " ^| findstr "LISTENING"') do if not "%%a"=="" set WhisperPort=LISTENING

REM --- Check .NET runtime (only matters for kokoro if not self-contained) ---
dotnet --version >nul 2>&1
if errorlevel 9009 (
    set DotNetStatus=NOT FOUND
) else (
    for /f "tokens=* usebackq" %%v in (`dotnet --version`) do set DotNetStatus=%%v
)

REM --- Summary Report ---
echo ==========================================
echo   SERVER HEALTH REPORT
echo ==========================================
echo.
echo   .NET Runtime:     %DotNetStatus%
echo.

if "%LlamaExe%"=="FOUND" (
    if "%LlamaPort%"=="LISTENING" (
        echo   [PASS] llama-server     - Exe found, port 8080 listening
    ) else (
        echo   [FAIL] llama-server     - Exe found, port 8080 NOT responding
        echo                        Run bin\LlamaServer\Start_Llama_Debug.bat to test manually
    )
) else (
    echo [MISS] llama-server     - Exe not found in bin\LlamaServer\
)

if "%KokoroExe%"=="FOUND" (
    if "%KokoroPort%"=="LISTENING" (
        echo   [PASS] kokoro-server    - Exe found, port 5050 listening
    ) else (
        echo   [FAIL] kokoro-server    - Exe found, port 5050 NOT responding
        echo                        Run bin\KokoroServer\Start_Kokoro_Debug.bat to test manually
    )
) else (
    echo [MISS] kokoro-server    - Exe not found in bin\KokoroServer\
)

if "%WhisperExe%"=="FOUND" (
    if "%WhisperPort%"=="LISTENING" (
        echo   [PASS] whisper-server   - Exe found, port 5052 listening
    ) else (
        echo   [FAIL] whisper-server   - Exe found, port 5052 NOT responding
        echo                        Run bin\WhisperServer\Start_Whisper_Debug.bat to test manually
    )
) else (
    echo [MISS] whisper-server   - Exe not found in bin\WhisperServer\
)

echo.
echo ==========================================
echo How to use:
echo   1. Launch the game first -> ServerManager starts servers automatically
echo   2. Run this script from another CMD window to check status
echo   3. For any [FAIL], run the suggested debug .bat for that server
echo      (it keeps the window open so you can read errors)
echo ==========================================
echo.

pause
