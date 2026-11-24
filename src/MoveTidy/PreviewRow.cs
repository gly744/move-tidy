using System.IO;
using System.Windows;
using System.Windows.Media;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>預覽清單的一列（畫面樣板在 App.xaml 的 PreviewRow）。</summary>
sealed record PreviewRow(string Icon, string From, string To, string Tag, Brush? TagBg, Brush? TagFg, Visibility TagVisibility, double ToOpacity, Thickness Separator)
{
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff" };

    /// <param name="showRenamed">改了名字的要不要標「已改名」（改名視窗每個都會改，不用標）。</param>
    public static PreviewRow Of(PlannedItem p, int index, bool showRenamed = true)
    {
        var icon = p.IsDirectory ? "" : ImageExt.Contains(Path.GetExtension(p.Source)) ? "" : "";
        var app = Application.Current;
        string tag = p.Skip switch
        {
            SkipReason.AlreadyThere => L.Get("skip_there"),
            SkipReason.IntoItself => L.Get("skip_into"),
            SkipReason.Missing => L.Get("skip_missing"),
            SkipReason.SameName => L.Get("skip_same"),
            _ => p.Renamed && showRenamed ? L.Get("renamed") : "",
        };
        bool isSkip = p.Skip != SkipReason.None;
        return new PreviewRow(icon, Path.GetFileName(p.Source), isSkip ? "—" : p.TargetName, tag,
            (Brush)app.FindResource(isSkip ? "ErrBg" : "WarnBg"),
            (Brush)app.FindResource(isSkip ? "ErrFg" : "WarnFg"),
            tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            isSkip ? 0.5 : 1,
            new Thickness(0, index == 0 ? 0 : 1, 0, 0));
    }
}
