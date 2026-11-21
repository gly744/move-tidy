using MoveTidy.Core;

namespace MoveTidy.Tests;

public class UndoTests
{
    static MoveBatch Move(IEnumerable<string> sources, string dest, PlanOptions? opt = null)
    {
        var r = Mover.Execute(NamePlanner.Plan(sources, dest, opt ?? new()), dest);
        return MoveBatch.From(r, dest);
    }

    [Fact]
    public void Undo_NumberedNames_GoBackWithOriginalNames()
    {
        using var t = new TempDir();
        var a = t.File("src/海邊.jpg", "a"); var b = t.File("src/山上.png", "b");
        t.File("src/相簿/1.jpg");
        var dest = Path.Combine(t.Root, "dest", "11-21");

        var batch = Move([a, b, Path.Combine(t.Root, "src", "相簿")], dest, new(NamingMode.Number));
        Assert.True(batch.CreatedDest);
        Assert.Equal(3, batch.Items.Count);
        Assert.False(File.Exists(a));

        var r = Undoer.Execute(batch);
        Assert.Equal(3, r.Restored);
        Assert.Equal("a", File.ReadAllText(a));
        Assert.Equal("b", File.ReadAllText(b));
        Assert.True(File.Exists(Path.Combine(t.Root, "src", "相簿", "1.jpg")));
        Assert.False(Directory.Exists(dest)); // 這次才建立的日期資料夾，空了就刪掉
    }

    [Fact]
    public void Undo_KeepsExistingDestFolder_AndHandlesClashesAndMissing()
    {
        using var t = new TempDir();
        var dest = t.Dir("dest");
        t.File("dest/舊的.txt");
        var a = t.File("src/a.txt", "old"); var b = t.File("src/b.txt");
        var batch = Move([a, b], dest);
        Assert.False(batch.CreatedDest);

        t.File("src/a.txt", "new");                       // 原處又出現同名檔
        File.Delete(Path.Combine(dest, "b.txt"));         // 搬過去的被刪了

        var r = Undoer.Execute(batch);
        Assert.Equal(1, r.Restored);
        Assert.Equal(1, r.Renamed);
        Assert.Single(r.Missing);
        Assert.Equal("new", File.ReadAllText(a));
        Assert.Equal("old", File.ReadAllText(Path.Combine(t.Root, "src", "a (2).txt")));
        Assert.True(Directory.Exists(dest));              // 本來就有的資料夾不刪
    }

    [Fact]
    public void Undo_FolderTidy_150Items()
    {
        using var t = new TempDir();
        for (int i = 1; i <= 150; i++) t.File($"p{i}.jpg");
        t.Dir("11-20");
        var dest = Path.Combine(t.Root, "11-21");
        var batch = Move(FolderTidy.Collect(t.Root, true), dest);

        var r = Undoer.Execute(batch);
        Assert.Equal(150, r.Restored);
        Assert.Equal(151, Directory.EnumerateFileSystemEntries(t.Root).Count()); // 150 張 + 11-20
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void History_KeepsLast20_LastAndRemove()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "history.json");
        Assert.Null(MoveHistory.Last(file));
        for (int i = 0; i < 25; i++)
            MoveHistory.Add(new MoveBatch { Dest = $"D{i}", Items = [new("a", "b", false)] }, file);
        MoveHistory.Add(new MoveBatch { Dest = "空的" }, file);  // 沒搬到東西的不記

        var last = MoveHistory.Last(file)!;
        Assert.Equal("D24", last.Dest);
        MoveHistory.Remove(last.Id, file);
        Assert.Equal("D23", MoveHistory.Last(file)!.Dest);

        int count = 0;
        while (MoveHistory.Last(file) is { } b) { MoveHistory.Remove(b.Id, file); count++; }
        Assert.Equal(MoveHistory.Keep - 1, count);
    }
}
