@echo off
setlocal
rem Builds RetroGleek.exe (the program) and RetroGleekCli.exe (its console companion for command-line audits)
rem using the C# compiler that is already part of Windows (.NET Framework 4.x).
rem No SDK, no NuGet, no internet access needed.
rem Every source file is listed in SRC below: add new .cs files to that list.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
rem WPF's imaging classes (Windows' own WebP, HEIC, AVIF, JPEG XL and RAW decoders) live in the WPF folder next to csc.exe
for %%I in ("%CSC%") do set "FW=%%~dpI"
set REFS=/lib:"%FW%WPF" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Microsoft.VisualBasic.dll /reference:System.Xml.dll /reference:System.Xaml.dll /reference:WindowsBase.dll /reference:PresentationCore.dll
set SRC="%~dp0Program.cs" "%~dp0Util.cs" "%~dp0Imaging.cs" "%~dp0Thumbnails.cs" "%~dp0Controls.cs" "%~dp0Canvas.cs" "%~dp0ThumbGrid.cs" "%~dp0MainForm.cs" "%~dp0MainForm.Files.cs" "%~dp0MainForm.Undo.cs" "%~dp0MainForm.Dual.cs" "%~dp0BatchRename.cs" "%~dp0Settings.cs" "%~dp0Audit.cs" "%~dp0ScreenEffects.cs" "%~dp0Marks.cs" "%~dp0ContactSheet.cs" "%~dp0JpegRotate.cs" "%~dp0WinCodecs.cs" "%~dp0RetroConvert.cs" "%~dp0Duplicates.cs" "%~dp0Metadata.cs" "%~dp0RECOIL.cs"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:"%~dp0RetroGleek.exe" /win32manifest:"%~dp0RetroGleek.manifest" %REFS% %SRC%
if errorlevel 1 (echo. & echo Build FAILED. & goto end)
echo. & echo Built RetroGleek.exe
rem Its SHA-256 fingerprint, to put in the release notes (certutil is part of Windows). Each build gets a different
rem one, because the compiler stamps the build time into the file.
set "HASH="
for /f "delims=" %%H in ('certutil -hashfile "%~dp0RetroGleek.exe" SHA256 ^| findstr /v ":"') do if not defined HASH set "HASH=%%H"
if defined HASH (set "HASH=%HASH: =%") else (set "HASH=could not be worked out")
echo SHA-256 of RetroGleek.exe: %HASH%
"%CSC%" /nologo /target:exe /define:CLI /optimize+ /platform:anycpu /out:"%~dp0RetroGleekCli.exe" /win32manifest:"%~dp0RetroGleek.manifest" %REFS% %SRC%
if errorlevel 1 (echo. & echo Building RetroGleekCli.exe FAILED.) else (echo Built RetroGleekCli.exe)
:end
pause
