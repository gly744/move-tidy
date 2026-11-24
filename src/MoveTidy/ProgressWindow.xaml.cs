using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>
/// 右下角的小進度窗：搬完後自動關閉（滑鼠移上去就停住）；有失敗時留著讓人看。
/// 移動、複製（貼到這裡）、改名都用它；完成後可以按「撤回」，也用來執行右鍵的「撤回上一次」。
/// </summary>
public partial class ProgressWindow : Window
{
    enum Op { Move, Copy, Rename }

    readonly Op _op;
    readonly IReadOnlyList<PlannedItem>? _plan;
    readonly PasteClipboard? _clip;   // 「貼到這裡」用：搬完要清空剪貼簿
    readonly string _dest;
    readonly bool _autoClose;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int _countdown = 5;

    MoveResult? _result;          // 移動的結果
    MoveBatch? _batch;            // 這次移動的紀錄（撤回用）
    bool _undoing;                // 現在顯示的是撤回
    UndoResult? _undo;            // 撤回的結果
    string? _error;

    public ProgressWindow(IReadOnlyList<PlannedItem> plan, string dest, bool autoClose) : this(Op.Move, plan, dest, autoClose, null) { }

    /// <summary>「貼到這裡」：剪下的就移動，複製的就複製。</summary>
    public static ProgressWindow Paste(IReadOnlyList<PlannedItem> plan, string dest, bool autoClose, PasteClipboard clip) =>
        new(clip.Cut ? Op.Move : Op.Copy, plan, dest, autoClose, clip);

    /// <summary>「自動改名」：留在原資料夾，只換名字。</summary>
    public static ProgressWindow Rename(IReadOnlyList<PlannedItem> plan, bool autoClose) =>
        new(Op.Rename, plan, Path.GetDirectoryName(plan[0].Source) ?? plan[0].Source, autoClose, null);

    ProgressWindow(Op op, IReadOnlyList<PlannedItem> plan, string dest, bool autoClose, PasteClipboard? clip)
    {
        InitializeComponent();
        _op = op;
        _plan = plan;
        _clip = clip;
        _dest = dest;
        _autoClose = autoClose;
        Init();
    }

    /// <summary>右鍵「撤回上一次」：一打開就撤回這一批。</summary>
    public ProgressWindow(MoveBatch batch, bool autoClose)
    {
        InitializeComponent();
        _op = batch.Kind == BatchKind.Rename ? Op.Rename : Op.Move;
        _batch = batch;
        _dest = batch.Dest;
        _autoClose = autoClose;
        _undoing = true;
        Init();
    }

