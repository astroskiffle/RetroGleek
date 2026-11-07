@echo off
setlocal
rem Builds RetroGleek.exe using the C# compiler that is already part of Windows (.NET Framework 4.x).
rem No SDK, no NuGet, no internet access needed.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:"%~dp0RetroGleek.exe" /win32manifest:"%~dp0RetroGleek.manifest" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Microsoft.VisualBasic.dll "%~dp0Program.cs" "%~dp0RECOIL.cs"
if errorlevel 1 (echo. & echo Build FAILED.) else (echo. & echo Built RetroGleek.exe)
pause
