# Release APK

- The app is **Albie**, package `com.albie.vr`, version 1.0.0 (version code 2). Raise the version code by 1 for every upload.
- Build: **Forge > Build > Release APK**, or the batch command below. Output: `unity/Builds/Albie-<version>-<code>.apk`.
- Signing comes from 4 env vars. Unset = debug key + a loud warning. The keystore and passwords never go in git.
- Testers side-load with `adb install`. Judges get it from the "Competition" release channel.
- `com.albie.vr` installs as a **new app**. Uninstall the old `com.UnityTechnologies.com.unity.template.urpblank` one; its saved data does not carry over.

## Build machine requirements
- The release build is `QuestBuild.Release` (`unity/Assets/_Forge/Core/Editor/QuestBuild.cs`): `Main.unity` only, IL2CPP ARM64, not a development build, LZ4-compressed (`BuildOptions.CompressWithLz4`). It refuses to build without IL2CPP and ARM64.
- Two git-ignored files must exist on the build machine, or the APK builds but ships broken:
  - `unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt`: the cloud app key (the value of `ALBIE_APP_KEY` in Vercel and the Space). `AlbieVoiceToken.cs`, `ForgeCloud.cs` and `ClaudeClient.cs` read it. A missing or placeholder key makes voice and cloud calls return 401.
  - The gator model: `unity/Assets/_Forge/Pet/Models/Gator_Model.fbx` plus `unity/Assets/_Forge/Pet/ThirdParty/Quirky Series/` (paid Asset Store pack, [SETUP.md](SETUP.md) step 9). Without them Albie has no body.
- Signing: the keystore path comes from `ALBIE_KEYSTORE_PATH` (convention: `~/.albie/albie-release.keystore`), plus `ALBIE_KEY_ALIAS` and the two password env vars. A path to a missing file stops the build; any unset var gives a debug-signed APK.

### Pre-upload checklist
1. Both git-ignored files above are present; the gator shows in the Editor.
2. The version code is 1 higher than the last upload.
3. The build log says `signing: release key (albie)`, and `apksigner` shows the release certificate.
4. On a headset, with no controller and no laptop: Albie appears, the voice opens and answers, and a CAD or ML tool reaches the cloud.
5. The APK goes to the channel named exactly **Competition**.

## 1. Make the release key (owner, once)
```sh
/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/OpenJDK/bin/keytool -genkeypair -v \
  -keystore ~/.albie/albie-release.keystore -alias albie -keyalg RSA -keysize 2048 -validity 10000
```
Keep the file outside the repo (`~/.albie/`) and the passwords in a password manager, with a backup. Android only accepts an update signed with the same key, so a lost key means a new package id.

## 2. Build
```sh
export ALBIE_KEYSTORE_PATH=~/.albie/albie-release.keystore ALBIE_KEY_ALIAS=albie
read -rs ALBIE_KEYSTORE_PASS && read -rs ALBIE_KEY_ALIAS_PASS && export ALBIE_KEYSTORE_PASS ALBIE_KEY_ALIAS_PASS
/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath unity \
  -buildTarget Android -executeMethod QuestBuild.Release -logFile unity/Logs/release-build.log
grep "\[QuestBuild\]" unity/Logs/release-build.log   # size, signing mode, package and version
```
Close the Editor first (a batch build fails while the project is open). The menu item reads the env vars of the Editor process, so start Unity Hub from that shell to sign from the menu. Check the signature: `.../SDK/build-tools/36.0.0/apksigner verify --print-certs <apk>`.

## 3. Side-load for testers
`adb install -r unity/Builds/Albie-1.0.0-2.apk`, or `sh quest-run.sh unity/Builds/Albie-1.0.0-2.apk` (also grants mic and room permissions, launches, and logs).

## 4. Owner does this by hand (Meta developer dashboard, not verified here)
1. Create the app (free) in the dashboard and note its App ID.
2. Create a release channel named exactly **Competition** (competition rule, PROJECT_CONTEXT 0.4).
3. Upload the signed APK to that channel: dashboard web upload, Meta Quest Developer Hub, or `ovr-platform-util upload-quest-build --app-id <id> --app-secret <secret> --apk <apk> --channel <channel>` (flags from the SDK's `OVRPlatformTool.cs`; whether `<channel>` is the name or an id is unverified).
4. Add testers to the channel by email. Unverified: the exact invite flow and whether testers need a Meta account with that email.
5. Unverified: the dashboard's own checks (store icon and cover art, privacy policy URL, data-use form) for a release channel build.
