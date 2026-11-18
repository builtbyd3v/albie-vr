using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>One thing Albie says. Lines in a beat play in order, one at a time.</summary>
    public sealed class DemoLineDef
    {
        public string text = "";
        /// <summary>Clip name under Resources/DemoAudio/ (no extension). Missing clip: the caption plus 0.06 s per character.</summary>
        public string audio = "";
        /// <summary>Seconds after the beat's lead-in before this line may start (default: as soon as the line before it is done).</summary>
        public float at = -1f;
        /// <summary>Wait for this signal (DemoActions.Raise) first. After <see cref="timeout"/> s of the beat it starts anyway.</summary>
        public string after;
        /// <summary>Extra seconds after the signal (or after <see cref="at"/>).</summary>
        public float delay;
        public float timeout = 12f;
    }

    /// <summary>An action fired at a time, or when a signal comes (with a timeout, so a missing piece never stalls the run).</summary>
    public sealed class DemoCueDef
    {
        public float at;
        public string after;
        public float delay;
        public float timeout = 10f;
        public string action = "";
        /// <summary>String values like "slot:Left" become that DemoLayout slot's world position [x, y, z] when the cue fires.</summary>
        public JObject args;
    }

    public sealed class DemoBeatDef
    {
        public string id = "";
        public string title = "";
        /// <summary>"speech", "signal:name", "auto:seconds", "remote"; several joined with "|" (the first one that comes true wins).</summary>
        public string advance = "remote";
        /// <summary>Seconds into the beat before speech and signals count (remote Next always works).</summary>
        public float min;
        /// <summary>Seconds between the beat starting and its timeline (cues, lines) starting: Albie's "thinking" beat after Hudson speaks.</summary>
        public float lead;
        public List<DemoLineDef> lines = new List<DemoLineDef>();
        public List<DemoCueDef> cues = new List<DemoCueDef>();
    }

    public sealed class DemoScriptDef
    {
        public int version = 1;
        public List<DemoBeatDef> beats = new List<DemoBeatDef>();

        public int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < beats.Count; i++)
                if (string.Equals(beats[i].id, id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Resources/&lt;name&gt;.json; null (with an error in the console) when it is missing or broken.</summary>
        public static DemoScriptDef Load(string resource = "demo_script")
        {
            var asset = Resources.Load<TextAsset>(resource);
            if (asset == null)
            {
                Debug.LogError($"[Demo] Resources/{resource}.json not found.");
                return null;
            }
            try
            {
                var def = JsonConvert.DeserializeObject<DemoScriptDef>(asset.text);
                if (def == null || def.beats == null || def.beats.Count == 0)
                {
                    Debug.LogError($"[Demo] {resource}.json has no beats.");
                    return null;
                }
                return def;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Demo] {resource}.json is not valid: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>Resources/demo_config.json. Off by default, so judges never see the scripted Director.</summary>
    public sealed class DemoConfig
    {
        public bool enabled;
        /// <summary>0 = wait for Next (laptop remote or the ] key). Above 0: B0 starts by itself this many seconds after launch.</summary>
        public float autoStartSeconds;
        /// <summary>Playback pitch of Albie's recorded lines. Matches StreamedPcmTextToSpeech's cartoon pitch so recorded and live Albie sound alike.</summary>
        public float voicePitch = 1.2f;
        /// <summary>Cancel the live agent's reply to Hudson's speech while the Director runs.</summary>
        public bool muteLiveAgent = true;
        /// <summary>Wait per character for a line with no audio clip.</summary>
        public float secondsPerChar = 0.06f;
        public float lineGapSeconds = 0.35f;
        /// <summary>A final transcript with fewer words than this does not advance a beat (a cough, "um").</summary>
        public int minSpeechWords = 2;
        /// <summary>B1: no palm after this many seconds (0 = wait forever): Albie lands in front of Hudson anyway.</summary>
        public float palmTimeoutSeconds = 0f;
        /// <summary>Keep the microphone open hands-free while a beat waits for Hudson's speech.</summary>
        public bool keepMicOpen = true;
        /// <summary>A on the right controller plays Hudson's next move (start, palm, each of his lines, Add, Confirm, palm again),
        /// B goes back a beat. While on, his speech never moves a beat on (A is the only trigger, so the two can't double up) and
        /// the live voice is switched off, because A is push-to-talk outside the demo.</summary>
        public bool stepButton = true;

        public const string Resource = "demo_config";

        public static DemoConfig Load()
        {
            var asset = Resources.Load<TextAsset>(Resource);
            if (asset == null) return new DemoConfig();
            try { return JsonConvert.DeserializeObject<DemoConfig>(asset.text) ?? new DemoConfig(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Demo] demo_config.json is not valid ({e.Message}); demo mode stays off.");
                return new DemoConfig();
            }
        }
    }
}
