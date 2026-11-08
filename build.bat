@echo off
rem Builds camera-trap.exe with the C# compiler that ships with Windows. Re-run after editing camera-trap.cs.
rem ffmpeg.exe is downloaded once and embedded in camera-trap.exe, so the result is a single file.
rem Pinned FFmpeg release (GPL v3, see ffmpeg-LICENSE.txt); to update, change FFMPEG_VERSION and delete ffmpeg.exe.
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set FFMPEG_VERSION=9.0.2
set FFMPEG=ffmpeg-%FFMPEG_VERSION%-essentials_build

if exist "%~dp0ffmpeg.exe" goto build
echo Downloading FFmpeg %FFMPEG_VERSION%...
curl -sSfL -o "%TEMP%\%FFMPEG%.zip" https://github.com/GyanD/codexffmpeg/releases/download/%FFMPEG_VERSION%/%FFMPEG%.zip
if errorlevel 1 goto failed
rem tar can't handle non-ASCII folder names, so unpack in %TEMP% and move it here
tar -xf "%TEMP%\%FFMPEG%.zip" -C "%TEMP%" %FFMPEG%/bin/ffmpeg.exe
if errorlevel 1 goto failed
move /y "%TEMP%\%FFMPEG%\bin\ffmpeg.exe" "%~dp0ffmpeg.exe" >nul
rd /s /q "%TEMP%\%FFMPEG%"
del "%TEMP%\%FFMPEG%.zip"

:build
"%NET%\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0camera-trap.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /resource:"%~dp0ffmpeg.exe",ffmpeg.exe ^
  "%~dp0camera-trap.cs"
exit /b %errorlevel%

:failed
echo Could not download ffmpeg.
exit /b 1
