@echo off
title P104 PCIe Gen2 x4 - Uninstall
cd /d "%~dp0"

echo ============================================================
echo   P104-100 PCIe Gen2 x4 - Uninstall
echo ============================================================
echo.

net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Administrator rights required.
    echo         Right-click uninstall.cmd and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo [1/4] Stopping the hold process...
schtasks /end /tn "P104 PCIe Gen2x4 Bring-up" >nul 2>&1
rem Give it a moment to exit gracefully, then force-stop if still alive.
ping -n 3 127.0.0.1 >nul 2>&1
taskkill /F /IM P104Gen2x4.exe >nul 2>&1
echo       done

echo [2/4] Removing autostart task, drivers and registry policy...
"%~dp0P104Gen2x4.exe" -uninstall

echo [3/4] Restoring GPU registers...
"%~dp0P104Gen2x4.exe" -off

echo [4/4] Deleting installed files...
if exist "C:\Program Files\P104Gen2x4" rmdir /S /Q "C:\Program Files\P104Gen2x4"
if exist "C:\ProgramData\P104Gen2x4" rmdir /S /Q "C:\ProgramData\P104Gen2x4"
echo       done

echo.
echo ============================================================
echo   Uninstalled. Please REBOOT to fully restore the GPU.
echo ============================================================
echo.
pause
