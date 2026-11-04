using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.Notebook
{
    /// <summary>
    /// The notebook's disk side (C-17 section 7). Writes go to one background queue, in order, and each one writes a
    /// temp file and then replaces the target, so a crash or a pulled battery loses at most the write in flight and
    /// never leaves half a file. Reads parse with dates left as strings, so timestamps come back exactly as written.
    /// </summary>
    public static class NotebookFiles
    {
        static readonly Queue<(string path, string text)> queue = new Queue<(string, string)>();
        static readonly object gate = new object();
        static bool draining;

        /// <summary>The last write that failed, or null. Read it on the main thread to report it.</summary>
        public static string LastError { get; private set; }
        public static int Written { get; private set; }

        /// <summary>Queues <paramref name="text"/> for <paramref name="path"/>. Returns at once; the write happens on a worker thread.</summary>
        public static void Write(string path, string text)
        {
            lock (gate)
            {
                queue.Enqueue((path, text));
                if (draining) return;
                draining = true;
            }
            ThreadPool.QueueUserWorkItem(Drain);
        }

        /// <summary>Waits until every queued write is on disk (or <paramref name="timeoutMs"/> passes). Returns false on a timeout.</summary>
        public static bool Flush(int timeoutMs = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (gate)
            {
                while (draining || queue.Count > 0)
                {
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0 || !Monitor.Wait(gate, left)) return false;
                }
            }
            return true;
        }

        static void Drain(object _)
        {
            while (true)
            {
                (string path, string text) item;
                lock (gate)
                {
                    if (queue.Count == 0)
                    {
                        draining = false;
                        Monitor.PulseAll(gate);
                        return;
                    }
                    item = queue.Dequeue();
                }
                try
                {
                    WriteAtomic(item.path, item.text);
                    Written++;
                }
                catch (Exception e)
                {
                    LastError = $"couldn't write {item.path}: {e.Message}";
                    UnityEngine.Debug.LogWarning("[Notebook] " + LastError);
                }
            }
        }

        /// <summary>Writes through a temp file next to the target, then replaces it. Runs on the calling thread.</summary>
        public static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        /// <summary>Parses JSON with ISO dates kept as plain strings (Json.NET would otherwise turn them into DateTimes).</summary>
        public static JToken Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader);
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("extra content after the JSON value");
                return token;
            }
        }

        /// <summary>Moves a file that won't load into <c>damaged/</c> beside it, never overwriting an earlier one. Returns the new path.</summary>
        public static string MoveToDamaged(string path)
        {
            string folder = Path.Combine(Path.GetDirectoryName(path), "damaged");
            Directory.CreateDirectory(folder);
            string name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            string target = Path.Combine(folder, name + ext);
            for (int i = 1; File.Exists(target); i++) target = Path.Combine(folder, $"{name}-{i}{ext}");
            File.Move(path, target);
            return target;
        }
    }
}
