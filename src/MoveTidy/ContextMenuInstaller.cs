using Microsoft.Win32;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>
/// 把「移動整理」子選單寫進目前使用者的登錄檔（HKCU，不需要系統管理員權限）。
/// 檔案（*）和資料夾（Directory）都會出現。
/// </summary>
public static class ContextMenuInstaller
{
    const string Verb = "MoveTidy";
    static readonly string[] Targets = [@"Software\Classes\*\shell\" + Verb, @"Software\Classes\Directory\shell\" + Verb];

    public static bool IsInstalled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Targets[0]);
            return k is not null;
        }
    }

    /// <summary>選單目前指向的程式路徑（沒安裝時為 null）。</summary>
    public static string? InstalledExe
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Targets[0]);
            return k?.GetValue("Icon") is string icon ? icon.Split(',')[0].Trim('"') : null;
        }
    }

    public static string CurrentExe => Environment.ProcessPath!;

    public static void Install(AppSettings s, string? exe = null)
    {
        exe ??= CurrentExe;
        Uninstall();
        foreach (var target in Targets)
        {
            using var root = Registry.CurrentUser.CreateSubKey(target);
            root.SetValue("MUIVerb", L.Get("app"));
            root.SetValue("SubCommands", "");
            root.SetValue("Icon", $"\"{exe}\",0");
            using var shell = root.CreateSubKey("shell");
            int order = 0;
            for (int i = 0; i < s.Favorites.Count; i++)
            {
                var f = s.Favorites[i];
                Add(shell, order++, L.F("menu_to", f.Name), $"\"{exe}\" --quick {i} \"%1\"", false);
                if (f.ShowDateEntry)
                    Add(shell, order++, L.F("menu_to_date", f.Name), $"\"{exe}\" --quick {i} --date \"%1\"", false);
            }
            Add(shell, 90, L.Get("menu_other"), $"\"{exe}\" --pick \"%1\"", s.Favorites.Count > 0);
            Add(shell, 91, L.Get("menu_settings"), $"\"{exe}\"", false);
        }
    }

    static void Add(RegistryKey shell, int order, string text, string command, bool separatorBefore)
    {
        using var k = shell.CreateSubKey($"{order:00}");
        k.SetValue("MUIVerb", text);
        k.SetValue("MultiSelectModel", "Player");
        if (separatorBefore) k.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord);
        using var c = k.CreateSubKey("command");
        c.SetValue("", command);
    }

    public static void Uninstall()
    {
        foreach (var target in Targets)
            Registry.CurrentUser.DeleteSubKeyTree(target, throwOnMissingSubKey: false);
    }
}
