using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MoveTidy;

public partial class App : Application
{
    /// <summary>依語言換字型：繁體用正黑體、簡體用雅黑。</summary>
    public void ApplyLanguageFont()
    {
        var cjk = L.I.Lang == "cn" ? "Microsoft YaHei UI" : "Microsoft JhengHei UI";
        Resources["UiFont"] = new FontFamily($"Segoe UI, {cjk}");
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>讓 Windows 標題列也變成深色，跟視窗內容一致。</summary>
    public static void UseDarkTitleBar(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            int on = 1;
            DwmSetWindowAttribute(h, 20, ref on, sizeof(int));           // 深色模式
            int caption = 0x001C1918;                                     // #18191C（BGR）
            DwmSetWindowAttribute(h, 35, ref caption, sizeof(int));
            int text = 0x00EAE8E7;
            DwmSetWindowAttribute(h, 36, ref text, sizeof(int));
            int border = 0x00332E2C;
            DwmSetWindowAttribute(h, 34, ref border, sizeof(int));
        };
    }
}
