# Windows private preview

Target platform: Windows 11 x64. Extract the ZIP to a permanent folder, run `TalkPasswordFill.exe`, save your own KakaoTalk password, and leave **Launch at Windows sign-in** enabled. Click **Log In** yourself when KakaoTalk shows the filled field.

## Verify on Windows

1. Restart Windows and sign in. Open KakaoTalk if needed. Confirm the helper starts and fills its first login window.
2. Sign into KakaoTalk, log out, and confirm that the new login window fills again.
3. Confirm the helper leaves existing text alone and pauses from the tray menu.

The preview was cross-built on macOS. The live KakaoTalk tests are pending. The executable is unsigned and may show a SmartScreen prompt. The password is encrypted at rest with per-user DPAPI.

Build with the .NET 10 SDK from the repository root.

```powershell
dotnet publish windows/TalkPasswordFill.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/windows-x64
```
