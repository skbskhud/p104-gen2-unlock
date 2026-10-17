@echo off
chcp 65001 >nul 2>&1
title P104 PCIe Status
cd /d "%~dp0"

if not exist "bin\P104Gen2x4.exe" (
    echo.
    echo [ERROR] bin\P104Gen2x4.exe not found.
    echo         Please extract the WHOLE zip first.
    echo.
    pause
    exit /b 1
)

rem ---------------------------------------------------------------
rem This does NOT need Administrator rights.
rem It reads a real-time status file written by the hold process.
rem ---------------------------------------------------------------
net session >nul 2>&1
if errorlevel 1 (
    echo   [NOTE] Running WITHOUT Administrator rights.
    echo          Speed / width / hold-process status are fully readable.
    echo          Only the autostart-task check may show as "unknown".
    echo.
)

"bin\P104Gen2x4.exe" -status

echo.
echo  ------------------------------------------------------------
echo   [IMPORTANT] The RESULT line is printed BOTH at the TOP and at
echo               the BOTTOM of the output above. Look for:
echo.
echo                   RESULT:  Gen2 x4  (5.0 GT/s)  UNLOCKED
echo.
echo   [IMPORTANT] If GPU-Z or nvidia-smi shows "1.1 x4", that is
echo               EXPECTED on this mining card. They read the NVIDIA
echo               driver's reported value, which is unreliable here.
echo.
echo               Trust these two instead:
echo                 1) The RESULT line (raw LNKSTA based)
echo                 2) Real bandwidth test - run check-bw.cmd
echo.
echo   Need more register-level detail? Right-click status.cmd
echo   and choose "Run as administrator" to unlock -diag.
echo  ------------------------------------------------------------
echo.
pause
