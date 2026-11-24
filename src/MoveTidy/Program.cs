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
///   MoveTidy.exe --fav ID "路徑"         移到常用目的地（ID 見 Favorite.Id）
///   MoveTidy.exe --fav ID --date "路徑"  移到常用目的地\今天日期
///   MoveTidy.exe --quick 0 "路徑"        beta.4 以前的選單（只認第幾個，可能搬錯地方）：不搬，請使用者更新選單
///   MoveTidy.exe --pick "路徑"           打開「移到其他位置…」視窗
///   MoveTidy.exe --rename "路徑"         自動改名（樣式在設定裡選）
///   MoveTidy.exe --paste "資料夾"        把剪下／複製的檔案貼到這個資料夾，重名自動改名（空白處右鍵）
///   MoveTidy.exe --tidy "資料夾"         把資料夾裡的項目移到「資料夾\今天日期」（空白處右鍵）
///   MoveTidy.exe --undo                  撤回上一次移動或改名
///   MoveTidy.exe --install-menu          安裝（或更新）右鍵選單
///   MoveTidy.exe --uninstall-menu        移除右鍵選單
/// </summary>
public static class Program
{
    enum Kind { Settings, Quick, OldMenu, Pick, Rename, Paste, Tidy, Undo }

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
        string fav = "";
        bool date = false;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fav" when i + 1 < args.Length: kind = Kind.Quick; fav = args[++i]; break;
                case "--quick" when i + 1 < args.Length: kind = Kind.OldMenu; i++; break;
                case "--date": date = true; break;
                case "--pick": kind = Kind.Pick; break;
                case "--rename": kind = Kind.Rename; break;
                case "--paste": kind = Kind.Paste; break;
                case "--tidy": kind = Kind.Tidy; break;
                case "--undo": kind = Kind.Undo; break;
                default: paths.Add(args[i]); break;
            }
        }

        // 設定視窗只開一個（檔案總管選了多個項目再點「設定…」也會啟動很多次）
        SingleWindow? single = null;
        if (kind == Kind.Settings)
        {
            single = SingleWindow.Claim($"Settings|{AppSettings.DataDir}");
            if (single is null) return 0;
        }
        else if (kind == Kind.Undo)
        {
            // 選了很多項目再點「撤回」也會啟動很多次，只讓第一個做
            single = SingleWindow.Claim($"Undo|{AppSettings.DataDir}");
            if (single is null) return 0;
        }
        else if (kind is Kind.Tidy or Kind.Paste)
        {
            // 同一個資料夾正在整理（或貼上）時，重複點選單不再開第二次
            if (paths is not [var raw]) return 0;
            var folder = raw.Trim('"');                          // 磁碟根目錄 "D:\" 會傳成 D:\" 
            if (folder.EndsWith(':')) folder += "\\";
            if (!Directory.Exists(folder)) return 0;
            paths = [folder];
            single = SingleWindow.Claim($"{kind}|{NamePlanner.Normalize(folder)}");
            if (single is null) return 0;
        }
        else
        {
            // 檔案總管選了多個項目時，會為每個項目各啟動一次本程式；
            // 第一個啟動的負責收集，其他的把路徑交給它後立刻結束。
            var collected = Aggregator.Collect($"{kind}-{fav}-{date}", paths);
            if (collected is null) return 0;
            paths = collected.Where(Path.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) return 0;
        }

        MoveJournal.Recover(); // 上次意外中斷的先收拾好

        var settings = AppSettings.Load();
        var app = new App();
        app.InitializeComponent();
        L.I.Lang = settings.Language;
        app.ApplyLanguageFont();

        switch (kind)
        {
            case Kind.Quick:
                if (settings.FindFavorite(fav) is not { } to || string.IsNullOrWhiteSpace(to.Path))
                {
                    MessageBox.Show(L.Get("fav_missing"), L.Get("app"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 1;
                }
                var dest = to.Path;
                if (date) dest = Path.Combine(dest, settings.TodayFolderName());
                var plan = NamePlanner.Plan(paths, dest, settings.QuickOptions);
                return app.Run(new ProgressWindow(plan, dest, settings.AutoClose));
            case Kind.OldMenu:
                if (MessageBox.Show(L.Get("menu_old"), L.Get("app"), MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
                {
                    ContextMenuInstaller.Install(settings);
                    MessageBox.Show(L.Get("menu_updated"), L.Get("app"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return 0;
            case Kind.Pick:
                return app.Run(new PickWindow(settings, paths));
            case Kind.Rename:
                if (settings.RenameNeedsWindow) return app.Run(new RenameWindow(settings, paths));
                return app.Run(ProgressWindow.Rename(Renamer.Plan(paths, settings.RenameOptions), settings.AutoClose));
            case Kind.Paste:
                return RunPaste(app, settings, NamePlanner.Normalize(paths[0]));
            case Kind.Tidy:
                return RunTidy(app, settings, NamePlanner.Normalize(paths[0]));
            case Kind.Undo:
                return RunUndo(app, settings);
            default:
                var win = new SettingsWindow(settings);
                single!.BringToFrontOnSignal(win);
                return app.Run(win);
        }
    }

    /// <summary>把資料夾裡的項目移到「資料夾\今天日期」：先問一次，再顯示進度。</summary>
    static int RunTidy(App app, AppSettings settings, string folder)
    {
        var items = FolderTidy.Collect(folder, settings.TidyIncludeFolders);
        var dest = Path.Combine(folder, settings.TodayFolderName());
        if (items.Count == 0)
        {
            MessageBox.Show(L.Get("tidy_nothing"), L.Get("app"), MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        var ask = L.F("tidy_confirm", Path.GetFileName(folder), items.Count, Path.GetFileName(dest));
        if (MessageBox.Show(ask, L.Get("app"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return 0;
        var plan = NamePlanner.Plan(items, dest, settings.QuickOptions);
        return app.Run(new ProgressWindow(plan, dest, settings.AutoClose));
    }

    /// <summary>把剪下（移動）或複製的檔案貼到這個資料夾：名字保留，重名的才加號碼。</summary>
    static int RunPaste(App app, AppSettings settings, string folder)
    {
        var clip = PasteClipboard.Read();
        var paths = clip?.Paths.Where(Path.Exists).ToList();
        if (clip is null || paths!.Count == 0)
        {
            MessageBox.Show(L.Get(clip is null ? "paste_none" : "paste_gone"), L.Get("app"), MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        var plan = NamePlanner.Plan(paths, folder, new(Copy: !clip.Cut));
        return app.Run(ProgressWindow.Paste(plan, folder, settings.AutoClose, clip));
    }

    /// <summary>撤回上一次移動或改名：先問一次，再顯示進度。</summary>
    static int RunUndo(App app, AppSettings settings)
    {
        var batch = MoveHistory.Last();
        if (batch is null)
        {
            MessageBox.Show(L.Get("undo_none"), L.Get("app"), MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        var ask = L.F(batch.Kind == BatchKind.Rename ? "rn_undo_confirm" : "undo_confirm", batch.Time.ToString("MM-dd HH:mm"), batch.Items.Count, batch.Dest);
        if (MessageBox.Show(ask, L.Get("app"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return 0;
        return app.Run(new ProgressWindow(batch, settings.AutoClose));
    }

    /// <summary>把任意字串轉成可當作系統物件名稱的短名字（同一個使用者同一個 key 得到同一個名字）。</summary>
    public static string ObjectName(string key) =>
        "MoveTidy-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{Environment.UserName}|{key}")))[..16];
}

/// <summary>
/// 讓某種視窗只開一個：第一個程式持有具名 Mutex 並等待通知；
/// 之後再啟動的程式只發通知請它跳到最前面，然後結束。
/// </summary>
sealed class SingleWindow
{
    static SingleWindow? _current; // 整個程式執行期間都留著，Mutex 才不會被回收

    readonly Mutex _mutex;
    readonly EventWaitHandle _show;

    SingleWindow(Mutex mutex, EventWaitHandle show) { _mutex = mutex; _show = show; }

    /// <summary>回傳 null 代表已經有一個開著，已通知它跳到最前面。</summary>
    public static SingleWindow? Claim(string key)
    {
        var name = Program.ObjectName(key);
        var mutex = new Mutex(true, $@"Local\{name}", out bool first);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}-show");
        if (!first)
        {
            AllowSetForegroundWindow(-1); // 讓已開著的那個可以搶到前景
            show.Set();
            show.Dispose();
            mutex.Dispose();
            return null;
        }
        return _current = new SingleWindow(mutex, show);
    }

    public void BringToFrontOnSignal(Window w)
    {
        new Thread(() =>
        {
            while (_show.WaitOne())
                w.Dispatcher.BeginInvoke(() =>
                {
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Activate();
                    w.Topmost = true;
                    w.Topmost = false;
                });
        }) { IsBackground = true }.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}

static class Aggregator
{
    const int IdleMs = 700;      // 最後一個路徑送來後再等這麼久沒新的就開始
    const int MaxMs = 15000;

    /// <summary>回傳 null 代表路徑已交給別的程式處理，自己可以結束。</summary>
    public static List<string>? Collect(string key, List<string> mine)
    {
        var name = Program.ObjectName(key);
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
