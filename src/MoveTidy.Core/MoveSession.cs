using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoveTidy.Core;

public enum MoveOp { Move, Copy, Rename }

/// <summary>執行一次移動／複製／改名，邊做邊留紀錄，做完寫進撤回紀錄。</summary>
public static class MoveSession
{
    public static (MoveResult Result, MoveBatch Batch) Run(IReadOnlyList<PlannedItem> plan, string dest, MoveOp op,
        IProgress<int>? progress = null, string? historyFile = null)
    {
        var batch = new MoveBatch
        {
            Kind = op == MoveOp.Rename ? BatchKind.Rename : BatchKind.Move,
            Dest = NamePlanner.Normalize(dest),
            CreatedDest = op != MoveOp.Rename && !Directory.Exists(dest),
        };
        using var journal = MoveJournal.Start(batch, historyFile);
        try
        {
            var result = op == MoveOp.Rename
                ? Renamer.Execute(plan, progress, journal)
                : Mover.Execute(plan, dest, progress, copy: op == MoveOp.Copy, journal: journal);
            return (result, batch);
        }
        finally { journal.Complete(); }   // 中途出錯也把已經做的記下來
    }

    /// <summary>撤回一批，完成後從紀錄移除。</summary>
    public static UndoResult Undo(MoveBatch batch, IProgress<int>? progress = null, string? historyFile = null)
    {
        using var journal = MoveJournal.Start(new MoveBatch { Dest = batch.Dest }, historyFile, undo: true);
        try
        {
            var result = Undoer.Execute(batch, progress, journal);
            try { MoveHistory.Remove(batch.Id, historyFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return result;
        }
        finally { journal.Complete(); }
    }
}

/// <summary>某個項目暫時換了名字（或正要換）。意外中斷時靠它放回原名。</summary>
/// <param name="Parked">暫時的位置；只是在複製、來源沒有換名時是空字串。</param>
/// <param name="Copy">目的地那邊複製到一半的暫時複本。</param>
public sealed record ParkNote(string Source, string Parked, bool IsDirectory, string? Copy = null);

/// <summary>
/// 進行中的紀錄：每一步就往 pending-*.jsonl 加一行。做完併進 history.json 後刪掉；
/// 程式意外結束時檔案會留著，下次啟動由 <see cref="Recover"/> 收拾。
/// 寫紀錄失敗不影響搬移本身。
/// </summary>
public sealed class MoveJournal : IDisposable
{
    const string Prefix = "pending-";

    sealed record Line(MoveBatch? Batch = null, MoveRecord? Moved = null, ParkNote? Park = null, bool Undo = false);

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    readonly MoveBatch _batch;
    readonly string _historyFile;
    readonly string _file;
    readonly bool _undo;
    StreamWriter? _writer;

    MoveJournal(MoveBatch batch, string historyFile, bool undo)
    {
        _batch = batch;
        _historyFile = historyFile;
        _undo = undo;
        _file = Path.Combine(Folder(historyFile), $"{Prefix}{batch.Id}.jsonl");
    }

    static string Folder(string historyFile) => Path.GetDirectoryName(Path.GetFullPath(historyFile))!;

    public static MoveJournal Start(MoveBatch batch, string? historyFile = null, bool undo = false)
    {
        var j = new MoveJournal(batch, historyFile ?? MoveHistory.DefaultFile, undo);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(j._file)!);
            // 開著的時候不給別人寫或刪：別的程式就知道這一批還在進行
            j._writer = new StreamWriter(new FileStream(j._file, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            j.Write(new Line(Batch: batch, Undo: undo));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return j;
    }

    void Write(Line line)
    {
        try { _writer?.WriteLine(JsonSerializer.Serialize(line, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>一個項目已經在目的地了。</summary>
    public void Moved(MoveRecord record)
    {
        _batch.Items.Add(record);
        Write(new Line(Moved: record));
    }

    /// <summary>正要把一個項目換成暫時的名字（先記再做）。</summary>
    public void Parking(ParkNote note) => Write(new Line(Park: note));

    /// <summary>併進撤回紀錄，刪掉進行中的紀錄（併不進去就留著，下次再併）。</summary>
    public void Complete()
    {
        if (!_undo)
        {
            try { MoveHistory.Add(_batch, _historyFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        }
        Dispose();
        try { File.Delete(_file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _writer = null;
    }

    /// <summary>
    /// 收拾意外中斷的：暫時換名的放回原名，已經搬好的併進撤回紀錄。
    /// 還在進行的（檔案被開著）不碰；收拾不完的留到下次。
    /// </summary>
    public static void Recover(string? historyFile = null)
    {
        historyFile ??= MoveHistory.DefaultFile;
        if (!Directory.Exists(Folder(historyFile))) return;
        foreach (var file in Directory.GetFiles(Folder(historyFile), Prefix + "*.jsonl"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete);
                var lines = Read(stream);
                if (lines.Count > 0 && lines[0].Batch is { } batch)
                {
                    foreach (var line in lines)
                        if (line.Moved is { } rec) batch.Items.Add(rec);
                    bool ok = true;
                    foreach (var line in lines)
                        if (line.Park is { } note) ok &= Settle(note, batch.Items);
                    if (!ok) continue;
                    if (!lines[0].Undo) MoveHistory.Add(batch, historyFile);
                }
                File.Delete(file);
            }
            catch (Exception) { } // 收拾不了就留著；壞掉的紀錄不能讓程式打不開
        }
    }

    static List<Line> Read(Stream stream)
    {
        var lines = new List<Line>();
        using var r = new StreamReader(stream, leaveOpen: true);
        while (r.ReadLine() is { } text)
        {
            try { if (JsonSerializer.Deserialize<Line>(text) is { } line) lines.Add(line); }
            catch (JsonException) { break; }   // 最後一行可能只寫到一半
        }
        return lines;
    }

    /// <summary>處理一個暫時換過名的項目，收拾好了回傳 true。</summary>
    static bool Settle(ParkNote note, List<MoveRecord> moved)
    {
        var done = moved.LastOrDefault(m => string.Equals(m.From, note.Source, StringComparison.OrdinalIgnoreCase));
        if (Path.Exists(note.Parked))
        {
            if (done is null)
            {
                // 還沒搬成：內容都在暫時的名字裡，放回原名
                if (Mover.Unpark(note.Parked, note.Source, note.IsDirectory) == note.Parked) return false;
            }
            else if (note.IsDirectory && Directory.Exists(done.To) && done.Stamp is not null && done.StillSame(done.To))
            {
                // 已經核對過、確定在目的地：把來源沒刪完的刪完
                if (!Mover.DeleteTree(note.Parked)) return true; // 刪不掉就留著，不擋後面的
            }
        }
        // 沒搬成的話，目的地的暫時複本沒用了（來源還在才刪）
        if (done is null && note.Copy is not null)
        {
            if (note.IsDirectory && Directory.Exists(note.Copy) && Directory.Exists(note.Source)) Mover.DeleteTree(note.Copy);
            else if (!note.IsDirectory && File.Exists(note.Copy) && File.Exists(note.Source)) Mover.DeleteFile(note.Copy);
        }
        return true;
    }
}
