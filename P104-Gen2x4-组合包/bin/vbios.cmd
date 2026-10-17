@echo off
REM ============================================================
REM  P104 VBIOS Verify - read the VBIOS from the card and hash it
REM  Read-only. Does NOT flash, does NOT reboot.
REM  Run as Administrator.
REM ============================================================
setlocal
set EXE=C:\Program Files\P104Gen2x4\P104Gen2x4.exe
if not exist "%EXE%" set EXE=%~dp0P104Gen2x4.exe
echo.
echo  Reading VBIOS from the card (read-only)...
echo.
"%EXE%" -vbios %*
echo.
echo  Compare the MD5 above with your backup:
echo    certutil -hashfile ^<your-backup.rom^> MD5
echo.
pause