#!/bin/sh
# Put the standalone build on the Quest over USB and run it: install, grant permissions, launch, then record the
# headset's logs until Ctrl+C (reconnects if the cable or adb drops). After launch the cable can come out: the app
# needs no laptop. Usage: sh quest-run.sh [path/to/app.apk]
set -e
ADB=/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb
APK=${1:-unity/Builds/AlbieStandalone.apk}
PKG=com.albie.vr
LOG=unity/Logs/quest-$(date +%m%d-%H%M).log

"$ADB" start-server >/dev/null
if ! "$ADB" devices | grep -qw device; then
    echo "No headset found. Plug it in, put it on, and tap Allow on 'Allow USB debugging?'. Then run this again."
    exit 1
fi
echo "Installing $APK ..."
"$ADB" install -r "$APK"
# Ask once here instead of a pop-up in the headset (mic for Albie, room scan for the desk).
"$ADB" shell pm grant $PKG android.permission.RECORD_AUDIO || true
"$ADB" shell pm grant $PKG com.oculus.permission.USE_SCENE || true
"$ADB" shell am force-stop $PKG
"$ADB" shell am start -n $PKG/com.unity3d.player.UnityPlayerActivity >/dev/null
echo "Albie is running. Logs: $LOG (Ctrl+C stops recording; the app keeps running)."
"$ADB" logcat -c || true
while true; do
    "$ADB" wait-for-device
    "$ADB" logcat -v time >> "$LOG" || true
    sleep 2
done
