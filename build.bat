@echo off
rem Builds camera-trap.exe with the C# compiler that ships with Windows. Re-run after editing camera-trap.cs.
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WINMD=%WINDIR%\System32\WinMetadata

"%NET%\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0camera-trap.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%NET%\System.Runtime.dll" ^
  /r:"%WINMD%\Windows.Foundation.winmd" /r:"%WINMD%\Windows.Media.winmd" /r:"%WINMD%\Windows.Storage.winmd" ^
  "%~dp0camera-trap.cs"
