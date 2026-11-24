using System.Globalization;
using System.Security.Cryptography;

namespace MoveTidy.Core;

/// <summary>「自動改名」的樣式。前四種是隨機，後三種是按順序。</summary>
public enum RenameStyle
{
    /// <summary>20261124-153012-k7f2.jpg</summary>
    StampRandom,
    /// <summary>k7f2m9xq.jpg</summary>
    RandomText,
    /// <summary>4829105736.jpg</summary>
    RandomDigits,
    /// <summary>image_k7f2.jpg</summary>
    KeepRandom,
    /// <summary>20261124-153012-001.jpg</summary>
    StampNumber,
    /// <summary>貓咪-001.jpg（名字由使用者輸入）</summary>
    PrefixNumber,
    /// <summary>001.jpg</summary>
    PureNumber,
}

public sealed record RenameOptions(RenameStyle Style, NumberOrder Order = NumberOrder.Name, int Digits = 3, string Prefix = "", DateTime? Now = null);

/// <summary>留在原資料夾，只換名字。</summary>
public static class Renamer
{
    static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    // 去掉容易看錯的 0 1 i l o
    const string Letters = "23456789abcdefghjkmnpqrstuvwxyz";

    public static bool IsNumbered(RenameStyle s) => s is RenameStyle.StampNumber or RenameStyle.PrefixNumber or RenameStyle.PureNumber;

