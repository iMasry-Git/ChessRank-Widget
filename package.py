import os
import json
import zipfile

def package():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    manifest_path = os.path.join(base_dir, "manifest.json")
    
    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)
    
    version = manifest.get("version", "2.2.0")
    desktop_dir = r"C:\Users\Ibrahim\Desktop"
    
    zip_names = [
        f"ChessRank-Widget-v{version}.zip",
        f"chessrank-widget-v{version}.zip"
    ]
    
    include_files = [
        "ChessRankWidget.exe",
        "ChessRankWidget.cs",
        "build.bat",
        "manifest.json",
        "index.html",
        "widget.css",
        "widget.js",
        "background.js",
        "Launch-Widget.bat",
        "Launch-Widget.vbs",
        "run_native_widget.py",
        "run_desktop_widget.py",
        "README.md"
    ]
    
    include_dirs = [
        "assets",
        "icons"
    ]
    
    for zip_name in zip_names:
        zip_path = os.path.join(desktop_dir, zip_name)
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zipf:
            for f in include_files:
                f_path = os.path.join(base_dir, f)
                if os.path.exists(f_path):
                    zipf.write(f_path, arcname=f)
            
            for d in include_dirs:
                d_path = os.path.join(base_dir, d)
                if os.path.exists(d_path):
                    for root, _, files in os.walk(d_path):
                        for file in files:
                            full_path = os.path.join(root, file)
                            rel_path = os.path.relpath(full_path, base_dir)
                            zipf.write(full_path, arcname=rel_path)
                            
        print(f"Created portable zip: {zip_path} ({os.path.getsize(zip_path)} bytes)")

if __name__ == "__main__":
    package()
