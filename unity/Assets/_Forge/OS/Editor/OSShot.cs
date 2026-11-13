using System.IO;
using Forge.Pet;
using UnityEngine;

namespace Forge.OS.Editor
{
    /// <summary>Screenshots of what the user's head camera sees, for the OS play tests and by-hand checks. Written to unity/Logs/os/ (not in git).</summary>
    public static class OSShot
    {
        public const string Dir = "Logs/os";

        /// <summary>Renders the head camera to Logs/os/&lt;name&gt;.png. Returns the path, or null when there is no camera or no graphics.</summary>
        public static string Take(string name, int width = 1280, int height = 800)
        {
            Texture2D tex = Capture(width, height);
            if (tex == null) return null;
            Directory.CreateDirectory(Dir);
            string path = Path.GetFullPath(Path.Combine(Dir, name + ".png"));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return path;
        }

        /// <summary>Renders the head camera into a new RGB24 texture (the caller destroys it), or null when there is no camera or no graphics.</summary>
        public static Texture2D Capture(int width = 1280, int height = 800)
        {
            Camera cam = PetUser.HeadCamera;
            if (cam == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var previous = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = previous;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            return tex;
        }
    }
}
