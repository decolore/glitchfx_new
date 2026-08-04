#!/bin/bash
set -e
cd "$(dirname "$0")"
source .venv/bin/activate
python -m PyInstaller --clean -y --windowed --onedir --name "Glitch FX" app_appkit.py
echo "Built: dist/Glitch FX.app"
