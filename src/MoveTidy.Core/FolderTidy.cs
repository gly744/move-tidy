using System.Globalization;

namespace MoveTidy.Core;

/// <summary>
/// 「把這裡的項目移到今天日期資料夾」：在資料夾空白處按右鍵時，挑出這個資料夾裡要搬的項目。
/// </summary>
public static class FolderTidy
{
    /// <summary>
    /// 列出 <paramref name="folder"/> 裡要搬進今天日期資料夾的項目（只看第一層）。
    /// 一定略過：日期資料夾（任何一種日期格式的名字，包括今天這個）、隱藏的系統檔（desktop.ini 之類）。
    /// </summary>
    public static List<string> Collect(string folder, bool includeFolders)
    {
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            FileAttributes attr;
            try { attr = File.GetAttributes(path); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            if (attr.HasFlag(FileAttributes.Hidden) && attr.HasFlag(FileAttributes.System)) continue;
            if (attr.HasFlag(FileAttributes.Directory))
            {
                if (!includeFolders || IsDateFolderName(Path.GetFileName(path))) continue;
            }
            result.Add(path);
        }
        result.Sort(NaturalComparer.Instance);
        return result;
    }

    /// <summary>名字是不是設定裡任何一種日期格式（11-21、1121、11月21日、2026-11-21）的合法日期。</summary>
    public static bool IsDateFolderName(string name)
    {
        foreach (var f in AppSettings.DateFormats)
        {
            // 沒有年份的格式補上 2000 年（閏年），2 月 29 日才認得
            var (text, fmt) = f.Contains("yyyy") ? (name, f) : ("2000|" + name, "yyyy|" + f);
            if (DateTime.TryParseExact(text, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return true;
        }
        return false;
    }
}