    void Init()
    {
        _timer.Tick += (_, _) => { if (--_countdown <= 0) Close(); else Render(); };
        L.I.LanguageChanged += Render;
        Loaded += OnLoaded;
        MouseEnter += (_, _) => StopCountdown();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { StopCountdown(); DragMove(); } };
    }

    int Total => _undoing ? _batch!.Items.Count : _plan!.Count;

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
        SizeChanged += (_, _) => Top = area.Bottom - ActualHeight - 4;

        if (_undoing) { await RunUndo(); return; }

        Render();
        var progress = new Progress<int>(SetBar);
        try
        {
            _result = await Task.Run(() => _op == Op.Rename
                ? Renamer.Execute(_plan!, progress)
                : Mover.Execute(_plan!, _dest, progress, copy: _op == Op.Copy));
            if (_result.Failures.Count == 0 && _result.Moved > 0) _clip?.ClearIfUnchanged();
            _batch = MoveBatch.From(_result, _dest, _op == Op.Rename ? BatchKind.Rename : BatchKind.Move);
            try { MoveHistory.Add(_batch); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        SetBar(Total);
        Finish(ok: _error is null && _result!.Failures.Count == 0 && _result.Leftovers.Count == 0, canUndo: _batch is { Items.Count: > 0 });
    }

    async Task RunUndo()
    {
        _timer.Stop();
        _countdown = 5;
        _undo = null;
        _error = null;
        Buttons.Visibility = Visibility.Collapsed;
        FailList.Visibility = Visibility.Collapsed;
        SetBar(0);
        Render();
        var batch = _batch!;
        var progress = new Progress<int>(SetBar);
        try
        {
            _undo = await Task.Run(() => Undoer.Execute(batch, progress));
            try { MoveHistory.Remove(batch.Id); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        SetBar(Total);
        Finish(ok: _error is null && _undo!.Failures.Count == 0 && _undo.Missing.Count == 0 && _undo.Leftovers.Count == 0, canUndo: false);
    }

    void Finish(bool ok, bool canUndo)
    {
        UndoBtn.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        // 改名和貼上時，使用者本來就在這個資料夾裡
        OpenBtn.Visibility = _undoing || _op == Op.Rename || _clip is not null ? Visibility.Collapsed : Visibility.Visible;
        Buttons.Visibility = Visibility.Visible;
        if (ok && _autoClose && !IsMouseOver) _timer.Start();
        Render();
    }

    void SetBar(int done)
    {
        var track = ((FrameworkElement)Bar.Parent).ActualWidth;
        Bar.Width = Total == 0 ? track : track * done / Total;
    }

    void Render()
    {
        bool rename = _op == Op.Rename;
        PathText.Text = _undoing ? L.F(rename ? "rn_undo_path" : "undo_path", PickWindow.ShortPath(_dest, 30))
                      : (rename ? "" : "→ ") + PickWindow.ShortPath(_dest, 40);
        PathText.ToolTip = _dest;
        bool running = _undoing ? _undo is null && _error is null : _result is null && _error is null;
        if (running)
        {
            TitleText.Text = _undoing ? L.F("undoing", Total)
                : L.F(_op switch { Op.Rename => "renaming", Op.Copy => "copying", _ => "moving" }, _plan!.Count(p => p.Skip == SkipReason.None));
            LeftText.Text = "";
            RightText.Text = "";
            return;
        }

        int failed;
        var left = (_undoing ? _undo?.Leftovers : _result?.Leftovers) ?? [];
        var parts = new List<string>();
        List<object> fails;
        if (_undoing)
        {
            failed = _error is not null ? Total : _undo!.Failures.Count + _undo.Missing.Count;
            if (_undo is not null)
            {
                if (_undo.Renamed > 0) parts.Add(L.F("undo_renamed", _undo.Renamed));
                fails = [.. _undo.Missing.Select(m => new { Name = Path.GetFileName(m), Message = L.Get("undo_missing") }),
                         .. _undo.Failures.Select(f => new { Name = Path.GetFileName(f.Source), Message = Explain(f) })];
            }
            else fails = [];
            TitleText.Text = failed == 0 ? L.F(rename ? "rn_undone" : "undone", _undo?.Restored ?? 0) : L.F("undo_fail", failed);
        }
        else
        {
            failed = _error is not null ? _plan!.Count(p => p.Skip == SkipReason.None) : _result!.Failures.Count;
            if (_result is not null)
            {
                if (rename) parts.Add(L.F("rn_n", _result.Moved));
                else parts.Add(_result.Renamed > 0 ? L.F("renamed_n", _result.Renamed) : L.Get("no_conflict"));
                if (_result.Skipped > 0) parts.Add(L.F("skipped_n", _result.Skipped));
                fails = [.. _result.Failures.Select(f => new { Name = Path.GetFileName(f.Source), Message = Explain(f) })];
            }
            else fails = [];
            var (good, bad) = _op switch { Op.Rename => ("rn_done", "rn_fail"), Op.Copy => ("copy_done", "copy_fail"), _ => ("done", "done_fail") };
            TitleText.Text = failed == 0 ? L.Get(good) : L.F(bad, failed);
        }
        if (left.Count > 0)
        {
            parts.Add(L.F("leftover_n", left.Count));
            fails.AddRange(left.Select(x => new { Name = Path.GetFileName(x.Source), Message = L.F("leftover", x.Left) }));
        }
        if (_error is not null) fails = [new { Name = _dest, Message = _error }];

        bool ok = failed == 0 && left.Count == 0;
        StatusIcon.Text = !ok ? "" : _undoing ? "" : ""; // 警告 / 撤回 / 打勾
        StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Accent" : "WarnFg");
        IconBox.Background = (System.Windows.Media.Brush)FindResource(ok ? "AccentSoft" : "WarnBg");
        Bar.Background = (System.Windows.Media.Brush)FindResource(ok ? "Accent" : "WarnFg");
        LeftText.Text = string.Join(" · ", parts);
        RightText.Text = _timer.IsEnabled ? L.F("closing", _countdown) : "";
        FailList.ItemsSource = fails;
        FailList.Visibility = fails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>常見錯誤換成中文說明，其他的保留系統原文。</summary>
    static string Explain(MoveFailure f) => f.Code switch
    {
        32 or 33 => L.Get("err_in_use"),
        5 => L.Get("err_denied"),
        39 or 112 => L.Get("err_disk_full"),
        206 => L.Get("err_too_long"),
        2 or 3 => L.Get("err_not_found"),
        _ => L.F("error", f.Message),
    };

    void StopCountdown()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        Render();
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_batch is null) return;
        _undoing = true;
        await RunUndo();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_dest)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dest}\"") { UseShellExecute = true });
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        L.I.LanguageChanged -= Render;
        base.OnClosed(e);
    }
}
