using System;
using System.IO;

namespace Forge.Controllers
{
    /// <summary>
    /// Watches one controller file and hot-reloads it into a host when it changes on disk (a save from the code panel, an
    /// edit in another editor, a git checkout). It polls the file's write time, because FileSystemWatcher isn't
    /// reliable on Android. Call <see cref="Poll"/> from Update. The code panel can also skip the file and call
    /// <see cref="ControllerHost.Reload(string)"/> directly. Writers should write a temp file and replace the target, so a
    /// half-written file is never read; a file that is refused is retried at its next change.
    /// </summary>
    public sealed class ControllerFileWatcher
    {
        public readonly string Path;
        readonly ControllerHost host;
        DateTime lastWrite;
        double nextPoll;

        /// <summary>Seconds between checks of the file's write time.</summary>
        public double Interval = 0.5;
        /// <summary>The last reload's result: null for success, else the reason it was refused.</summary>
        public string LastError { get; private set; }
        public int Reloads { get; private set; }

        /// <summary>Starts watching. Changes from now on are reloaded; the file as it is now is not.</summary>
        public ControllerFileWatcher(ControllerHost host, string path)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            lastWrite = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        /// <summary>Checks the file at most every <see cref="Interval"/> seconds. Returns true when it tried a reload.</summary>
        public bool Poll(double now)
        {
            if (now < nextPoll) return false;
            nextPoll = now + Interval;
            if (!File.Exists(Path)) return false;
            var write = File.GetLastWriteTimeUtc(Path);
            if (write == lastWrite) return false;
            lastWrite = write;
            string text;
            try { text = File.ReadAllText(Path); }
            catch (IOException e) { LastError = "bad_value: couldn't read the file (" + e.Message + ")"; return true; }
            LastError = host.Reload(text);
            if (LastError == null) Reloads++;
            return true;
        }

        /// <summary>Writes <paramref name="json"/> to <paramref name="path"/> through a temp file, so a watcher never reads half of it.</summary>
        public static void WriteAtomic(string path, string json)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
