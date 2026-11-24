using System.Windows;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>上次意外中斷的正在收拾（逐檔比對可能要一點時間），收拾好自動關閉。</summary>
public partial class RecoverWindow : Window
{
    readonly Task _work;

    RecoverWindow(Task work)
    {
        InitializeComponent();
        _work = work;
        Loaded += async (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 4;
            Top = area.Bottom - ActualHeight - 4;
            try { await _work; }
            catch (Exception) { } // 收拾不了的留到下次
            Close();
        };
        Closing += (_, e) => e.Cancel = !_work.IsCompleted;
    }

    /// <summary>收拾上次中斷的。很快就好的話不會跳出視窗。</summary>
    public static void Run(Application app)
    {
        var work = Task.Run(() => MoveJournal.Recover());
        try { if (work.Wait(400)) return; }
        catch (AggregateException) { return; }
        var mode = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown; // 關掉這個小窗不能讓整個程式結束
        new RecoverWindow(work).ShowDialog();
        app.ShutdownMode = mode;
    }
}
