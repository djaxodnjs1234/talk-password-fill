# Talk Password Fill

A private preview that fills the password field in the official KakaoTalk desktop app. Click **Log In** yourself.

<p>
  <a href="https://github.com/djaxodnjs1234/talk-password-fill/releases/download/v0.1.0-preview/TalkPasswordFill-macOS-Preview.zip"><img src="macos-download.png" width="184" alt="Download macOS preview"></a>
  <a href="https://github.com/djaxodnjs1234/talk-password-fill/releases/download/v0.2.0-preview/TalkPasswordFill-Windows-x64-Preview.zip"><img src="windows-download.png" width="184" alt="Download Windows x64 preview"></a>
</p>

## macOS

1. Download and unzip the preview. Move **Talk Password Fill** to **Applications** and open it.
2. Save your KakaoTalk password and enable **Talk Password Fill** under **System Settings → Privacy & Security → Accessibility**.
3. Open KakaoTalk and click **Log In** when the password appears.

Requires macOS 13 or later on Apple silicon or Intel. The ad hoc preview can show a first-launch warning. [Apple app-opening guide](https://support.apple.com/102445)

## Windows

1. Download and extract the ZIP to a permanent folder, such as `%LOCALAPPDATA%\Programs\TalkPasswordFill`.
2. Run **TalkPasswordFill.exe**, save your password, and leave **Launch at Windows sign-in** enabled.
3. Open KakaoTalk and click **Log In** when the password appears.

For Windows 11 x64. This is an unsigned preview. Automatic filling after Windows sign-in and after KakaoTalk logout still needs on-device validation. [Windows setup and test checklist](windows/README.md)

## Security and privacy

Passwords are encrypted at rest by macOS Keychain or Windows DPAPI for the current user. The helper verifies the KakaoTalk process and fills a supported empty password field. It uses the password in memory for autofill and keeps it out of the clipboard, logs, and network requests. Each person enters their own password. The preview stores one password per OS user and does not match the account ID in the login window.

The previews are available to people with access to this private repository. Windows source lives in `windows/`. Mac source and build instructions are in the [GitHub Kit](https://github.com/djaxodnjs1234/talk-password-fill/releases/download/v0.1.0-preview/TalkPasswordFill-GitHub-Kit.zip).

Talk Password Fill is an independent utility. KakaoTalk is a product of Kakao Corp.
