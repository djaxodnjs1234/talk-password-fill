# macOS

Swift menu bar helper for macOS 13 or later. The source builds for Apple silicon and Intel.

Run from the repository root with Xcode 16 or newer.

```bash
bash macos/build.sh --local
```

Open the app at the printed path. Use macOS Keychain and enable Accessibility for the helper. `--release` builds a Developer ID-signed and notarized ZIP when `MACOS_SIGNING_IDENTITY` and `MACOS_NOTARY_PROFILE` are configured.

The local build and release use separate Keychain entries.

Bring the KakaoTalk login window to the front. The helper focuses its empty password field before filling it so the text editor can report the edit. Click **Log In** in KakaoTalk yourself.

The helper checks for the login window when KakaoTalk launches, comes forward, or reports a window change. While KakaoTalk is running, a 12-second timer covers missed window notifications. The timer stops when KakaoTalk quits or autofill is paused.

Run the secure-field regression test on a Mac with an active desktop session. Bring the **Test Login** window forward if it opens behind another app.

```bash
swiftc -swift-version 6 macos/Autofill.swift macos/PasswordStore.swift macos/tests/FocusedPasswordEdit.swift -o /tmp/talk-fill-focused-test
/tmp/talk-fill-focused-test
```
