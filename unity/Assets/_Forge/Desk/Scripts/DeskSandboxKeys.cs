using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// DeskSandbox only: what Albie would do, on the number keys (hub live or off). 1 a 25 second tea timer, 2 an hour of
/// focus, 3 a note, 4 the notes open / closed, 5 the notes to a dot, 0 every timer off.
/// </summary>
public class DeskSandboxKeys : MonoBehaviour
{
    static readonly string[] Notes =
    {
        "Ask Adrian about Albie's jump timing", "Push the demo build by 3", "Re-record the fallback ML run",
        "Try the timer dot in the simulator",
    };

    int next;
    UiWidgets widgets = new UiWidgets();

    void OnEnable()
    {
        ForgeEvents.OnUiWidgets += OnWidgets;
    }

    void OnDisable()
    {
        ForgeEvents.OnUiWidgets -= OnWidgets;
    }

    void OnWidgets(UiWidgets value) => widgets = value ?? new UiWidgets();

    void Update()
    {
        var k = Keyboard.current;
        if (k == null) return;
        if (k.digit1Key.wasPressedThisFrame) DeskApi.TimerStart(25f, "tea");
        if (k.digit2Key.wasPressedThisFrame) DeskApi.TimerStart(3600f, "");
        if (k.digit3Key.wasPressedThisFrame) DeskApi.NoteAdd(Notes[next++ % Notes.Length]);
        if (k.digit4Key.wasPressedThisFrame) DeskApi.SetWidget("notes", widgets.notes == "closed" ? "open" : "closed");
        if (k.digit5Key.wasPressedThisFrame) DeskApi.SetWidget("notes", "minimized");
        if (k.digit0Key.wasPressedThisFrame) DeskApi.SetWidget("timer", "closed");
    }
}
