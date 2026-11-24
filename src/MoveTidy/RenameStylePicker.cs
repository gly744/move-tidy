using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>挑「自動改名」樣式的兩欄清單（隨機／按順序），設定和改名視窗共用。</summary>
public sealed class RenameStylePicker : Grid
{
    static readonly RenameStyle[] Random = [RenameStyle.StampRandom, RenameStyle.RandomText, RenameStyle.RandomDigits, RenameStyle.KeepRandom];
    static readonly RenameStyle[] Numbered = [RenameStyle.StampNumber, RenameStyle.PrefixNumber, RenameStyle.PureNumber];

    readonly Dictionary<RenameStyle, RadioButton> _radios = [];
    readonly string _group = Guid.NewGuid().ToString("N");

    public event Action? Changed;

    public RenameStylePicker()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(Column("rn_random", Random));
        var right = Column("rn_seq", Numbered);
        SetColumn(right, 2);
        Children.Add(right);
    }

    public RenameStyle Selected
    {
        get => _radios.FirstOrDefault(r => r.Value.IsChecked == true).Key;
        set => _radios[value].IsChecked = true;
    }

    static string Example(RenameStyle s) => s switch
    {
        RenameStyle.StampRandom => "20261124-153012-k7f2.jpg",
        RenameStyle.RandomText => "k7f2m9xq.jpg",
        RenameStyle.RandomDigits => "4829105736.jpg",
        RenameStyle.KeepRandom => "image_k7f2.jpg",
        RenameStyle.StampNumber => "20261124-153012-001.jpg",
        RenameStyle.PrefixNumber => "旅行-001.jpg",
        _ => "001.jpg",
    };

    StackPanel Column(string title, RenameStyle[] styles)
    {
        var app = Application.Current;
        var list = new StackPanel();
        foreach (var s in styles)
        {
            if (list.Children.Count > 0) list.Children.Add(new Border { Height = 1, Background = (Brush)app.FindResource("Line") });
            var name = new TextBlock { FontWeight = FontWeights.SemiBold };
            name.SetBinding(TextBlock.TextProperty, Text("rn_" + s));
            var ex = new TextBlock
            {
                Text = Example(s), Style = (Style)app.FindResource("Hint"), FontFamily = (FontFamily)app.FindResource("Mono"),
                FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var rb = new RadioButton
            {
                Style = (Style)app.FindResource("OptionCard"), GroupName = _group,
                Content = new StackPanel { Children = { name, ex } },
            };
            rb.Checked += (_, _) => Changed?.Invoke();
            _radios[s] = rb;
            list.Children.Add(rb);
        }
        var head = new TextBlock { Style = (Style)app.FindResource("Lbl") };
        head.SetBinding(TextBlock.TextProperty, Text(title));
        return new StackPanel
        {
            Children = { head, new Border { Style = (Style)app.FindResource("CardBorder"), Child = list } },
        };
    }

    static System.Windows.Data.Binding Text(string key) => new($"[{key}]") { Source = L.I, Mode = System.Windows.Data.BindingMode.OneWay };
}
