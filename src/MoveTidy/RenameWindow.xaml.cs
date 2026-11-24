using System.IO;
using System.Windows;
using System.Windows.Controls;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>「自動改名」的確認視窗：挑樣式，先看改完的名字再確定。</summary>
public partial class RenameWindow : Window
{
    readonly AppSettings _s;
    readonly List<string> _sources;
    IReadOnlyList<PlannedItem> _plan = [];
    bool _ready;

    public RenameWindow(AppSettings settings, List<string> sources)
    {
        InitializeComponent();
        App.UseDarkTitleBar(this);
        _s = settings;
        _sources = sources;

        Picker.Selected = _s.RenameStyle;
        Picker.Changed += () => { Refresh(); FocusPrefix(); };
        PrefixBox.Text = _s.RenamePrefix;
        (_s.Order == NumberOrder.Modified ? ByTime : ByName).IsChecked = true;

        L.I.LanguageChanged += Refresh;
        Loaded += (_, _) => { Activate(); FocusPrefix(); };
        _ready = true;
        Refresh();
    }

    void FocusPrefix()
    {
        if (Picker.Selected != RenameStyle.PrefixNumber) return;
        PrefixBox.Focus();
        PrefixBox.SelectAll();
    }

    void Changed(object sender, RoutedEventArgs e) => Refresh();
    void Prefix_Changed(object sender, TextChangedEventArgs e) => Refresh();

    void Refresh()
    {
        if (!_ready) return;
        var style = Picker.Selected;
        Heading.Text = L.F("rn_title", _sources.Count);
        PrefixPanel.Visibility = style == RenameStyle.PrefixNumber ? Visibility.Visible : Visibility.Collapsed;
        OrderPanel.Visibility = Renamer.IsNumbered(style) ? Visibility.Visible : Visibility.Collapsed;

        _plan = Renamer.Plan(_sources, new(style, ByTime.IsChecked == true ? NumberOrder.Modified : NumberOrder.Name, _s.Digits, PrefixBox.Text));
        Rows.ItemsSource = _plan.Select((p, i) => PreviewRow.Of(p, i, showRenamed: false)).ToList();
        OkBtn.IsEnabled = _plan.Any(p => p.Skip == SkipReason.None)
            && (style != RenameStyle.PrefixNumber || Renamer.CleanPrefix(PrefixBox.Text).Length > 0);
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Picker.Selected == RenameStyle.PrefixNumber)
        {
            _s.RenamePrefix = PrefixBox.Text.Trim();
            try { _s.Save(); } catch (IOException) { }
        }
        ProgressWindow.Rename(_plan, _s.AutoClose).Show(); // 用畫面上看到的這份名字
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        L.I.LanguageChanged -= Refresh;
        base.OnClosed(e);
    }
}
