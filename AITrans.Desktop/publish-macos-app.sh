#!/bin/zsh
set -euo pipefail

SCRIPT_DIR=${0:A:h}
PROJECT_FILE="$SCRIPT_DIR/AITrans.Desktop.csproj"
CONFIGURATION="${1:-Release}"
RUNTIME_IDENTIFIER="osx-arm64"
FRAMEWORK="net9.0"
APP_DISPLAY_NAME="AI Trans"
APP_BUNDLE_NAME="AITrans.app"
APP_EXECUTABLE="AITrans.Desktop"
APP_BUNDLE_IDENTIFIER="com.aitrans.desktop"
APP_VERSION="1.0"
PUBLISH_DIR="$SCRIPT_DIR/bin/$CONFIGURATION/$FRAMEWORK/$RUNTIME_IDENTIFIER/publish"
APP_DIR="$SCRIPT_DIR/bin/$CONFIGURATION/$FRAMEWORK/$RUNTIME_IDENTIFIER/$APP_BUNDLE_NAME"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"
ICON_SOURCE="$SCRIPT_DIR/../AITrans/Assets/translation_logo2.icns"

echo "Publishing self-contained macOS app for $RUNTIME_IDENTIFIER..."
dotnet publish "$PROJECT_FILE" \
  -c "$CONFIGURATION" \
  -r "$RUNTIME_IDENTIFIER" \
  --self-contained true \
  -p:UseAppHost=true

rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

cp -R "$PUBLISH_DIR/." "$MACOS_DIR/"

if [[ -f "$ICON_SOURCE" ]]; then
  cp "$ICON_SOURCE" "$RESOURCES_DIR/"
fi

cat > "$CONTENTS_DIR/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_DISPLAY_NAME</string>
    <key>CFBundleExecutable</key>
    <string>$APP_EXECUTABLE</string>
    <key>CFBundleIconFile</key>
    <string>translation_logo2.icns</string>
    <key>CFBundleIdentifier</key>
    <string>$APP_BUNDLE_IDENTIFIER</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>AITrans</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$APP_VERSION</string>
    <key>CFBundleVersion</key>
    <string>$APP_VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

chmod +x "$MACOS_DIR/$APP_EXECUTABLE"

codesign --force --deep --sign - "$APP_DIR"

echo
echo "App bundle created:"
echo "$APP_DIR"