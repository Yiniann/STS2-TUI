#!/bin/bash

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR" || exit 1

if [ -x "$SCRIPT_DIR/STS2-TUI" ]; then
    exec "$SCRIPT_DIR/STS2-TUI" "$@"
fi

if command -v python3 >/dev/null 2>&1 && [ -f "$SCRIPT_DIR/python/play.py" ]; then
    exec python3 "$SCRIPT_DIR/python/play.py" "$@"
fi

echo "STS2-TUI executable was not found."
echo "Download the macOS release, or install Python 3 for source development."
read -r -p "Press Enter to close..."
exit 1
