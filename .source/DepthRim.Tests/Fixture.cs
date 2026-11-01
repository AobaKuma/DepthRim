using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

// 真實的 DepthRim 類別與 1.7 參考組件；不啟動遊戲或渲染迴圈。結構比照 DMSE.BVRTests。
static partial class Test
{
    static int count;
    static string section;
    static readonly List<string> failures = new List<string>();

    // 一個檢查失敗只記錄下來，區段繼續跑，避免一個過時的預期值蓋掉其他結果。
    static bool Check(bool condition, string name)
    {
        if (!condition) { failures.Add(section + ": " + name); return false; }
        count++;
        return true;
    }

    // 執行一個區段；例外只讓該區段失敗。
    static void Run(string name, Action test)
    {
        section = name;
        try { test(); }
        catch (Exception e) { failures.Add(name + ": threw " + e.GetType().Name + ": " + e.Message); }
    }

    static void Near(float actual, float expected, string name, float tolerance = 0.001f)
        => Check(Math.Abs(actual - expected) <= tolerance, name + $" ({actual} != {expected})");

    /// <summary>模組根目錄（含 About/About.xml）：由這個原始檔的位置往上找，與執行時的工作目錄、輸出資料夾無關。</summary>
    static string ModRoot => modRoot ??= FindModRoot();
    static string modRoot;

    static string FindModRoot([CallerFilePath] string thisFile = "")
    {
        for (DirectoryInfo dir = new FileInfo(thisFile).Directory; dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "About", "About.xml"))) { return dir.FullName; }
        }
        throw new Exception("Mod root (About/About.xml) not found above " + thisFile);
    }
}
