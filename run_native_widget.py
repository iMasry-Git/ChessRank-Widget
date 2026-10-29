"""
ChessRank Widget - Native Windows 11 Desktop Widget Runner (378px x 171px)
Uses pywebview with Windows Edge WebView2 for a frameless,
transparent, curved, draggable desktop gadget experience.
"""
import os
import sys
import webview

def main():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    html_file = os.path.join(base_dir, "index.html")
    url = f"file:///{html_file.replace(os.sep, '/')}"

    # 90% scale size: 378px x 171px
    window = webview.create_window(
        title="ChessRank Widget",
        url=url,
        width=378,
        height=171,
        resizable=False,
        frameless=True,
        transparent=True,
        on_top=True,
        easy_drag=True
    )
    webview.start(gui='edgechromium')

if __name__ == "__main__":
    main()
