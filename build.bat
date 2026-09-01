@echo off
rem ============================================
rem  远程桌面快切 - 一键编译脚本
rem  使用 Windows 自带的 .NET Framework 编译器，无需安装任何开发环境
rem ============================================
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [错误] 未找到 .NET Framework 自带编译器 csc.exe
    pause
    exit /b 1
)

echo 正在编译...
"%CSC%" /nologo /target:winexe /platform:anycpu /out:"%~dp0远程桌面快切.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%~dp0Program.cs"

if errorlevel 1 (
    echo [错误] 编译失败，请检查上方报错信息。
    pause
    exit /b 1
)

echo [成功] 已生成：远程桌面快切.exe，双击即可使用。
pause
