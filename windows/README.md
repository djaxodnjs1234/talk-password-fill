# Windows preview

Target platform: Windows 11 x64. Quit any older copy of the helper, extract the entire ZIP to a permanent folder, and run `TalkPasswordFill.exe`. To keep an existing saved password, leave the password box blank and click **Save settings**. Select **Launch at Windows sign-in** to start the helper automatically.

Launching the executable directly opens settings. When KakaoTalk displays a supported login window, the helper fills a password field it can confirm is empty. If the helper asks you to clear the password field, do so and click **Fill now** in settings. Click **Log In** yourself after checking the field.

## Verify on Windows

1. Restart Windows and sign in. Open KakaoTalk if needed. Confirm the helper starts and fills its first login window.
2. Sign into KakaoTalk, log out, and confirm that the new login window fills again.
3. Confirm the helper leaves existing text alone and pauses from the tray menu.

Automated checks on Windows verified startup, reopening settings, and filling the first login window of official KakaoTalk with a test password. KakaoTalk may display the masked characters only after you click the password field. Validation on a Windows 11 PC after reboot and after signing out of a KakaoTalk account is pending.

The executable is unsigned and may show a SmartScreen prompt. The password is encrypted at rest with per-user DPAPI.

Build with the .NET 10 SDK from the repository root.

```powershell
dotnet publish windows/TalkPasswordFill.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist/windows-x64
```
