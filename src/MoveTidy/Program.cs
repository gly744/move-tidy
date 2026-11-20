using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>
/// 指令格式（右鍵選單會這樣呼叫）：
///   MoveTidy.exe                         打開設定
///   MoveTidy.exe --quick 0 "路徑"        移到第 1 個常用目的地
///   MoveTidy.exe --quick 0 --date "路徑" 移到第 1 個常用目的地\今天日期
///   MoveTidy.exe --pick "路徑"           打開「移到其他位置…」視窗
///   MoveTidy.exe --install-menu          安裝（或更新）右鍵選單
///   MoveTidy.exe --uninstall-menu        移除右鍵選單
/// </summary>
public static class Program
{
    enum Kind { Settings, Quick, Pick }

    [STAThread]
    public static int Main(string[] args)
    {
        // 安裝／移除右鍵選單（給安裝腳本用，不開視窗）
        if (args is ["--install-menu"] or ["--uninstall-menu"])
        {
            var s = AppSettings.Load();
            L.I.Lang = s.Language;
            if (args[0] == "--install-menu") ContextMenuInstaller.Install(s);
            else ContextMenuInstaller.Uninstall();
            return 0;
        }

        var kind = Kind.Settings;
        int fav = -1;
        bool date = false;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--quick" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n):
                    kind = Kind.Quick; fav = n; i++; break;
                case "--date": date = true; break;
                case "--pick": kind = Kind.Pick; break;
                default: paths.Add(args[i]); break;
            }
        }

        if (kind != Kind.Settings)
        {
            // 檔案總管選了多個項目時，會為每個項目各啟動一次本程式；
            // 第一個啟動的負責收集，其他的把路徑交給它後立刻結束。
            var collected = Aggregator.Collect($"{kind}-{fav}-{date}", paths);
            if (collected is null) return 0;
            paths = collected.Where(Path.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) return 0;
        }

        var settings = AppSettings.Load();
        var app = new App();
        app.InitializeComponent();
        L.I.Lang = settings.Language;
        app.ApplyLanguageFont();

        switch (kind)
        {
            case Kind.Quick:
                if (fav < 0 || fav >= settings.Favorites.Count || string.IsNullOrWhiteSpace(settings.Favorites[fav].Path))
                {
                    MessageBox.Show(L.Get("fav_missing"), L.Get("app"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 1;
                }
                var dest = settings.Favorites[fav].Path;
                if (date) dest = Path.Combine(dest, AppSettings.TodayFolderName());
                var plan = NamePlanner.Plan(paths, dest, settings.QuickOptions);
                return app.Run(new ProgressWindow(plan, dest, settings.AutoClose));
            case Kind.Pick:
                return app.Run(new PickWindow(settings, paths));
            default:
                return app.Run(new SettingsWindow(settings));
        }
    }
}

static class Aggregator
{
    const int IdleMs = 700;      // 最後一個路徑送來後再等這麼久沒新的就開始
    const int MaxMs = 15000;

    /// <summary>回傳 null 代表路徑已交給別的程式處理，自己可以結束。</summary>
    public static List<string>? Collect(string key, List<string> mine)
    {
        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{Environment.UserName}|{key}")))[..16];
        var name = $"MoveTidy-{id}";
        var mutex = new Mutex(true, $@"Local\{name}", out bool first);
        if (!first)
        {
            mutex.Dispose();
            try
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
                client.Connect(2500);
                using var w = new StreamWriter(client, new UTF8Encoding(false));
                foreach (var p in mine) w.WriteLine(p);
                return null;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                return mine; // 收集者已經結束，自己處理
            }
        }

        var all = new List<string>(mine);
        var sw = Stopwatch.StartNew();
        long last = 0;
        try
        {
            while (sw.ElapsedMilliseconds < MaxMs)
            {
                var remaining = IdleMs - (sw.ElapsedMilliseconds - last);
                if (remaining <= 0) break;
                using var server = new NamedPipeServerStream(name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var wait = server.WaitForConnectionAsync();
                if (!wait.Wait((int)remaining)) break;
                using var r = new StreamReader(server, Encoding.UTF8);
                while (r.ReadLine() is { } line)
                    if (line.Length > 0) all.Add(line);
                last = sw.ElapsedMilliseconds;
            }
        }
        catch (AggregateException) { }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
        return all;
    }
}
