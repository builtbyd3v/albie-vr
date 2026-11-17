#!/usr/bin/env bash
# Record the headset screen and its Unity log while a command runs (default: the feature sweep).
#
#   bash tools/quest-record.sh                          # feature sweep, everything
#   bash tools/quest-record.sh --group lifecycle        # feature sweep, one group (setup/teardown always run)
#   bash tools/quest-record.sh --only "first run" --paid
#   bash tools/quest-record.sh --out /tmp/rec -- python3 tools/questbridge.py sweep   # any other command
#
# Output: unity/Logs/quest-record-YYYYMMDD-HHMMSS/ with screen-NN.mp4 (one per 3 minute segment), unity.log, sweep/ (report).
# Reads only: never installs or uninstalls, never changes headset settings. Removes its own videos from the headset.
# Env: ADB (adb path), QR_SIZE (default 1280x720), QR_BITRATE (default 8000000 bits/s).
# Exit: the command's exit code; 2 no headset, 3 more than one device, 4 bad arguments.
#
# Flags checked against developer.android.com/tools/adb (screenrecord: --size WxH, --bit-rate in bits/s,
# --time-limit seconds, default and max 180) and developer.android.com/tools/logcat (tag:priority filters, *:S, -v threadtime).
# UNVERIFIED (not on those pages): logcat -T 1 (start at the newest line; from logcat --help on Android 5+), and toybox
# pkill -INT -f on the Quest (used to stop screenrecord so it finishes the mp4). QR_SIZE 1280x720 is the docs' fallback
# size; the Quest's native size may be refused by its encoder, which is why it is not the default.
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY_ADB=/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb
SIZE="${QR_SIZE:-1280x720}"
BITRATE="${QR_BITRATE:-8000000}"
SEGMENT_S=180  # screenrecord's maximum; segments are chained
OUT=""
SWEEP_ARGS=()
CMD=()

while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="${2:?--out needs a folder}"; shift 2 ;;
    --group|--only) SWEEP_ARGS+=("$1" "${2:?$1 needs a value}"); shift 2 ;;
    --paid) SWEEP_ARGS+=("--paid"); shift ;;
    --) shift; CMD=("$@"); break ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1 (see --help)"; exit 4 ;;
  esac
done

# adb: $ADB, else PATH, else Unity's bundled one.
if [ -n "${ADB:-}" ]; then :
elif command -v adb >/dev/null 2>&1; then ADB="$(command -v adb)"
else ADB="$UNITY_ADB"
fi
if [ ! -x "$ADB" ]; then echo "no adb found (set ADB, put adb on PATH, or install Unity's Android module)"; exit 2; fi

# Exactly one device in the "device" state.
DEVICES="$("$ADB" devices | awk 'NR > 1 && $2 == "device" { print $1 }')"
COUNT=$(printf '%s' "$DEVICES" | grep -c . || true)
if [ "$COUNT" -eq 0 ]; then
  echo "no headset found. Plug it in over USB, put it on once and allow USB debugging. ('$ADB devices' lists none ready.)"
  exit 2
fi
if [ "$COUNT" -gt 1 ]; then echo "more than one device attached; unplug all but the headset:"; echo "$DEVICES"; exit 3; fi
SERIAL="$DEVICES"
adbs() { "$ADB" -s "$SERIAL" "$@"; }

STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="${OUT:-$ROOT/unity/Logs/quest-record-$STAMP}"
mkdir -p "$OUT"
REMOTE="/sdcard/albie-rec-$STAMP"
STOP="$OUT/.stop"
[ ${#CMD[@]} -eq 0 ] && CMD=(python3 "$ROOT/tools/questbridge.py" sweep "$ROOT/tools/questbridge_feature_cases.json" --out "$OUT/sweep" ${SWEEP_ARGS[@]+"${SWEEP_ARGS[@]}"})

# Screen: back-to-back segments until $STOP exists (about a second is lost between two segments).
record() {
  n=1
  while [ ! -e "$STOP" ]; do
    adbs shell screenrecord --size "$SIZE" --bit-rate "$BITRATE" --time-limit "$SEGMENT_S" "$REMOTE-$(printf %02d $n).mp4" >/dev/null 2>&1
    n=$((n + 1))
  done
}
record &
REC_PID=$!

# Log: the Unity tag (and Java crashes) from now on, with timestamps to line up with the video.
adbs logcat -v threadtime -T 1 Unity:V AndroidRuntime:E '*:S' > "$OUT/unity.log" 2>&1 &
LOG_PID=$!

finish() {
  trap - EXIT INT TERM
  touch "$STOP"
  # SIGINT lets screenrecord finish the mp4 it is writing; only ours (the -f match is on our file name).
  adbs shell "pkill -INT -f $REMOTE || kill -INT \$(pidof screenrecord) 2>/dev/null" >/dev/null 2>&1
  wait "$REC_PID" 2>/dev/null
  kill "$LOG_PID" 2>/dev/null; wait "$LOG_PID" 2>/dev/null
  sleep 1
  for f in $(adbs shell "ls $REMOTE-*.mp4 2>/dev/null" | tr -d '\r'); do
    adbs pull "$f" "$OUT/screen-${f##*-}" >/dev/null 2>&1 || echo "could not pull $f"
  done
  adbs shell "rm -f $REMOTE-*.mp4" >/dev/null 2>&1
  rm -f "$STOP"
  echo
  echo "Recording: $OUT"
  for f in "$OUT"/screen-*.mp4; do [ -e "$f" ] && echo "  $f"; done
  echo "  $OUT/unity.log"
  [ -e "$OUT/sweep/report.md" ] && echo "  $OUT/sweep/report.md"
}
trap 'finish; exit 130' INT TERM
trap finish EXIT

echo "Recording $SERIAL into $OUT. Running: ${CMD[*]}"
sleep 1  # let the first segment start before the command does anything
"${CMD[@]}"
CODE=$?
finish
exit $CODE
