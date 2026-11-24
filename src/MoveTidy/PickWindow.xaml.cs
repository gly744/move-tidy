using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>「移到其他位置…」：當場選目的地、日期資料夾和命名方式。</summary>
public partial class PickWindow : Window
{
    readonly AppSettings _s;
    readonly List<string> _sources;
    string? _base;
    bool _ready;

    public PickWindow(AppSettings settings, List<string> sources)
    {
        InitializeComponent();
        App.UseDarkTitleBar(this);
        _s = settings;
        _sources = sources;

        _base = _s.PickLastPath is { Length: > 0 } last && Directory.Exists(last) ? last
              : _s.Favorites.FirstOrDefault(f => Directory.Exists(f.Path))?.Path;
        DateBox.IsChecked = _s.PickDateFolder;
        (_s.PickMode == NamingMode.Number ? NumRadio : KeepRadio).IsChecked = true;
        (_s.PickOrder == NumberOrder.Modified ? ByTime : ByName).IsChecked = true;
        DateText.Text = _s.TodayFolderName();

        foreach (var f in _s.Favorites)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = f.Name, GroupName = "dest", Tag = f.Path, ToolTip = f.Path };
            chip.Checked += (_, _) => { _base = (string)chip.Tag; Refresh(); };
            Chips.Children.Add(chip);
        }

        L.I.LanguageChanged += Refresh;
        _ready = true;
        Refresh();
    }

    string? Dest => _base is null ? null : DateBox.IsChecked == true ? Path.Combine(_base, _s.TodayFolderName()) : _base;

    PlanOptions Options => new(NumRadio.IsChecked == true ? NamingMode.Number : NamingMode.Keep,
                               ByTime.IsChecked == true ? NumberOrder.Modified : NumberOrder.Name,
                               _s.Digits);

    void Changed(object sender, RoutedEventArgs e) => Refresh();

    void Refresh()
    {
        if (!_ready) return;
        Heading.Text = L.F("pick_title", _sources.Count);
        OrderSeg.IsEnabled = NumRadio.IsChecked == true;
        OrderSeg.Opacity = OrderSeg.IsEnabled ? 1 : 0.45;

        foreach (RadioButton c in Chips.Children)
            c.IsChecked = _base is not null && string.Equals(NamePlanner.Normalize((string)c.Tag), NamePlanner.Normalize(_base), StringComparison.OrdinalIgnoreCase);
        Chips.Visibility = Chips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        PathBox.Inlines.Clear();
        if (_base is null)
        {
            PathBox.Inlines.Add(new System.Windows.Documents.Run(L.Get("choose_dest")) { Foreground = (Brush)FindResource("Muted") });
            Rows.ItemsSource = _sources.Select((s, i) => PreviewRow.Of(new(s, Path.GetFileName(s), Directory.Exists(s), false, SkipReason.None), i)).ToList();
            MoveBtn.IsEnabled = false;
            return;
        }
        PathBox.Inlines.Add(new System.Windows.Documents.Run(ShortPath(NamePlanner.Normalize(_base))));
        PathBox.ToolTip = Dest;
        if (DateBox.IsChecked == true)
            PathBox.Inlines.Add(new System.Windows.Documents.Run("\\" + _s.TodayFolderName()) { Foreground = (Brush)FindResource("Accent"), FontWeight = FontWeights.SemiBold });

        var plan = NamePlanner.Plan(_sources, Dest!, Options);
        Rows.ItemsSource = plan.Select((p, i) => PreviewRow.Of(p, i)).ToList();
        MoveBtn.IsEnabled = plan.Any(p => p.Skip == SkipReason.None);
    }

    /// <summary>路徑太長時保留開頭磁碟和最後兩層：C:\…\dest\下載整理。</summary>
    internal static string ShortPath(string path, int max = 46)
    {
        if (path.Length <= max) return path;
        var parts = path.Split('\\');
        if (parts.Length <= 3) return path;
        return $"{parts[0]}\\…\\{parts[^2]}\\{parts[^1]}";
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.Get("pick_folder_title") };
        if (_base is not null && Directory.Exists(_base)) dlg.InitialDirectory = _base;
        if (dlg.ShowDialog(this) == true)
        {
            _base = dlg.FolderName;
            Refresh();
        }
    }

    void Move_Click(object sender, RoutedEventArgs e)
    {
        if (Dest is not { } dest) return;
        var options = Options;
        try
        {
            AppSettings.Update(s =>
            {
                s.PickLastPath = _base;
                s.PickDateFolder = DateBox.IsChecked == true;
                s.PickMode = options.Mode;
                s.PickOrder = options.Order;
            });
        }
        catch (IOException) { }

        var plan = NamePlanner.Plan(_sources, dest, options);
        new ProgressWindow(plan, dest, _s.AutoClose).Show();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        L.I.LanguageChanged -= Refresh;
        base.OnClosed(e);
    }
}
