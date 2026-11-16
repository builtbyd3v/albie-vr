using Forge.Pet;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// Brings crew members home and sends them out again, for the Agents window and anything else in the OS. A member that is
    /// brought home keeps its task, its bridge and its hub state (<see cref="AlbieCrewMember.SetHome"/> only stops it
    /// wandering). At a desk it is given a pad: a spot on the desk just past the crew egg, toward the middle of the desk,
    /// in a small block of slots so every home agent has its own. Away from a desk, or when the desk has no free spot, it
    /// gets no spot and hides its body instead. Nothing here is ever deleted or dismissed: dismissing is a different
    /// action (<c>dismiss_agent</c>) that the window asks for behind a confirm.
    /// </summary>
    public static class OSAgentHome
    {
        // Pad slots, in metres, measured from the egg toward the desk's middle and sideways along it. Three across, then the next row.
        const float FirstDistance = 0.16f, RowStep = 0.11f, ColumnStep = 0.11f;
        const float EdgeMargin = 0.07f;   // pads stay this far inside the desk edge
        const float SlotClear = 0.07f;    // two home spots are at least this far apart
        const float EggInset = 0.09f;     // the egg's own corner inset (AlbieHatchery.cornerInset), used when the egg isn't placed yet
        const int MaxSlots = 15;
        static readonly int[] ColumnOrder = { 0, 1, -1 };

        static AlbieHatchery hatchery;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => hatchery = null;

        /// <summary>
        /// A free home spot on the desk for <paramref name="self"/> (its own current spot does not count as taken). False when
        /// the user is not at a desk, the desk surface is not built, or every slot is blocked.
        /// </summary>
        public static bool TryPadSpot(AlbieCrewMember self, out Vector3 spot)
        {
            spot = default;
            OSShell shell = OSShell.Current;
            if (shell == null || !shell.AtDesk) return false;
            AlbieNavSurface desk = AlbieNavSurface.Desk;
            if (desk == null || !desk.IsBuilt) return false;

            if (hatchery == null) hatchery = UnityEngine.Object.FindAnyObjectByType<AlbieHatchery>();
            Vector3 egg;
            if (hatchery != null && hatchery.Placed) egg = hatchery.transform.position;
            else
            {
                Transform head = OSShell.FindHead();
                if (head == null) return false;
                if (!DeskCorner.TryPose(desk.TopCenter, desk.Rotation, desk.Size, head.position, EggInset, true, 0f, out egg, out Quaternion unused)) return false;
            }

            Vector3 toMiddle = desk.TopCenter - egg;
            toMiddle.y = 0f;
            if (toMiddle.sqrMagnitude < 0.0001f) toMiddle = desk.Rotation * Vector3.forward;
            toMiddle.Normalize();
            Vector3 along = Vector3.Cross(Vector3.up, toMiddle);

            for (int k = 0; k < MaxSlots; k++)
            {
                int row = k / 3;
                int column = ColumnOrder[k % 3];
                Vector3 p = egg + toMiddle * (FirstDistance + RowStep * row) + along * (ColumnStep * column);
                p = DeskCorner.ClampInside(desk.TopCenter, desk.Rotation, desk.Size, p, EdgeMargin);
                p.y = desk.TopCenter.y;
                if (desk.IsInsideHole(p, out Bounds hole)) continue;  // the laptop / table button keep-out
                if (Taken(p, self)) continue;
                spot = p;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Brings a member home. At a desk it stands on a pad (true); otherwise its body is hidden and it keeps working out of
        /// sight (false). Its task is not touched either way. Does nothing for a member that is leaving.
        /// </summary>
        public static bool BringHome(AlbieCrewMember member)
        {
            if (member == null || member.Leaving) return false;
            bool onPad = TryPadSpot(member, out Vector3 spot);
            member.SetHome(true, onPad ? spot : (Vector3?)null);
            if (onPad) OSHomePad.ShowFor(member);
            Debug.Log("[OS] " + member.PetId + " is home" + (onPad ? " on its pad" : ", out of sight") + ".");
            return onPad;
        }

        /// <summary>Sends a member out again: its body is back on and it wanders as before.</summary>
        public static void SendOut(AlbieCrewMember member)
        {
            if (member == null || member.Leaving) return;
            member.SetHome(false);
            Debug.Log("[OS] " + member.PetId + " is out again.");
        }

        /// <summary>Brings every crew member that is out home. Returns how many were brought.</summary>
        public static int BringAllHome()
        {
            AlbieCrew crew = AlbieCrew.Instance;
            if (crew == null) return 0;
            int n = 0;
            for (int i = 0; i < crew.Members.Count; i++)
            {
                AlbieCrewMember m = crew.Members[i];
                if (m == null || m.Leaving || m.Home) continue;
                BringHome(m);
                n++;
            }
            return n;
        }

        /// <summary>Sends every crew member that is home out again. Returns how many were sent.</summary>
        public static int SendAllOut()
        {
            AlbieCrew crew = AlbieCrew.Instance;
            if (crew == null) return 0;
            int n = 0;
            for (int i = 0; i < crew.Members.Count; i++)
            {
                AlbieCrewMember m = crew.Members[i];
                if (m == null || m.Leaving || !m.Home) continue;
                SendOut(m);
                n++;
            }
            return n;
        }

        /// <summary>How many crew members are home.</summary>
        public static int HomeCount()
        {
            AlbieCrew crew = AlbieCrew.Instance;
            if (crew == null) return 0;
            int n = 0;
            for (int i = 0; i < crew.Members.Count; i++)
                if (crew.Members[i] != null && crew.Members[i].Home) n++;
            return n;
        }

        // Another home member already stands within SlotClear of this point.
        static bool Taken(Vector3 p, AlbieCrewMember self)
        {
            AlbieCrew crew = AlbieCrew.Instance;
            if (crew == null) return false;
            for (int i = 0; i < crew.Members.Count; i++)
            {
                AlbieCrewMember other = crew.Members[i];
                if (other == null || other == self || !other.Home || !other.HomeSpot.HasValue) continue;
                Vector3 d = other.HomeSpot.Value - p;
                d.y = 0f;
                if (d.sqrMagnitude < SlotClear * SlotClear) return true;
            }
            return false;
        }
    }
}
