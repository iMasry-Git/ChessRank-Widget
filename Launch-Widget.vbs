' ChessRank Widget - Silent Windows 11 Native Desktop Widget Runner
Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

strDir = fso.GetParentFolderName(WScript.ScriptFullName)
nativeExe = strDir & "\ChessRankWidget.exe"

If fso.FileExists(nativeExe) Then
    WshShell.Run """" & nativeExe & """", 0, False
    WScript.Quit
End If

strHtml = strDir & "\index.html"
strHtml = Replace(strHtml, "\", "/")

edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
If Not fso.FileExists(edgePath) Then
    edgePath = "C:\Program Files\Microsoft\Edge\Application\msedge.exe"
End If

If fso.FileExists(edgePath) Then
    WshShell.Run """" & edgePath & """ --app=""file:///" & strHtml & """ --window-size=394,196", 0, False
Else
    WshShell.Run "cmd /c start """" """ & strDir & "\index.html""", 0, False
End If
