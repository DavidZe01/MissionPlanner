#!/usr/bin/env bash
# Sign "Mission Planner.app" with a Developer ID certificate (hardened runtime), have Apple
# notarize it and staple the ticket, so that it opens from Finder without the quarantine
# warning. Needs an Apple Developer Program account; macos-native.yml runs it only when
# the repository has these secrets:
#   MACOS_CERTIFICATE           base64 of a .p12 with the "Developer ID Application" certificate
#   MACOS_CERTIFICATE_PASSWORD  its password
#   APPLE_ID, APPLE_TEAM_ID     the account's Apple ID and team
#   APPLE_APP_PASSWORD          an app-specific password of that Apple ID
# Usage: sign-notarize.sh <app> <zip to write>
set -euo pipefail
app=${1:?app}
zip=${2:?zip}
native_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
entitlements="$native_dir/Entitlements.plist"
work=${RUNNER_TEMP:-$(mktemp -d)}

keychain="$work/signing.keychain-db"
keychain_password=$(uuidgen)
security create-keychain -p "$keychain_password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
printf '%s' "$MACOS_CERTIFICATE" | base64 --decode > "$work/certificate.p12"
security import "$work/certificate.p12" -P "$MACOS_CERTIFICATE_PASSWORD" -A -t cert -f pkcs12 -k "$keychain"
rm -f -- "$work/certificate.p12"
security set-key-partition-list -S apple-tool:,apple: -k "$keychain_password" "$keychain" > /dev/null
# shellcheck disable=SC2046
security list-keychains -d user -s "$keychain" $(security list-keychains -d user | tr -d '"')
identity=$(security find-identity -v -p codesigning "$keychain" | awk -F'"' '/Developer ID Application/ {print $2; exit}')
[[ -n $identity ]] || { echo "no Developer ID Application identity in the certificate" >&2; exit 1; }
echo "Signing with: $identity"

sign() { codesign --force --timestamp --options runtime --sign "$identity" "$@"; }
main_exe="$app/Contents/MacOS/$(defaults read "$(cd -- "$app" && pwd)/Contents/Info" CFBundleExecutable)"

# every Mach-O inside first (libraries, GStreamer plugins, the simulators), then the app
while IFS= read -r -d '' file; do
    [[ $file == "$main_exe" ]] && continue
    if file -b "$file" | grep -q 'Mach-O'; then
        if file -b "$file" | grep -q 'executable'; then
            sign --entitlements "$entitlements" "$file"
        else
            sign "$file"
        fi
    fi
done < <(find "$app/Contents" -type f -print0)
sign --entitlements "$entitlements" "$app"
codesign --verify --strict --deep "$app"
spctl --assess --type execute -vv "$app" || true

rm -f -- "$zip"
ditto -c -k --keepParent "$app" "$zip"
xcrun notarytool submit "$zip" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" \
    --password "$APPLE_APP_PASSWORD" --wait --timeout 25m
xcrun stapler staple "$app"
xcrun stapler validate "$app"
rm -f -- "$zip"
ditto -c -k --keepParent "$app" "$zip"
security delete-keychain "$keychain" || true
echo "Signed, notarized and stapled: $zip"
