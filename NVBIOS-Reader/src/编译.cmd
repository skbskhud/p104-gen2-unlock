@echo off
REM ============================================================
REM  NVBIOSReader 编译脚本
REM  需要 .NET Framework 4.0 的 csc.exe（Windows 自带，不用装任何东西）
REM ============================================================
setlocal
set SRC=%~dp0
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [错误] 找不到 csc.exe，请确认已安装 .NET Framework 4.x
  pause
  exit /b 1
)

REM 输出目录：本脚本所在目录的【上一级】= 发布根目录
set OUT=%~dp0..
if not exist "%OUT%\bin" mkdir "%OUT%\bin"

echo 编译图形界面版 -^> NVBIOSReader.exe
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /unsafe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /win32manifest:"%SRC%app.manifest" ^
  /out:"%OUT%\NVBIOSReader.exe" "%SRC%NVBIOSReader.cs" "%SRC%Program.cs"
if errorlevel 1 goto fail

echo 编译命令行版 -^> bin\NVBIOSReader-CLI.exe
"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /unsafe /define:NVBIOS_CLI ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /win32manifest:"%SRC%app.manifest" ^
  /out:"%OUT%\bin\NVBIOSReader-CLI.exe" "%SRC%NVBIOSReader.cs" "%SRC%Program.cs"
if errorlevel 1 goto fail

echo.
echo [成功] 输出:
echo   %OUT%\NVBIOSReader.exe
echo   %OUT%\bin\NVBIOSReader-CLI.exe
exit /b 0

:fail
echo.
echo [失败] 编译出错，请把上面的错误信息发出来。
pause
exit /b 1
