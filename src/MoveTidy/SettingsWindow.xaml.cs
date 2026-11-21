using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MoveTidy.Core;

namespace MoveTidy;

public partial class SettingsWindow : Window
{
    readonly AppSettings _s;
    readonly ObservableCollection<FavItem> _favs = [];
    bool _ready;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        App.UseDarkTitleBar(this);
        _s = settings;

        foreach (var f in _s.Favorites) _favs.Add(Watch(new FavItem(f)));
        FavList.ItemsSource = _favs;
        _favs.CollectionChanged += (_, _) => RenumberRows();
        RenumberRows();

        (_s.Mode == NamingMode.Number ? ModeNum : ModeKeep).IsChecked = true;
        (_s.Order == NumberOrder.Modified ? OrderTime : OrderName).IsChecked = true;
        (_s.Digits switch { 1 => Dig1, 2 => Dig2, _ => Dig3 }).IsChecked = true;
        AutoClose.IsChecked = _s.AutoClose;
        TidyFolders.IsChecked = _s.TidyIncludeFolders;
        foreach (var fmt in AppSettings.DateFormats)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Seg"), GroupName = "datefmt", Tag = fmt,
                Content = AppSettings.FormatDate(fmt), FontFamily = (FontFamily)FindResource("Mono"),
                IsChecked = fmt == _s.DateFormat || (fmt == AppSettings.DateFormats[0] && !AppSettings.DateFormats.Contains(_s.DateFormat)),
            };
            rb.Checked += DateFmt_Checked;
            DateFmtBox.Children.Add(rb);
        }
        (_s.Language == "cn" ? LangCn : LangTw).IsChecked = true;

        _ready = true;
        RefreshMenuStatus();
    }

    // ── 導覽 ──
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageFav is null) return;
        PageFav.Visibility = NavFav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageNaming.Visibility = NavNaming.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageMenu.Visibility = NavMenu.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageLang.Visibility = NavLang.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavMenu.IsChecked == true) RefreshMenuStatus();
    }

    // ── 常用目的地 ──
    FavItem Watch(FavItem f)
    {
        f.PropertyChanged += (_, _) => SaveFavorites();
        return f;
    }

    void RenumberRows()
    {
        for (int i = 0; i < _favs.Count; i++) _favs[i].Separator = new Thickness(0, i == 0 ? 0 : 1, 0, 0);
        FavEmpty.Visibility = _favs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.Get("pick_folder_title") };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FolderName;
        if (_favs.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(name)) name = path.TrimEnd('\\', ':');
        _favs.Add(Watch(new FavItem(new Favorite { Name = name, Path = path, ShowDateEntry = true })));
        SaveFavorites();
    }

    void Up_Click(object sender, RoutedEventArgs e) => MoveFav(sender, -1);
    void Down_Click(object sender, RoutedEventArgs e) => MoveFav(sender, +1);

    void MoveFav(object sender, int delta)
    {
        var f = (FavItem)((Button)sender).Tag;
        int i = _favs.IndexOf(f), j = i + delta;
        if (j < 0 || j >= _favs.Count) return;
        _favs.Move(i, j);
        SaveFavorites();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        _favs.Remove((FavItem)((Button)sender).Tag);
        SaveFavorites();
    }

    void SaveFavorites()
    {
        _s.Favorites = _favs.Select(f => f.ToFavorite()).ToList();
        Save(menuChanged: true);
    }

    // ── 命名規則 ──
    void Opt_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.Mode = ModeNum.IsChecked == true ? NamingMode.Number : NamingMode.Keep;
        _s.Order = OrderTime.IsChecked == true ? NumberOrder.Modified : NumberOrder.Name;
        _s.Digits = Dig1.IsChecked == true ? 1 : Dig2.IsChecked == true ? 2 : 3;
        _s.AutoClose = AutoClose.IsChecked == true;
        _s.TidyIncludeFolders = TidyFolders.IsChecked == true;
        Save(menuChanged: false);
    }

    void DateFmt_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.DateFormat = (string)((RadioButton)sender).Tag;
        Save(menuChanged: false);
    }

    // ── 語言 ──
    void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.Language = (string)((RadioButton)sender).Tag;
        L.I.Lang = _s.Language;
        ((App)Application.Current).ApplyLanguageFont();
        Save(menuChanged: true);
        RefreshMenuStatus();
    }

    // ── 右鍵選單 ──
    void RefreshMenuStatus()
    {
        bool on = ContextMenuInstaller.IsInstalled;
        var exe = ContextMenuInstaller.InstalledExe;
        MenuDot.Fill = (Brush)FindResource(on ? "Accent" : "Muted");
        MenuStatus.Text = L.Get(on ? "menu_on" : "menu_off");
        MenuExe.Text = on ? L.F("menu_exe", exe ?? "") : "";
        MenuExe.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MenuMoved.Visibility = on && !string.Equals(exe, ContextMenuInstaller.CurrentExe, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        InstallBtn.Content = L.Get(on ? "menu_reinstall" : "menu_install");
        UninstallBtn.IsEnabled = on;
    }

    void Install_Click(object sender, RoutedEventArgs e)
    {
        ContextMenuInstaller.Install(_s);
        RefreshMenuStatus();
    }

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        ContextMenuInstaller.Uninstall();
        RefreshMenuStatus();
    }

    void Save(bool menuChanged)
    {
        if (!_ready) return;
        try { _s.Save(); } catch (IOException) { }
        // 已安裝的選單跟著更新（只在指向這個程式時才改，避免動到別處安裝的版本）
        if (menuChanged && ContextMenuInstaller.IsInstalled &&
            string.Equals(ContextMenuInstaller.InstalledExe, ContextMenuInstaller.CurrentExe, StringComparison.OrdinalIgnoreCase))
            ContextMenuInstaller.Install(_s);
    }

    /// <summary>設定畫面裡的一個常用目的地。</summary>
    public sealed class FavItem(Favorite f) : INotifyPropertyChanged
    {
        string _name = f.Name;
        bool _showDate = f.ShowDateEntry;
        Thickness _sep;

        public string Path { get; } = f.Path;
        public string Name { get => _name; set { if (_name != value) { _name = value; On(); } } }
        public bool ShowDate { get => _showDate; set { if (_showDate != value) { _showDate = value; On(); } } }
        public Thickness Separator { get => _sep; set { _sep = value; PropertyChanged?.Invoke(this, new(nameof(Separator))); } }

        public Favorite ToFavorite() => new() { Name = string.IsNullOrWhiteSpace(_name) ? System.IO.Path.GetFileName(Path) : _name.Trim(), Path = Path, ShowDateEntry = _showDate };

        public event PropertyChangedEventHandler? PropertyChanged;
        void On([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
    }
}
