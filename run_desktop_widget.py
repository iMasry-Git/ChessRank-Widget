"""
ChessRank Widget - Standalone Python Desktop Runner (378px x 171px)
"""
import os
import sys
import subprocess
import webbrowser

def main():
    base_dir = os.path.dirname(os.path.abspath(__file__))

    # Priority 1: Native compiled C# Windows 11 executable
    native_exe = os.path.join(base_dir, "ChessRankWidget.exe")
    if os.path.exists(native_exe):
        subprocess.Popen([native_exe])
        return

    # Priority 2: Edge standalone app mode (394x196 window)
    html_file = os.path.join(base_dir, "index.html")
    edge_paths = [
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    ]

    edge_exe = None
    for p in edge_paths:
        if os.path.exists(p):
            edge_exe = p
            break

    if edge_exe:
        file_url = "file:///" + html_file.replace("\\", "/")
        args = [
            edge_exe,
            f"--app={file_url}",
            "--window-size=394,196",
            "--window-position=1100,80",
        ]
        subprocess.Popen(args)
    else:
        webbrowser.open("file:///" + html_file.replace("\\", "/"))

if __name__ == "__main__":
    main()
