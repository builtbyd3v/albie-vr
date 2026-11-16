# Release APK

- The app is **Albie**, package `com.albie.vr`, version 1.0.0 (version code 2). Raise the version code by 1 for every upload.
- Build: **Forge > Build > Release APK**, or the batch command below. Output: `unity/Builds/Albie-<version>-<code>.apk`.
- Signing comes from 4 env vars. Unset = debug key + a loud warning. The keystore and passwords never go in git.
- Testers side-load with `adb install`. Judges get it from the "Competition" release channel.
- `com.albie.vr` installs as a **new app**. Uninstall the old `com.UnityTechnologies.com.unity.template.urpblank` one; its saved data does not carry over.

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
