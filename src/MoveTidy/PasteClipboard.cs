using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace MoveTidy;

/// <summary>讀檔案總管「剪下／複製」時放進剪貼簿的檔案清單（只讀，不影響系統的貼上）。</summary>
public sealed record PasteClipboard(List<string> Paths, bool Cut, uint Sequence)
{
    /// <summary>剪貼簿裡沒有檔案時回傳 null。</summary>
    public static PasteClipboard? Read()
    {
        // 剪貼簿可能剛好被別的程式佔住，多試幾次
        for (int i = 0; ; i++)
        {
            try
            {
                var seq = GetClipboardSequenceNumber();
                var data = Clipboard.GetDataObject();
                if (data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return null;
                bool cut = data.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4
                           && (ms.ReadByte() & (int)DragDropEffects.Move) != 0;
                return new([.. files.Distinct(StringComparer.OrdinalIgnoreCase)], cut, seq);
            }
            catch (ExternalException) when (i < 9) { Thread.Sleep(80); }
            catch (ExternalException) { return null; }
        }
    }

    /// <summary>剪下的檔案搬完後清空剪貼簿（跟檔案總管一樣）；中途又複製了別的東西就不動它。</summary>
    public void ClearIfUnchanged()
    {
        if (!Cut || GetClipboardSequenceNumber() != Sequence) return;
        try { Clipboard.Clear(); } catch (ExternalException) { }
    }

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();
}
