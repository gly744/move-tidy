using System.Text.Json;

namespace MoveTidy.Core;

/// <summary>搬了一個項目：原本的完整路徑 → 搬過去後的完整路徑。</summary>
/// <param name="Stamp">搬完當時的特徵（<see cref="FileStamp"/>），撤回前用來確認還是同一個。舊版的紀錄沒有。</param>
public sealed record MoveRecord(string From, string To, bool IsDirectory, string? Stamp = null)
{
    public static MoveRecord Of(string from, string to, bool isDirectory) => new(from, to, isDirectory, FileStamp.Of(to, isDirectory));

    /// <summary>在 <paramref name="path"/> 的還是當初搬的那個，沒被換掉或編輯過。</summary>
    public bool StillSame(string path) => Stamp is null || FileStamp.Matches(Stamp, path, IsDirectory);
}

public enum BatchKind { Move, Rename }

/// <summary>一次移動或改名（右鍵點一次）。</summary>
public sealed class MoveBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public BatchKind Kind { get; set; }
    public string Dest { get; set; } = "";
    public bool CreatedDest { get; set; }
    public List<MoveRecord> Items { get; set; } = [];

    public static MoveBatch From(MoveResult r, string dest, BatchKind kind = BatchKind.Move) =>
        new() { Kind = kind, Dest = NamePlanner.Normalize(dest), CreatedDest = r.CreatedDest, Items = [.. r.Records] };
}

/// <summary>
/// 最近幾次移動的紀錄，存在設定資料夾的 history.json。
/// </summary>
public static class MoveHistory
{
    public const int Keep = 20;

    public static string DefaultFile => Path.Combine(AppSettings.DataDir, "history.json");

    public static void Add(MoveBatch batch, string? file = null)
    {
        if (batch.Items.Count == 0) return;
        Update(file, list =>
        {
            if (list.Any(b => b.Id == batch.Id)) return;
            // 依開始的時間排（意外中斷的會晚一點才併進來）
            int at = list.FindLastIndex(b => b.Time <= batch.Time) + 1;
            list.Insert(at, batch);
        });
    }

    /// <summary>最近一次還沒撤回的移動，沒有則為 null。</summary>
    public static MoveBatch? Last(string? file = null)
    {
        MoveJournal.Recover(file);
        MoveBatch? last = null;
        Update(file, list => last = list.LastOrDefault(), save: false);
        return last;
    }

    public static void Remove(string id, string? file = null) =>
        Update(file, list => list.RemoveAll(b => b.Id == id));

    /// <summary>這一批只剩下 <paramref name="items"/> 還沒撤回；沒有了就移除。</summary>
    public static void Replace(string id, List<MoveRecord> items, string? file = null) =>
        Update(file, list =>
        {
            if (items.Count == 0) list.RemoveAll(b => b.Id == id);
            else if (list.Find(b => b.Id == id) is { } batch) batch.Items = items;
        });

    static void Update(string? file, Action<List<MoveBatch>> change, bool save = true)
    {
        file ??= DefaultFile;
        FileLock.Run(file, () =>
        {
            var list = Read(file);
            change(list);
            if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
            if (save) Write(file, list);
        });
    }

    static void Write(string file, List<MoveBatch> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, file, overwrite: true);
    }

    static List<MoveBatch> Read(string file)
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<List<MoveBatch>>(File.ReadAllText(file), Json) ?? [];
        }
        catch (JsonException) { }
        catch (IOException) { }
        return [];
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}

public sealed class UndoResult
{
    /// <summary>放回原位置的項目數。</summary>
    public int Restored { get; set; }
    /// <summary>原位置已有同名，只好加 (2) 的項目數。</summary>
    public int Renamed { get; set; }
    /// <summary>搬過去之後又被移走或刪掉，找不到的項目。</summary>
    public List<string> Missing { get; } = [];
    /// <summary>搬過去之後被換成別的、或編輯過的項目，沒有動它。</summary>
    public List<string> Changed { get; } = [];
    public List<MoveFailure> Failures { get; } = [];
    /// <summary>失敗、之後可以再試的項目（跟原本的順序一樣）。</summary>
    public List<MoveRecord> Remaining { get; } = [];
    /// <summary>已經放回原處，但搬過去的那邊有東西刪不掉：路徑 → 殘留所在的資料夾。</summary>
    public List<(string Source, string Left)> Leftovers { get; } = [];
}

/// <summary>把一次移動倒回去：每個項目放回原資料夾、改回原名。</summary>
public static class Undoer
{
    public static UndoResult Execute(MoveBatch batch, IProgress<int>? progress = null, MoveJournal? journal = null)
    {
        var result = new UndoResult();
        int done = 0;
        // 已經不是原來那個的，完全不碰（也不會叫它讓開）
        var changed = batch.Items.Where(r => Path.Exists(r.To) && !r.StillSame(r.To)).ToHashSet();
        var chain = new NameChain(batch.Items.Where(r => !changed.Contains(r)).Select(r => (r.To, r.IsDirectory)), journal);
        // 倒著做：如果同一批裡有東西依賴先前的步驟，倒序最安全
        for (int i = batch.Items.Count - 1; i >= 0; i--)
        {
            var rec = batch.Items[i];
            try
            {
                if (changed.Contains(rec)) { result.Changed.Add(rec.To); continue; }
                var now = chain.Where(rec.To);
                bool there = rec.IsDirectory ? Directory.Exists(now) : File.Exists(now);
                if (!there) { result.Missing.Add(rec.To); continue; }
                if (!rec.StillSame(now))
                {
                    chain.PutBack(rec.To, rec.IsDirectory);
                    result.Changed.Add(rec.To);
                    continue;
                }

                var folder = Path.GetDirectoryName(rec.From)!;
                Directory.CreateDirectory(folder);
                var name = Path.GetFileName(rec.From);
                chain.MakeRoom(rec.From);
                if (Path.Exists(rec.From))
                {
                    name = NamePlanner.UniqueKeepName(name, rec.IsDirectory, NamePlanner.NamesIn(folder));
                    result.Renamed++;
                }
                var (at, back) = (chain.Take(rec.To), Path.Combine(folder, name));
                if (Mover.MoveOne(at, back, rec.IsDirectory, () => journal?.Moved(MoveRecord.Of(at, back, rec.IsDirectory)), note => journal?.Parking(note)) is { } left)
                    result.Leftovers.Add((rec.To, left));
                result.Restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var at = chain.PutBack(rec.To, rec.IsDirectory);
                if (ex is SourceRelocatedException r) at = r.At;
                result.Failures.Add(MoveFailure.From(rec.To, ex));
                result.Remaining.Insert(0, rec with { To = at });
            }
            finally { progress?.Report(++done); }
        }

        // 這次移動才建立的資料夾（例如今天的日期資料夾），撤回後空了就刪掉
        try
        {
            if (batch.CreatedDest && Directory.Exists(batch.Dest) && !Directory.EnumerateFileSystemEntries(batch.Dest).Any())
                Directory.Delete(batch.Dest);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }
}