    /// <summary>決定每個項目的新名字，不實際動檔案。沒選到的檔案的名字一定避開。</summary>
    public static IReadOnlyList<PlannedItem> Plan(IEnumerable<string> sources, RenameOptions o)
    {
        var all = sources.Select(NamePlanner.Normalize).Distinct(Cmp).ToList();
        var selected = new HashSet<string>(all, Cmp);
        var items = new List<(string src, bool dir)>();
        var missing = new List<string>();
        foreach (var src in all)
        {
            bool dir = Directory.Exists(src);
            if ((dir || File.Exists(src)) && Path.GetDirectoryName(src) is not null) items.Add((src, dir));
            else missing.Add(src);
        }

        items.Sort((a, b) => IsNumbered(o.Style) && o.Order == NumberOrder.Modified
            ? NamePlanner.CompareByTime(a.src, a.dir, b.src, b.dir)
            : NaturalComparer.Instance.Compare(Path.GetFileName(a.src), Path.GetFileName(b.src)));

        // 同一批可能來自好幾個資料夾（例如搜尋結果），名字一起避開，之後搬到一起也不會重名
        var taken = new HashSet<string>(Cmp);
        foreach (var folder in items.Select(i => Path.GetDirectoryName(i.src)!).Distinct(Cmp))
            foreach (var name in NamePlanner.NamesIn(folder))
                if (!selected.Contains(Path.Combine(folder, name))) taken.Add(name);

        var stamp = (o.Now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        int digits = Math.Clamp(o.Digits, 1, 9);
        string head = o.Style switch
        {
            RenameStyle.StampNumber => stamp + "-",
            RenameStyle.PrefixNumber when CleanPrefix(o.Prefix) is { Length: > 0 } p => p + "-",
            _ => "",
        };
        long next = IsNumbered(o.Style) ? NextNumber(taken, head) : 0;

        var result = new List<PlannedItem>();
        foreach (var (src, dir) in items)
        {
            var name = Path.GetFileName(src);
            var (stem, ext) = Split(name, dir);
            string target;
            do
            {
                target = o.Style switch
                {
                    RenameStyle.StampRandom => $"{stamp}-{RandomNumberGenerator.GetString(Letters, 4)}",
                    RenameStyle.RandomText => RandomNumberGenerator.GetString(Letters, 8),
                    RenameStyle.RandomDigits => RandomNumberGenerator.GetString("123456789", 1) + RandomNumberGenerator.GetString("0123456789", 9),
                    RenameStyle.KeepRandom => $"{stem}_{RandomNumberGenerator.GetString(Letters, 4)}",
                    _ => head + (next++).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'),
                } + ext;
            } while (!taken.Add(target));
            bool same = Cmp.Equals(target, name);
            result.Add(new(src, target, dir, !same, same ? SkipReason.SameName : SkipReason.None));
        }
        foreach (var src in missing) result.Add(new(src, Path.GetFileName(src), false, false, SkipReason.Missing));
        return result;
    }

    /// <summary>去掉檔名不能用的字元（\ / : * ? " &lt; &gt; |）和頭尾空白、句點。</summary>
    public static string CleanPrefix(string? text) =>
        string.Concat((text ?? "").Split(Path.GetInvalidFileNameChars())).TrimStart().TrimEnd(' ', '.');

    static (string stem, string ext) Split(string name, bool dir)
    {
        if (dir) return (name, "");
        var ext = Path.GetExtension(name);
        return ext.Length > 0 && ext.Length < name.Length ? (name[..^ext.Length], ext) : (name, "");
    }

    /// <summary>已經有「開頭＋數字」的名字時，從最大的號碼接著編。</summary>
    static long NextNumber(IEnumerable<string> names, string head)
    {
        long max = 0;
        foreach (var n in names)
        {
            var stem = Path.GetFileNameWithoutExtension(n);
            if (!stem.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;
            var num = stem[head.Length..];
            if (num.Length is > 0 and <= 18 && num.All(char.IsAsciiDigit) && long.TryParse(num, out var v) && v > max) max = v;
        }
        return max + 1;
    }

    /// <summary>照計畫改名。</summary>
    public static MoveResult Execute(IReadOnlyList<PlannedItem> plan, IProgress<int>? progress = null, MoveJournal? journal = null)
    {
        var result = new MoveResult();
        int done = 0;
        var todo = new List<PlannedItem>();
        foreach (var item in plan)
        {
            if (item.Skip == SkipReason.None) { todo.Add(item); continue; }
            result.Skipped++;
            progress?.Report(++done);
        }

        // 資料夾和它裡面的檔案同時被選到時，先改裡面的
        todo = [.. todo.OrderByDescending(i => i.Source.Count(c => c == Path.DirectorySeparatorChar))];
        var chain = new NameChain(todo.Select(i => (i.Source, i.IsDirectory)), journal);
        foreach (var item in todo)
        {
            var folder = Path.GetDirectoryName(item.Source)!;
            try
            {
                var name = item.TargetName;
                var target = Path.Combine(folder, name);
                chain.MakeRoom(target);
                if (Path.Exists(target))
                {
                    // 計畫之後又多了同名項目：退回「名字 (n)」
                    name = NamePlanner.UniqueKeepName(name, item.IsDirectory, NamePlanner.NamesIn(folder));
                    target = Path.Combine(folder, name);
                }
                Mover.MoveOne(chain.Take(item.Source), target, item.IsDirectory);
                result.Moved++;
                result.Renamed++;
                var rec = MoveRecord.Of(item.Source, target, item.IsDirectory);
                result.Records.Add(rec);
                journal?.Moved(rec);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                chain.PutBack(item.Source, item.IsDirectory);
                result.Failures.Add(MoveFailure.From(item.Source, ex));
            }
            progress?.Report(++done);
        }
        return result;
    }
}

/// <summary>
/// 同一批裡互換名字時用（例如 002.jpg 要改成 001.jpg，而 001.jpg 自己也要改成別的）：
/// 還沒輪到的項目佔住了要用的名字，就先把它換成暫時的名字讓開。
/// </summary>
sealed class NameChain
{
    readonly Dictionary<string, bool> _waiting = new(StringComparer.OrdinalIgnoreCase);   // 還沒輪到的路徑 → 是不是資料夾
    readonly Dictionary<string, string> _parked = new(StringComparer.OrdinalIgnoreCase);  // 原路徑 → 暫時的路徑

    readonly MoveJournal? _journal;

    public NameChain(IEnumerable<(string path, bool dir)> items, MoveJournal? journal)
    {
        _journal = journal;
        foreach (var (path, dir) in items) _waiting[path] = dir;
    }

    /// <summary>如果 target 被還沒輪到的項目佔住，先請它讓開。</summary>
    public void MakeRoom(string target)
    {
        if (!_waiting.TryGetValue(target, out bool dir) || _parked.ContainsKey(target) || !Path.Exists(target)) return;
        var temp = Mover.TempName(target) + (dir ? "" : Path.GetExtension(target));
        _journal?.Parking(new(target, temp, dir));
        Mover.MoveOne(target, temp, dir);
        _parked[target] = temp;
    }

    /// <summary>這個項目現在實際在哪裡。</summary>
    public string Where(string path) => _parked.GetValueOrDefault(path, path);

    /// <summary>輪到這個項目了，回傳它現在實際的位置。</summary>
    public string Take(string path)
    {
        _waiting.Remove(path);
        return Where(path);
    }

    /// <summary>這個項目沒改成功：曾經讓開過的話，把原本的名字還給它（已被用掉就加號碼）。</summary>
    /// <returns>它最後的位置。</returns>
    public string PutBack(string path, bool dir)
    {
        _waiting.Remove(path);
        if (!_parked.Remove(path, out var temp)) return path;
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            var name = NamePlanner.UniqueKeepName(Path.GetFileName(path), dir, NamePlanner.NamesIn(folder));
            Mover.MoveOne(temp, Path.Combine(folder, name), dir);
            return Path.Combine(folder, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return temp; }
    }
}
