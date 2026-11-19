# macOS

Swift menu bar helper for macOS 13 or later. The source builds for Apple silicon and Intel.

Run from the repository root with Xcode 16 or newer.

```bash
bash macos/build.sh --local
```

Open the app at the printed path. Use macOS Keychain and enable Accessibility for the helper. `--release` builds a Developer ID-signed and notarized ZIP when `MACOS_SIGNING_IDENTITY` and `MACOS_NOTARY_PROFILE` are configured.

The local build and release use separate Keychain entries.
