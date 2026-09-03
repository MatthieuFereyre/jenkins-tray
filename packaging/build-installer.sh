#!/usr/bin/env bash
#
# Publishes Jenkins Tray for Windows and packages it as an installer — entirely on Linux.
#
# WPF itself cross-compiles: the Windows targeting packs come from NuGet, EnableWindowsTargeting
# fetches them. NSIS compiles a Windows installer on Linux too, so nothing here needs Windows.
#
# Usage: packaging/build-installer.sh <version> [output-dir]
set -euo pipefail

VERSION="${1:?usage: build-installer.sh <version> [output-dir]}"
OUT_DIR="${2:-artifacts}"

PROJECT="src/JenkinsTray/JenkinsTray.csproj"
PUBLISH_DIR="artifacts/publish"
ICON="src/JenkinsTray/Assets/app.ico"
SETUP_NAME="JenkinsTray-Setup-${VERSION}.exe"

cd "$(dirname "$0")/.."
mkdir -p "$OUT_DIR"
# Absolute from here on: the output directory is relative on a developer machine and absolute in
# the image, and makensis is handed the path it must write to.
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
SETUP="$OUT_DIR/$SETUP_NAME"

echo ">>> Publishing $VERSION"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" \
    --configuration Release \
    --runtime win-x64 \
    --self-contained false \
    -p:EnableWindowsTargeting=true \
    -p:Version="$VERSION" \
    -p:PublishDir="$PWD/$PUBLISH_DIR/"

FILE_COUNT="$(find "$PUBLISH_DIR" -type f | wc -l)"

echo ">>> Packaging $SETUP_NAME from $FILE_COUNT files"
# -V4 so the log names every file it takes in: nothing can be read back out of the installer once
# it is written — a solid LZMA block hides even the file names — so that log is the only account of
# what went in, and the check below reads it.
makensis -V4 \
    "-DVERSION=$VERSION" \
    "-DSOURCE_DIR=$PWD/$PUBLISH_DIR" \
    "-DICON=$PWD/$ICON" \
    "-DOUT_FILE=$SETUP" \
    packaging/JenkinsTray.nsi \
    | tee "$OUT_DIR/makensis.log"

# The installer is only ever tested by running it, which no Linux agent can do — so check here what
# can be checked, as the MSI packaging did before it.
echo ">>> Checking the installer"
fail() {
    echo "    FAIL $1" >&2
    exit 1
}

[ -s "$SETUP" ] || fail "$SETUP_NAME was not produced"

# The version resource is what tells the built installer apart from the previous one, and it is the
# only field of the .nsi that a typo would leave silently wrong.
setup_version="$(perl packaging/pe-version.pl "$SETUP")"
if [ "$setup_version" = "$VERSION.0" ]; then
    echo "    ok   version resource: $setup_version"
else
    fail "version resource is '$setup_version', expected '$VERSION.0'"
fi

# Counted, not sampled: a file quietly missing from the package is exactly the failure the MSI used
# to ship, and on disk the only sign would be a slightly smaller installer nobody looks at. The
# plugin the script pulls in is excluded — its line carries an arrow, the published files do not.
packed="$(grep -cE '^File: "[^"]+" [0-9]+ bytes$' "$OUT_DIR/makensis.log" || true)"
if [ "$packed" -eq "$FILE_COUNT" ]; then
    echo "    ok   files packed: $packed, one per published file"
else
    fail "$packed files packed, $FILE_COUNT were published"
fi

# JenkinsTray.exe is the one file whose absence would produce an installer that runs, installs, and
# leaves nothing to start.
grep -qE '^File: "JenkinsTray\.exe" ' "$OUT_DIR/makensis.log" \
    || fail "JenkinsTray.exe is not in the package"
echo "    ok   JenkinsTray.exe is in the package"

rm -f "$OUT_DIR/makensis.log"
ls -l "$SETUP"
