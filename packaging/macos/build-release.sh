#!/bin/bash

set -euo pipefail

PYTHON="${PYTHON:-python3}"
OUTPUT_DIRECTORY="${OUTPUT_DIRECTORY:-artifacts}"
SKIP_DEPENDENCY_INSTALL=false
PHASE="all"

while [ "$#" -gt 0 ]; do
    case "$1" in
        --python)
            PYTHON="$2"
            shift 2
            ;;
        --output-directory)
            OUTPUT_DIRECTORY="$2"
            shift 2
            ;;
        --skip-dependency-install)
            SKIP_DEPENDENCY_INSTALL=true
            shift
            ;;
        --phase)
            PHASE="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

case "$PHASE" in
    all|executable|archive) ;;
    *)
        echo "Invalid phase: $PHASE" >&2
        exit 2
        ;;
esac

REPO_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
MACHINE="$(uname -m)"
case "$MACHINE" in
    arm64) ARCH="arm64" ;;
    x86_64) ARCH="x64" ;;
    *)
        echo "Unsupported macOS architecture: $MACHINE" >&2
        exit 1
        ;;
esac

BUILD_DIRECTORY="$REPO_DIR/build/macos-$ARCH"
DIST_DIRECTORY="$REPO_DIR/dist/macos-$ARCH"
SPEC_DIRECTORY="$BUILD_DIRECTORY/spec"
PACKAGE_NAME="STS2-TUI-macOS-$ARCH"
STAGE_DIRECTORY="$BUILD_DIRECTORY/$PACKAGE_NAME"
OUTPUT_DIRECTORY="$REPO_DIR/$OUTPUT_DIRECTORY"
ZIP_PATH="$OUTPUT_DIRECTORY/$PACKAGE_NAME.zip"

build_executable() {
    rm -rf "$BUILD_DIRECTORY" "$DIST_DIRECTORY"
    mkdir -p "$SPEC_DIRECTORY" "$DIST_DIRECTORY"
    export PYINSTALLER_CONFIG_DIR="$BUILD_DIRECTORY/cache"

    if [ "$SKIP_DEPENDENCY_INSTALL" = false ]; then
        "$PYTHON" -m pip install --disable-pip-version-check \
            -r "$REPO_DIR/packaging/macos/requirements-build.txt"
    fi

    "$PYTHON" -m PyInstaller \
        --noconfirm \
        --clean \
        --onefile \
        --console \
        --name "STS2-TUI" \
        --paths "$REPO_DIR/python" \
        --hidden-import "tui" \
        --hidden-import "curses" \
        --workpath "$BUILD_DIRECTORY/pyinstaller" \
        --specpath "$SPEC_DIRECTORY" \
        --distpath "$DIST_DIRECTORY" \
        "$REPO_DIR/python/play.py"

    codesign --force --deep --sign - "$DIST_DIRECTORY/STS2-TUI"
}

build_archive() {
    if [ ! -x "$DIST_DIRECTORY/STS2-TUI" ]; then
        echo "Packaged executable was not found: $DIST_DIRECTORY/STS2-TUI" >&2
        exit 1
    fi

    rm -rf "$STAGE_DIRECTORY"
    mkdir -p "$STAGE_DIRECTORY" "$OUTPUT_DIRECTORY" "$STAGE_DIRECTORY/src"

    cp "$DIST_DIRECTORY/STS2-TUI" "$STAGE_DIRECTORY/"
    cp "$REPO_DIR/STS2-TUI.command" "$STAGE_DIRECTORY/"
    cp "$REPO_DIR/setup.sh" "$STAGE_DIRECTORY/"
    cp "$REPO_DIR/README.md" "$REPO_DIR/LICENSE" "$STAGE_DIRECTORY/"
    cp -R "$REPO_DIR/localization_eng" "$REPO_DIR/localization_zhs" "$STAGE_DIRECTORY/"
    rsync -a --exclude bin --exclude obj "$REPO_DIR/src/" "$STAGE_DIRECTORY/src/"
    chmod +x "$STAGE_DIRECTORY/STS2-TUI" "$STAGE_DIRECTORY/STS2-TUI.command" "$STAGE_DIRECTORY/setup.sh"

    rm -f "$ZIP_PATH"
    ditto -c -k --sequesterRsrc --keepParent "$STAGE_DIRECTORY" "$ZIP_PATH"
    echo "Created $ZIP_PATH"
}

if [ "$PHASE" = "all" ] || [ "$PHASE" = "executable" ]; then
    build_executable
fi

if [ "$PHASE" = "all" ] || [ "$PHASE" = "archive" ]; then
    build_archive
fi
