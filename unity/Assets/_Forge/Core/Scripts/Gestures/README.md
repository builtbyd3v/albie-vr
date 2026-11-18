# Hand gestures

Point at a window and flick to move it. Hold your palm out and Albie sits in it. Show Albie a new gesture three times and it's saved. Research behind it: `docs/HAND_GESTURES_RESEARCH.md`.

It installs itself in any scene with a `Workspace`, so there's nothing to set up.

## Built-in gestures

| Gesture | Default action |
|---|---|
| Point at a window, flick toward you | The window comes to about arm's length |
| Point at a window, flick away | A window that came to you goes back; otherwise it minimizes into a dot (tap the dot to reopen it) |
| Hand out flat, palm up, held for 0.6 s | Albie jumps onto your palm, sits and listens |
| Left hand: open it, then close it | Hides every window |
| Left hand: make a fist, then burst it open | Shows them all again |
| Right hand: look at a window, open your hand, close it | That window minimizes into a dot |
| Right hand: make a fist, then burst it open | The last window closed comes back |

The pointing flicks are the right hand's too. The Hands setting changes this: in Settings (page 3), the **Hands** pill on the gesture display, or by asking Albie (`gesture_hands`). The choices are *Left hand: all windows* (the default), *Right hand: all windows* (swapped), or *Either hand: one window* (nothing hides everything; both hands close, open and flick one window).

A close has to start from an open hand, not a resting or pointing one, and an open or close can't start out of a point: the pose has to be made afresh after the hand stops pointing. After any gesture fires, the other gestures on that hand wait half a second. That's what keeps curling your finger after a flick from also closing or reopening a window.

Closing the hand Albie sits in sends him off instead. Albie also hops off if you turn your hand over or it leaves tracking. The mic opens when he lands and again after each answer. One silence closes it until you call him again, so this never leaves a paid voice session listening to the room.

## Teaching a gesture

Say something like "Albie, when I do this, open CAD", or press **Teach a gesture** on the display. A countdown appears above your hand; show the gesture when it says "Show me now", three times. It works out whether you showed a held shape, a shape and a move, or one shape changing into another. If it can't use what it saw, it says why (the takes disagreed, it looks like a resting hand, it clashes with another gesture). If it can, the gesture is saved and live.

## Trying it without a headset

Press Play in `Main` with **Forge > Desktop Mode** on, then press E at the desk. You get a pretend right hand that aims where the crosshair aims:

| Key | Does |
|---|---|
| 1 2 3 4 | Relaxed, point, fist, open (the pretend hand is a right hand: set Hands to *Right hand: all windows* to try hiding everything) |
| 5 | Palm up |
| 6 | Peace sign |
| Q / Y | Flick toward you / away |

Only the keys are pretend. Everything after the joints is the real code.

## Tests

- **Forge > Gestures > Run Self-Test**: gesture logic against synthetic hands, at 72 and 30 Hz.
- **Forge > Gestures > Run Play Test**: the real `Main` scene with the simulated hand. Screenshots go to `unity/Logs/gestures/`. Voice stays off, so it costs nothing.

Neither can tell you how real hands feel: thresholds still need tuning on a headset.

## How it works

Everything is one data shape, `GestureDef`, for built-ins and taught gestures alike: hold a start pose, then optionally move and/or reach an end pose. No C# per gesture.

- `HandSample`: joints to 18 size-independent numbers (finger curl, knuckle bend, spread, palm direction).
- `GestureHand`: one hand over time, including motion in the head's frame, mirrored so either hand matches.
- `GestureMatcher`: the state machine for one gesture on one hand.
- `GestureEngine`: runs it all each frame and finds the window you're aiming at.
- `GestureRecorder`: three takes to a `GestureDef`.
- `GestureLibrary`: the built-ins, and the save file.
- `GestureActions`: what a gesture can do. `GestureWindow` and `GesturePerch` are the window and Albie behavior.
- `GestureTools`: Albie's voice tools, defined in `Resources/AlbieTools/gestures.json`.
- `GesturePanel`, `GestureFeedback`: the display, the aim beam and the text above your hand.
- `IsdkHandSource`, `SimulatedHandSource`: real hands and the pretend one.

Pinching and dragging panels stay with the Interaction SDK. Gestures hold off while a panel is being dragged.

## Safety

Binding any of Albie's tools to a gesture is allowed, with limits, because a gesture is easy to fire by accident:

- Tools in `GestureActions.RunsDirectly` (opening views, timers, reading) run straight away. Add one only if it loses nothing and reaches no one.
- Any other tool shows Albie's Yes/No card first.
- `messages_confirm` can't be bound at all: sending stays a deliberate tap.

## Saved data

`Application.persistentDataPath/gestures.json` holds taught gestures and any rebinding of the built-ins:

```json
{
  "version": 2,
  "hands": "LeftAll",
  "bindings": { "close_one": { "action": "window.close" } },
  "gestures": [
    {
      "id": "peace_sign", "title": "peace sign", "how": "Your hand shape, held",
      "start": { "index_curl": [0, 0.27], "ring_curl": [0.68, 1] },
      "hold": 0.3, "window": 0.4,
      "action": "tool:ui_open_view", "args": { "view": "cad" }
    }
  ]
}
```
