using System.Text.RegularExpressions;

namespace MoveTidy.Core;

public enum NamingMode { Keep, Number }

public enum NumberOrder { Name, Modified }

public enum SkipReason { None, AlreadyThere, IntoItself, Missing, SameName }

/// <summary>一個要搬的項目：從哪裡來、搬過去叫什麼。</summary>
public sealed record PlannedItem(string Source, string TargetName, bool IsDirectory, bool Renamed, SkipReason Skip)
{
    public string OriginalName => Path.GetFileName(Source);
}

/// <param name="Copy">複製：來源本來就在目的地也照樣處理（多一份加號碼的）。</param>
public sealed record PlanOptions(NamingMode Mode = NamingMode.Keep, NumberOrder Order = NumberOrder.Name, int Digits = 3, bool Copy = false);

/// <summary>決定每個項目搬到目的地後的名字，不實際動檔案。</summary>
public static partial class NamePlanner
{
    static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    [GeneratedRegex(@"^(?<stem>.*?) \((?<n>\d+)\)$")]
    private static partial Regex NumberedSuffix();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex AllDigits();

    public static IReadOnlyList<PlannedItem> Plan(IEnumerable<string> sources, string destDir, PlanOptions options)
    {
        destDir = Normalize(destDir);
        var taken = NamesIn(destDir);

        var items = new List<(string src, bool dir, SkipReason skip)>();
        var seen = new HashSet<string>(Cmp);
        foreach (var raw in sources)
        {
            var src = Normalize(raw);
            if (!seen.Add(src)) continue;
            bool dir = Directory.Exists(src);
            var skip = SkipReason.None;
            if (!dir && !File.Exists(src)) skip = SkipReason.Missing;
            else if (!options.Copy && Cmp.Equals(Path.GetDirectoryName(src) ?? "", destDir)) skip = SkipReason.AlreadyThere;
            else if (dir && (Cmp.Equals(destDir, src) || destDir.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                skip = SkipReason.IntoItself;
            items.Add((src, dir, skip));
        }

        var result = new List<PlannedItem>();
        if (options.Mode == NamingMode.Keep)
        {
            foreach (var (src, dir, skip) in items)
            {
                var name = Path.GetFileName(src);
                if (skip != SkipReason.None) { result.Add(new(src, name, dir, false, skip)); continue; }
                var target = UniqueKeepName(name, dir, taken);
                taken.Add(target);
                result.Add(new(src, target, dir, !Cmp.Equals(target, name), SkipReason.None));
            }
            return result;
        }

        var movable = items.Where(i => i.skip == SkipReason.None).ToList();
        movable.Sort((a, b) => options.Order == NumberOrder.Modified
            ? CompareByTime(a.src, a.dir, b.src, b.dir)
            : NaturalComparer.Instance.Compare(Path.GetFileName(a.src), Path.GetFileName(b.src)));

        long next = NextNumber(taken);
        int digits = Math.Clamp(options.Digits, 1, 9);
        foreach (var (src, dir, _) in movable)
        {
            var ext = dir ? "" : Path.GetExtension(src);
            string target;
            while (taken.Contains(target = next.ToString().PadLeft(digits, '0') + ext)) next++;
            next++;
            taken.Add(target);
            result.Add(new(src, target, dir, !Cmp.Equals(target, Path.GetFileName(src)), SkipReason.None));
        }
        foreach (var (src, dir, skip) in items.Where(i => i.skip != SkipReason.None))
            result.Add(new(src, Path.GetFileName(src), dir, false, skip));
        return result;
    }

    /// <summary>名字被佔用時，找最小可用的「名字 (n)」，n 從 2 開始。</summary>
    public static string UniqueKeepName(string name, bool isDirectory, ISet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        string stem = name, ext = "";
        if (!isDirectory)
        {
            var e = Path.GetExtension(name);
            if (e.Length > 0 && e.Length < name.Length) { ext = e; stem = name[..^e.Length]; }
        }
        var m = NumberedSuffix().Match(stem);
        if (m.Success && m.Groups["stem"].Value.Length > 0) stem = m.Groups["stem"].Value;
        for (int n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>目的地裡純數字名字（例如 005.jpg、12）的最大號碼加一。</summary>
    static long NextNumber(IEnumerable<string> names)
    {
        long max = 0;
        foreach (var n in names)
        {
            var stem = Path.GetFileNameWithoutExtension(n);
            if (stem.Length is > 0 and <= 18 && AllDigits().IsMatch(stem) && long.TryParse(stem, out var v) && v > max) max = v;
        }
        return max + 1;
    }

    internal static int CompareByTime(string a, bool aDir, string b, bool bDir)
    {
        var ta = aDir ? Directory.GetLastWriteTimeUtc(a) : File.GetLastWriteTimeUtc(a);
        var tb = bDir ? Directory.GetLastWriteTimeUtc(b) : File.GetLastWriteTimeUtc(b);
        int c = ta.CompareTo(tb);
        return c != 0 ? c : NaturalComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b));
    }

    /// <summary>資料夾裡現有的所有名字（不存在就是空的）。</summary>
    internal static HashSet<string> NamesIn(string dir) =>
        Directory.Exists(dir) ? new(Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!), Cmp) : new(Cmp);

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
