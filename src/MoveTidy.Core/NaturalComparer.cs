using System.Globalization;
using System.Runtime.InteropServices;

namespace MoveTidy.Core;

/// <summary>
/// 跟檔案總管「依名稱排序」一樣的順序：IMG_2.jpg 排在 IMG_10.jpg 前面。
/// 在 Windows 上直接用檔案總管同一個排序函式。
/// </summary>
public sealed partial class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return OperatingSystem.IsWindows() ? StrCmpLogicalW(x, y) : Fallback(x, y);
    }

    static int Fallback(string x, string y)
    {
        var ci = CultureInfo.InvariantCulture.CompareInfo;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int c = a.SequenceCompareTo(b);
                if (c != 0) return c;
            }
            else
            {
                int si = i, sj = j;
                while (i < x.Length && !char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && !char.IsAsciiDigit(y[j])) j++;
                int c = ci.Compare(x, si, i - si, y, sj, j - sj, CompareOptions.IgnoreCase);
                if (c != 0) return c;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
