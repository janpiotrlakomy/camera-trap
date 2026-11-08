@echo off
rem Builds camera-trap.exe with the C# compiler that ships with Windows. Re-run after editing camera-trap.cs.
rem ffmpeg.exe is downloaded once and embedded in camera-trap.exe, so the result is a single file.
rem Pinned FFmpeg release (GPL v3, see ffmpeg-LICENSE.txt). To update: change FFMPEG_VERSION, delete ffmpeg.exe,
rem check the new zip against the SHA-256 published on gyan.dev, and put the new ffmpeg.exe's SHA-256 below.
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set FFMPEG_VERSION=9.0.2
set FFMPEG=ffmpeg-%FFMPEG_VERSION%-essentials_build
rem SHA-256 of ffmpeg.exe from that zip (zip: 60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba)
set FFMPEG_SHA256=3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec

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
rem Only ever embed the exact ffmpeg.exe that was tested
certutil -hashfile "%~dp0ffmpeg.exe" SHA256 | findstr /i /x "%FFMPEG_SHA256%" >nul
if errorlevel 1 goto badhash
"%NET%\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0camera-trap.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /resource:"%~dp0ffmpeg.exe",ffmpeg.exe ^
  "%~dp0camera-trap.cs"
exit /b %errorlevel%

:failed
echo Could not download ffmpeg.
exit /b 1

:badhash
echo ffmpeg.exe does not match the expected SHA-256 - not building. Delete ffmpeg.exe to download it again.
exit /b 1
