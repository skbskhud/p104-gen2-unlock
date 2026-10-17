@echo off
chcp 65001 >nul 2>&1
title P104 PCIe Bandwidth Check
cd /d "%~dp0"

echo.
echo  ============================================================
echo   P104-100 PCIe BANDWIDTH TEST (the only trustworthy judge)
echo  ============================================================
echo.
echo   WHY: nvidia-smi / GPU-Z read the NVIDIA driver's reported
echo        value, which is UNRELIABLE on this mining card
echo        (it always reports Gen1). Real bandwidth is the only
echo        honest measurement.
echo.
echo   REFERENCE (x4 width):
echo     Gen1 x4  =  approx 0.78 GB/s
echo     Gen2 x4  =  approx 1.55 GB/s    [ TARGET ]
echo     Gen3 x4  =  approx 3.10 GB/s
echo.
echo  ------------------------------------------------------------
echo.

rem ---- locate a usable Python (must have the torch package) ----
set PY=
where python >nul 2>&1 && set PY=python
if not defined PY where py >nul 2>&1 && set PY=py

if not defined PY (
    echo   [No Python found - use GPU-Z instead]
    echo.
    echo   Open GPU-Z, click the [?] button next to "Bus Interface".
    echo   It runs a real PCIe bandwidth test.
    echo.
    echo   Reference: Gen1 x4 = 0.78 / Gen2 x4 = 1.55 / Gen3 x4 = 3.10 GB/s
    echo.
    pause
    exit /b 0
)

echo   Using Python: %PY%
echo.
"%PY%" "%~dp0bw_test.py"

echo.
pause
