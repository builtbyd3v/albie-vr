using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Forge > Check All: runs every Forge/.../Check ... menu item whose class has a <c>public static string Run()</c> that the
/// menu item calls (the repo's check shape: a report whose first line starts PASS or FAIL) and prints one table, then the
/// full report of each failing check. Menu items of another shape (Play-mode starters, scene builders, void runners) are
/// listed as "menu": run those from the menu. Batch: -executeMethod ForgeChecks.RunAll exits 1 on any FAIL.
/// </summary>
public static class ForgeChecks
{
    [MenuItem("Forge/Check All")]
    static void Menu()
    {
        string report = Report(out bool failed);
        if (failed) Debug.LogError(report); else Debug.Log(report);
    }

    /// <summary>For -executeMethod: exit code 1 on any FAIL (batch mode only).</summary>
    public static void RunAll()
    {
        string report = Report(out bool failed);
        Debug.Log(report);
        if (Application.isBatchMode) EditorApplication.Exit(failed ? 1 : 0);
    }

    struct Row
    {
        public string menu, status, first, full;
    }

    static string Report(out bool anyFailed)
    {
        var rows = new List<Row>();
        var ran = new HashSet<MethodInfo>();
        foreach (var method in TypeCache.GetMethodsWithAttribute<MenuItem>())
        {
            if (method.DeclaringType == typeof(ForgeChecks)) continue;
            foreach (var item in method.GetCustomAttributes<MenuItem>(false))
            {
                string menu = item.menuItem;
                if (item.validate || !menu.StartsWith("Forge/") || !menu.Substring(menu.LastIndexOf('/') + 1).StartsWith("Check ")) continue;
                var run = method.DeclaringType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (run == null || run.ReturnType != typeof(string) || !Calls(method, run) || !ran.Add(run))
                {
                    rows.Add(new Row { menu = menu, status = "menu", first = "run it from the menu" });
                    continue;
                }
                string report;
                try { report = (string)run.Invoke(null, null) ?? ""; }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    report = $"FAIL threw {inner.GetType().Name}: {inner.Message}";
                }
                string first = report.Split('\n')[0];
                string status = first.StartsWith("PASS") ? "PASS" : first.StartsWith("FAIL") ? "FAIL" : "?";
                rows.Add(new Row { menu = menu, status = status, first = first, full = report });
            }
        }
        rows.Sort((a, b) => string.CompareOrdinal(a.menu, b.menu));

        int failed = 0, byMenu = 0, width = 0;
        foreach (var r in rows)
        {
            if (r.status == "FAIL") failed++;
            if (r.status == "menu") byMenu++;
            width = Math.Max(width, r.menu.Length);
        }
        anyFailed = failed > 0;
        var sb = new StringBuilder();
        sb.Append(failed == 0 ? "PASS" : $"FAIL ({failed})").Append($" Forge checks ({(Application.isPlaying ? "Play" : "Edit")} mode): ")
          .Append($"{rows.Count - byMenu} run, {failed} failed, {byMenu} run by menu\n");
        foreach (var r in rows) sb.Append("  ").Append(r.status.PadRight(5)).Append(r.menu.PadRight(width + 2)).Append(r.first).Append('\n');
        foreach (var r in rows)
            if (r.status == "FAIL") sb.Append("\n--- ").Append(r.menu).Append('\n').Append(r.full).Append('\n');
        return sb.ToString();
    }

    // Does the menu method call Run()? (one class can have other menu items, e.g. a Play-mode loader next to its check)
    static bool Calls(MethodInfo caller, MethodInfo callee)
    {
        if (caller == callee) return true;
        byte[] il = caller.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return false;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28) continue;  // call
            try
            {
                if (caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == callee) return true;
            }
            catch (Exception) { }  // a 0x28 byte that is an operand, not an opcode
        }
        return false;
    }
}
