@echo off
:: Builds ChessRankWidget.exe using the C# compiler bundled with .NET Framework 4.x
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:ChessRankWidget.exe /win32icon:assets\app.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:"%FW%\System.Net.Http.dll" /r:"%FW%\System.Web.Extensions.dll" ^
  /r:"%WPF%\WindowsBase.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\PresentationFramework.dll" ^
  ChessRankWidget.cs
exit /b %ERRORLEVEL%
