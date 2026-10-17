@echo off
title P104 PCIe Gen2 x4 - Install
cd /d "%~dp0"

echo ============================================================
echo   P104-100 PCIe Gen2 x4 - Install
echo ============================================================
echo.

if not exist "bin\P104Gen2x4.exe" (
    echo [ERROR] bin\P104Gen2x4.exe not found.
    echo         Please extract the WHOLE zip first.
    echo.
    pause
    exit /b 1
)

net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Administrator rights required.
    echo         Close this window, then RIGHT-CLICK install.cmd
    echo         and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo [1/4] Deploying files and registering the autostart task...
"bin\P104Gen2x4.exe" -install

echo.
echo [2/4] Unlocking now (first pass)...
"bin\P104Gen2x4.exe" -once

echo.
echo [3/4] Register-level check (needs admin)...
"bin\P104Gen2x4.exe" -diag

echo.
echo [4/4] Status summary...
"bin\P104Gen2x4.exe" -status

echo.
echo ============================================================
echo   Done. A reboot is recommended.
echo.
echo   After reboot the link unlocks automatically in about
echo   1-2 minutes. The scheduled task retries a few times on
echo   purpose: the FIRST reset right after a cold boot usually
echo   is not enough on this card, the second one latches Gen2.
echo.
echo   To verify later:  run status.cmd   (no admin needed)
echo   To remove:        run uninstall.cmd as administrator
echo ============================================================
echo.
pause
