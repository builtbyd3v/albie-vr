# Headset tests

- One command runs the feature sweep on a plugged-in headset and records its screen and Unity log.
- The default run spends nothing: the voice is held, and the 3 paid voice cases run only with `--paid`.
- Each case asserts a tool result, the app's state, or its log lines, through the debug bridge (development builds only).
- 3 manual cases need a person in the headset; they are listed in the report.
- Output lands in `unity/Logs/quest-record-<time>/`.

## Run it

Install and launch a Development Build (`sh quest-run.sh`), put the headset on the table, then:

```
python3 tools/questbridge.py wake        # keep it awake unworn
bash tools/quest-record.sh               # add --group lifecycle, --only "first run", or --paid
python3 tools/questbridge.py rest        # undo wake
```

Needs: the HF Space paused (the cloud cases expect it offline) and a room scan with a table (the desk cases seat you at it).

## What each group proves

| Group | Proves |
|---|---|
| identity | The build reports `com.albie.vr` 1.0.0. An old template id is a WARN, not a FAIL. |
| voice | No voice session is open at idle. Paid: `say` opens one, it closes about 35 s after the last word, it stays closed for 60 s. |
| desk | With the user seated, cad, code and ml open (these refused with "nobody is at the desk"). |
| first run | `help_show` opens the welcome card; its 3 tries name registered tools; the taps answer as designed (approval, heart, "make a slalom first"); the card reopens from the dot. |
| lifecycle | Headset off stops game time and audio; back on restores them, Resumed fires once, and only an absence over 20 s gets "Welcome back". |
| cloud offline | With the Space paused, CAD and ML tools answer the offline line within 10 s, and a headset artifact still opens. |
| messages | The demo messenger opens chat and Mentions; a draft sends exactly once; provider arguments, `im:` ids and `imessage` items never show up. |

## Manual cases

1. Headset off for 2 minutes, then on. Albie says welcome back. Log: `[AppLifecycle] away (`, `[AlbieVoice] suspended`, `[AppLifecycle] back (... ) after 1xx s`.
2. Face Albie and talk (costs voice credits). Log: `[AlbieVoice] connected ...`, then `[AlbieVoice] idle: closing the voice session` about 35 s after his last word.
3. Pinch-drive a lap of the slalom. Then `python3 tools/questbridge.py call brain_status` shows `completed_laps` of 1 or more.
4. With the hub running on the laptop, stand at the ML desk and say "Albie, fine-tune distilbert on movie reviews".
   The desk unfolds when the project passes its checks, the first run downloads the model (tell the person it may
   take a minute), then the curves stream and the score is spoken at the end. Say "Albie, start an RNN project on
   movie reviews" for the from-scratch path. Log: `ml_new`, then `forge.check` passing and the run's epochs.

## Where things land

- `unity/Logs/quest-record-<time>/screen-NN.mp4`: one video per 3 minutes (screenrecord's limit), about a second lost between files.
- `.../unity.log`: the Unity log for the same window, with timestamps.
- `.../sweep/report.md` and `results.json`: pass, fail, skip and warn per case, with a screenshot each.
- Without recording: `python3 tools/questbridge.py sweep tools/questbridge_feature_cases.json` (report in `unity/Logs/questbridge-*/`).
