#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
source_icon="$project_dir/CDSI.Agent.Mac/Assets/logo.png"
asset_catalog="$project_dir/Resources/Assets.xcassets"
app_icon_set="$asset_catalog/AppIcon.appiconset"
contents_json="$app_icon_set/Contents.json"
compiled_assets="$project_dir/CDSI.Agent.Mac/Assets/Assets.car"
fallback_icon="$project_dir/CDSI.Agent.Mac/Assets/Beacon.icns"
renderer="$project_dir/scripts/render-app-icons.swift"
checksum_manifest="$project_dir/Resources/AppIcon.sha256"
mode=generate

case "${1:-}" in
  "") ;;
  --check) mode=check ;;
  *)
    echo "Usage: $0 [--check]" >&2
    exit 2
    ;;
esac

fail()
{
  echo "Icon validation failed: $*" >&2
  exit 1
}

require_command()
{
  command -v "$1" >/dev/null 2>&1 || fail "required command is unavailable: $1"
}

icon_manifest()
{
  printf '%s\n' \
    '16 icon_16x16.png' \
    '32 icon_16x16@2x.png' \
    '32 icon_32x32.png' \
    '64 icon_32x32@2x.png' \
    '128 icon_128x128.png' \
    '256 icon_128x128@2x.png' \
    '256 icon_256x256.png' \
    '512 icon_256x256@2x.png' \
    '512 icon_512x512.png' \
    '1024 icon_512x512@2x.png'
}

read_dimension()
{
  property=$1
  image_path=$2
  sips -g "$property" "$image_path" 2>/dev/null |
    awk -v label="$property:" '$1 == label { print $2; exit }'
}

verify_png()
{
  expected_size=$1
  image_path=$2
  [ -f "$image_path" ] || fail "missing image: $image_path"
  actual_width=$(read_dimension pixelWidth "$image_path")
  actual_height=$(read_dimension pixelHeight "$image_path")
  [ "$actual_width" = "$expected_size" ] ||
    fail "$(basename "$image_path") width is $actual_width, expected $expected_size"
  [ "$actual_height" = "$expected_size" ] ||
    fail "$(basename "$image_path") height is $actual_height, expected $expected_size"
}

require_command sips
require_command iconutil
require_command assetutil
require_command plutil
require_command shasum

[ -f "$source_icon" ] || fail "missing source icon: $source_icon"
source_width=$(read_dimension pixelWidth "$source_icon")
source_height=$(read_dimension pixelHeight "$source_icon")
[ "$source_width" = "$source_height" ] || fail "logo.png must be square"
[ "$source_width" -ge 1024 ] || fail "logo.png must be at least 1024x1024"
plutil -convert xml1 -o /dev/null "$contents_json"

temp_dir=$(mktemp -d "${TMPDIR:-/tmp}/beacon-icons.XXXXXX")
cleanup()
{
  status=$?
  trap - EXIT HUP INT TERM
  rm -rf "$temp_dir"
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

if [ "$mode" = generate ]; then
  require_command xcrun
  [ -f "$renderer" ] || fail "missing icon renderer: $renderer"
  SWIFT_MODULECACHE_PATH="$temp_dir/swift-module-cache" \
    CLANG_MODULE_CACHE_PATH="$temp_dir/clang-module-cache" \
    xcrun swift "$renderer" "$source_icon" "$app_icon_set"

  compiled_dir="$temp_dir/compiled"
  mkdir -p "$compiled_dir"
  xcrun actool \
    --compile "$compiled_dir" \
    --platform macosx \
    --minimum-deployment-target 10.12 \
    --app-icon AppIcon \
    --output-partial-info-plist "$temp_dir/asset-info.plist" \
    "$asset_catalog" >/dev/null
  [ -f "$compiled_dir/Assets.car" ] || fail "actool did not produce Assets.car"
  [ -f "$compiled_dir/AppIcon.icns" ] || fail "actool did not produce AppIcon.icns"
  cp "$compiled_dir/Assets.car" "$compiled_assets"
  cp "$compiled_dir/AppIcon.icns" "$fallback_icon"
fi

icon_manifest | while IFS=' ' read -r pixel_size filename; do
  verify_png "$pixel_size" "$app_icon_set/$filename"
done

[ -f "$compiled_assets" ] || fail "missing compiled asset catalog: $compiled_assets"
asset_info="$temp_dir/assets.json"
assetutil --info "$compiled_assets" > "$asset_info"
icon_manifest | while IFS=' ' read -r _ filename; do
  grep -F "\"RenditionName\" : \"$filename\"" "$asset_info" >/dev/null ||
    fail "Assets.car does not contain $filename"
done

[ -f "$fallback_icon" ] || fail "missing fallback icon: $fallback_icon"
verified_iconset="$temp_dir/Verified.iconset"
if ! iconutil --convert iconset "$fallback_icon" --output "$verified_iconset"; then
  fail "iconutil could not inspect $fallback_icon"
fi
icon_manifest | while IFS=' ' read -r pixel_size filename; do
  verify_png "$pixel_size" "$verified_iconset/$filename"
done

if [ "$mode" = generate ]; then
  (
    cd "$project_dir"
    shasum -a 256 \
      scripts/render-app-icons.swift \
      CDSI.Agent.Mac/Assets/logo.png \
      Resources/Assets.xcassets/AppIcon.appiconset/Contents.json \
      Resources/Assets.xcassets/AppIcon.appiconset/*.png \
      CDSI.Agent.Mac/Assets/Assets.car \
      CDSI.Agent.Mac/Assets/Beacon.icns
  ) > "$temp_dir/AppIcon.sha256"
  cp "$temp_dir/AppIcon.sha256" "$checksum_manifest"
fi

[ -f "$checksum_manifest" ] || fail "missing checksum manifest: $checksum_manifest"
if ! (cd "$project_dir" && shasum -a 256 --check "$checksum_manifest" >/dev/null); then
  fail "icon sources and generated assets do not match $checksum_manifest"
fi

echo "Icon resources are valid."
