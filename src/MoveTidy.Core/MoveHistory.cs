using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoveTidy.Core;

/// <summary>搬了一個項目：原本的完整路徑 → 搬過去後的完整路徑。</summary>
public sealed record MoveRecord(string From, string To, bool IsDirectory);

/// <summary>一次移動（右鍵點一次）。</summary>
public sealed class MoveBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string Dest { get; set; } = "";
    public bool CreatedDest { get; set; }
    public List<MoveRecord> Items { get; set; } = [];

    public static MoveBatch From(MoveResult r, string dest) =>
        new() { Dest = NamePlanner.Normalize(dest), CreatedDest = r.CreatedDest, Items = [.. r.Records] };
}

/// <summary>
/// 最近幾次移動的紀錄，存在設定資料夾的 history.json。
/// 好幾個程式可能同時讀寫（右鍵連點），用具名 Mutex 排隊。
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
            list.Add(batch);
            if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
        });
    }

    /// <summary>最近一次還沒撤回的移動，沒有則為 null。</summary>
    public static MoveBatch? Last(string? file = null)
    {
        MoveBatch? last = null;
        Update(file, list => last = list.LastOrDefault(), save: false);
        return last;
    }

    public static void Remove(string id, string? file = null) =>
        Update(file, list => list.RemoveAll(b => b.Id == id));

    static void Update(string? file, Action<List<MoveBatch>> change, bool save = true)
    {
        file ??= DefaultFile;
        using var mutex = new Mutex(false, @"Local\MoveTidy-history-" +
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(file.ToLowerInvariant())))[..16]);
        try { mutex.WaitOne(5000); } catch (AbandonedMutexException) { }
        try
        {
            var list = Read(file);
            change(list);
            if (!save) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
            File.Move(tmp, file, overwrite: true);
        }
        finally { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } }
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
    public List<MoveFailure> Failures { get; } = [];
}

/// <summary>把一次移動倒回去：每個項目放回原資料夾、改回原名。</summary>
public static class Undoer
{
    public static UndoResult Execute(MoveBatch batch, IProgress<int>? progress = null)
    {
        var result = new UndoResult();
        int done = 0;
        // 倒著做：如果同一批裡有東西依賴先前的步驟，倒序最安全
        for (int i = batch.Items.Count - 1; i >= 0; i--)
        {
            var rec = batch.Items[i];
            try
            {
                bool there = rec.IsDirectory ? Directory.Exists(rec.To) : File.Exists(rec.To);
                if (!there) { result.Missing.Add(rec.To); continue; }

                var folder = Path.GetDirectoryName(rec.From)!;
                Directory.CreateDirectory(folder);
                var name = Path.GetFileName(rec.From);
                if (Path.Exists(rec.From))
                {
                    var taken = new HashSet<string>(Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);
                    name = NamePlanner.UniqueKeepName(name, rec.IsDirectory, taken);
                    result.Renamed++;
                }
                Mover.MoveOne(rec.To, Path.Combine(folder, name), rec.IsDirectory);
                result.Restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failures.Add(new(rec.To, ex.Message, ex is UnauthorizedAccessException ? 5 : ex.HResult & 0xFFFF));
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
