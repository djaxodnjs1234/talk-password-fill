#!/bin/bash
set -euo pipefail

die() {
    printf '%s\n' "$*" >&2
    exit 1
}

mode="${1:---local}"
[[ $# -le 1 && ( "$mode" == --local || "$mode" == --release ) ]] || die "Usage: $0 [--local|--release]"
[[ "$(uname -s)" == Darwin ]] || die 'Build the macOS app on a Mac.'

signing_identity=-
signing_options=()
if [[ "$mode" == --release ]]; then
    signing_identity="${MACOS_SIGNING_IDENTITY:-}"
    [[ "$signing_identity" == 'Developer ID Application: '* ]] || die 'Set MACOS_SIGNING_IDENTITY to a Developer ID Application identity.'
    [[ -n "${MACOS_NOTARY_PROFILE:-}" ]] || die 'Set MACOS_NOTARY_PROFILE to a notarytool Keychain profile.'
    identities="$(security find-identity -v -p codesigning)"
    [[ "$identities" == *"\"$signing_identity\""* ]] || die 'The Developer ID identity and its private key must be available in Keychain.'
    signing_options=(--timestamp)
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
dist="$root/dist"
mkdir -p "$dist"
stage="$(mktemp -d "$dist/.macos.XXXXXXXX")"
trap 'rm -rf "$stage"' EXIT

app_name='Talk Password Fill.app'
app="$stage/$app_name"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$root/macos/Info.plist" "$app/Contents/Info.plist"
cp "$root/macos/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
if [[ "$mode" == --local ]]; then
    /usr/libexec/PlistBuddy -c 'Set :CFBundleIdentifier com.local.talk-password-fill.preview' "$app/Contents/Info.plist"
fi

for architecture in arm64 x86_64; do
    xcrun swiftc -swift-version 6 -target "$architecture-apple-macosx13.0" -O \
        "$root"/macos/*.swift -o "$stage/$architecture"
done

binary="$app/Contents/MacOS/TalkPasswordFill"
xcrun lipo -create "$stage/arm64" "$stage/x86_64" -output "$binary"
architectures="$(xcrun lipo -archs "$binary")"
[[ " $architectures " == *' arm64 '* && " $architectures " == *' x86_64 '* ]] || die 'Expected a Universal 2 app.'
chmod 755 "$binary"
if [[ "$mode" == --release ]]; then
    codesign --force --options runtime --sign "$signing_identity" "${signing_options[@]}" "$app"
else
    codesign --force --options runtime --sign - "$app"
fi
codesign --verify --deep --strict --all-architectures "$app"

if [[ "$mode" == --local ]]; then
    local_dir="$(mktemp -d "$dist/local.XXXXXXXX")"
    mv "$app" "$local_dir/"
    printf 'Local app: %s\n' "$local_dir/$app_name"
    exit 0
fi

staging_zip="$stage/notarization.zip"
submission="$stage/submission.json"
ditto -c -k --sequesterRsrc --keepParent "$app" "$staging_zip"
submit_status=0
xcrun notarytool submit "$staging_zip" --keychain-profile "$MACOS_NOTARY_PROFILE" \
    --wait --output-format json > "$submission" || submit_status=$?
cat "$submission"

submission_id="$(plutil -extract id raw -o - "$submission" 2>/dev/null || true)"
if [[ "$submission_id" =~ ^[0-9a-fA-F-]{36}$ ]] &&
    ! xcrun notarytool log "$submission_id" --keychain-profile "$MACOS_NOTARY_PROFILE" \
        "$dist/notary-$submission_id.json"; then
    printf 'Could not fetch the log for submission %s.\n' "$submission_id" >&2
fi
[[ "$submit_status" == 0 ]] || die 'Notarization submission failed.'
status="$(plutil -extract status raw -o - "$submission")"
[[ "$status" == Accepted ]] || die "Apple notarization status: $status"

xcrun stapler staple "$app"
asset_name=TalkPasswordFill-macOS.zip
ditto -c -k --sequesterRsrc --keepParent "$app" "$stage/$asset_name"
ditto -x -k "$stage/$asset_name" "$stage/check"
checked_app="$stage/check/$app_name"
codesign --verify --deep --strict --all-architectures "$checked_app"
xcrun stapler validate "$checked_app"
mv -f "$stage/$asset_name" "$dist/$asset_name"
printf 'Notarized release asset: %s\n' "$dist/$asset_name"
