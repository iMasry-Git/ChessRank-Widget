@echo off
:: ChessRank Widget v3.0.0 - Windows 11 Native Desktop Widget Launcher (378x171)
set "SCRIPT_DIR=%~dp0"
set "NATIVE_EXE=%SCRIPT_DIR%ChessRankWidget.exe"

if exist "%NATIVE_EXE%" (
    start "" "%NATIVE_EXE%"
    exit
)

set "HTML_PATH=%SCRIPT_DIR%index.html"
set "HTML_PATH=%HTML_PATH:\=/%"
set "EDGE_EXE=C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if not exist "%EDGE_EXE%" (
    set "EDGE_EXE=C:\Program Files\Microsoft\Edge\Application\msedge.exe"
)

if exist "%EDGE_EXE%" (
    start "" "%EDGE_EXE%" --app="file:///%HTML_PATH%" --window-size=394,196
) else (
    start "" "%SCRIPT_DIR%index.html"
)
exit
