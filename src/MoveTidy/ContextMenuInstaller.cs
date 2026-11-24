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
    const string Background = @"Software\Classes\Directory\Background\shell\" + Verb;

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
            // 沒設的話預設是 Document：選超過 15 個項目時，Windows 會把整個「移動整理」藏起來
            root.SetValue("MultiSelectModel", "Player");
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
            Add(shell, 91, L.Get("menu_rename"), $"\"{exe}\" --rename \"%1\"", false);
            Add(shell, 92, L.Get("menu_undo"), $"\"{exe}\" --undo", true);
            Add(shell, 93, L.Get("menu_settings"), $"\"{exe}\"", false);
        }

        // 資料夾空白處：貼到這裡、把這裡的項目移到今天日期資料夾
        using (var bg = Registry.CurrentUser.CreateSubKey(Background))
        {
            bg.SetValue("MUIVerb", L.Get("app"));
            bg.SetValue("SubCommands", "");
            bg.SetValue("Icon", $"\"{exe}\",0");
            using var shell = bg.CreateSubKey("shell");
            Add(shell, 0, L.Get("menu_paste"), $"\"{exe}\" --paste \"%V\"", false);
            Add(shell, 1, L.Get("menu_tidy"), $"\"{exe}\" --tidy \"%V\"", false);
            Add(shell, 92, L.Get("menu_undo"), $"\"{exe}\" --undo", true);
            Add(shell, 93, L.Get("menu_settings"), $"\"{exe}\"", false);
        }
        NotifyExplorer();
    }

    /// <summary>告訴檔案總管右鍵選單有變動，讓它重新讀取。</summary>
    static void NotifyExplorer() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

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
        foreach (var target in Targets.Append(Background))
            Registry.CurrentUser.DeleteSubKeyTree(target, throwOnMissingSubKey: false);
        NotifyExplorer();
    }
}
