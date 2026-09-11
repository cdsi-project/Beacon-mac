#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
dotnet_bin=${DOTNET_BIN:-dotnet}
configuration=${CONFIGURATION:-Release}
target_arch=${BEACON_ARCH:-arm64}

if [ "$target_arch" != arm64 ]; then
  echo "Unsupported target architecture: $target_arch. CDSI Beacon supports Apple Silicon (arm64) only." >&2
  exit 1
fi

runtime_id=osx-arm64
expected_arch=arm64

version=$(tr -d '[:space:]' < "$project_dir/VERSION")
output_root="$project_dir/build"
publish_dir="$output_root/publish/$runtime_id"
app_dir="$output_root/$runtime_id/CDSI Beacon.app"
contents_dir="$app_dir/Contents"

"$project_dir/scripts/generate-icons.sh" --check

rm -rf "$publish_dir" "$app_dir"

"$dotnet_bin" publish \
  "$project_dir/CDSI.Agent.Mac/CDSI.Agent.Mac.csproj" \
  -c "$configuration" \
  -r "$runtime_id" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$publish_dir"

mkdir -p "$contents_dir/MacOS" "$contents_dir/Resources/Legal"
cp "$publish_dir/CDSI-Beacon" "$contents_dir/MacOS/CDSI-Beacon"
find "$publish_dir" -maxdepth 1 -type f \( -name '*.dylib' -o -name '*.json' \) \
  -exec cp '{}' "$contents_dir/MacOS/" \;
cp "$project_dir/CDSI.Agent.Mac/Assets/Beacon.icns" "$contents_dir/Resources/Beacon.icns"
cp "$project_dir/CDSI.Agent.Mac/Assets/Assets.car" "$contents_dir/Resources/Assets.car"
cp "$project_dir/README.md" "$contents_dir/Resources/Legal/README.md"
cp "$project_dir/LICENSE" "$contents_dir/Resources/Legal/LICENSE"
cp "$project_dir/NOTICE" "$contents_dir/Resources/Legal/NOTICE"
cp "$project_dir/THIRD-PARTY-NOTICES.md" "$contents_dir/Resources/Legal/THIRD-PARTY-NOTICES.md"
find "$project_dir/Legal" -maxdepth 1 -type f \
  -exec cp '{}' "$contents_dir/Resources/Legal/" \;
sed "s/@VERSION@/$version/g" \
  "$project_dir/CDSI.Agent.Mac/Info.plist" > "$contents_dir/Info.plist"
printf 'APPL????' > "$contents_dir/PkgInfo"
chmod 0755 "$contents_dir/MacOS/CDSI-Beacon"
plutil -lint "$contents_dir/Info.plist"
actual_arch=$(lipo -archs "$contents_dir/MacOS/CDSI-Beacon")
if [ "$actual_arch" != "$expected_arch" ]; then
  echo "Unexpected application architecture: $actual_arch (expected $expected_arch)." >&2
  exit 1
fi

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$app_dir"
  codesign --verify --deep --strict --verbose=2 "$app_dir"
fi

echo "$app_dir"
