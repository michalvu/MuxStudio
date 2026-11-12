using System.Text;
using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>Thùng rác giả: ghi lại đường dẫn rồi xoá thật (= <c>FakeTrash</c> của test Python).</summary>
public sealed class FakeTrash
{
    public List<string> Items { get; } = [];

    public bool Call(string path, bool toRecycleBin = true)
    {
        Items.Add(path);
        File.Delete(path);
        return true;
    }
}

/// <summary>Nhật ký + hoàn tác (cả ba bước), Thùng rác, đọc nhật ký cũ của .bat. Port <c>tests/test_undo.py</c>.</summary>
[Collection(GlobalStateCollection.Name)]
public class UndoTests
{
    private static string Write(string p, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text, Py.Utf8NoBom);
        return p;
    }

    private static readonly UTF8Encoding Utf8Sig = new(true);

    [Fact]
    public void Runlog_csv_format_and_rotation()
    {
        using var tmp = new TempDir();
        var movieDir = tmp.MovieDir();
        for (var i = 0; i < 23; i++)
        {
            var log = new RunLog(movieDir, "_subprep_logs", "subprep", keep: 20);
            log.Add(Undo.Rename, Path.Combine(movieDir, "a.srt"), Path.Combine(movieDir, "b.srt"), "S01E01");
            log.Save(now: new DateTime(2026, 1, 1, 10, 0, i));
        }
        var files = Directory.GetFiles(Path.Combine(movieDir, "_subprep_logs")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(20, files.Count);
        Assert.Equal("run_20260101_100003.csv", files[0]);
        var raw = File.ReadAllBytes(Path.Combine(movieDir, "_subprep_logs", files[^1]!));
        Assert.True(raw.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])); // UTF-8 có BOM như Export-Csv
        var firstLine = Encoding.UTF8.GetString(raw[3..]).Split("\r\n")[0];
        Assert.Equal("\"Time\",\"Action\",\"From\",\"To\",\"Detail\",\"Status\"", firstLine);
        Assert.Null(new RunLog(movieDir, "_x", "mux").Save()); // không có gì -> không ghi
    }

    [Fact]
    public void Same_second_does_not_overwrite()
    {
        using var tmp = new TempDir();
        var movieDir = tmp.MovieDir();
        var now = new DateTime(2026, 1, 1, 10, 0, 0);
        var paths = new List<string?>();
        for (var k = 0; k < 2; k++)
        {
            var log = new RunLog(movieDir, "_muxtool_logs", "mux");
            log.Add(Undo.Create, "", Path.Combine(movieDir, "o.mkv"));
            paths.Add(log.Save(now: now));
        }
        Assert.NotEqual(paths[0], paths[1]);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Undo_create_copy_move_rename_mkdir()
    {
        using var tmp = new TempDir();
        var movieDir = tmp.MovieDir();
        var trash = new FakeTrash();
        var sub = Path.Combine(movieDir, "srt_can_sync", "vi");
        var outDir = Path.Combine(movieDir, "Output", "Phim (2020) [tmdbid-1]");
        var created = Write(Path.Combine(outDir, "Phim (2020) [tmdbid-1].mkv"));
        var copied = Write(Path.Combine(sub, "[Judas] Show - 01.vi.srt"));
        var movedFrom = Path.Combine(sub, "goc.srt");
        var movedTo = Write(Path.Combine(sub, "moi.srt"), "noi dung goc");
        var renFrom = Path.Combine(sub, "ten cu [1080p].srt");
        var renTo = Write(Path.Combine(sub, "Show.S01E02.vi.srt"), "doi ten");
        var log = new RunLog(movieDir, "_subfetch_logs", "subfetch");
        log.Add(Undo.Mkdir, "", outDir);
        log.Add(Undo.Create, "", created);
        log.Add(Undo.Copy, Path.Combine(sub, "nguon.srt"), copied);
        log.Add(Undo.Move, movedFrom, movedTo);
        log.Add(Undo.Rename, renFrom, renTo);
        var lp = log.Save()!;

        var runs = Undo.ListRuns(movieDir, "_subfetch_logs", "subfetch");
        Assert.Single(runs);
        Assert.True(runs[0].Undoable);
        Assert.Equal(1, runs[0].Counts[Undo.Create]);

        var plan = Undo.PlanUndo(lp);
        Assert.Equal(["trash", "trash", "rename_back", "move_back", "rmdir"], plan.Ops.Select(o => o.Kind));
        var res = Undo.ExecuteUndo(plan, trashFn: trash.Call);
        Assert.Equal(new HashSet<string> { created, copied }, trash.Items.ToHashSet());
        Assert.Equal("noi dung goc", File.ReadAllText(movedFrom));
        Assert.False(File.Exists(movedTo));
        Assert.Equal("doi ten", File.ReadAllText(renFrom));
        Assert.False(File.Exists(renTo));
        Assert.False(Directory.Exists(outDir));
        Assert.False(File.Exists(lp)); // nhật ký đã hoàn tác -> bỏ
        Assert.Empty(res.Skipped);
    }

    [Fact]
    public void Undo_rename_conflict_is_skipped_not_overwritten()
    {
        using var tmp = new TempDir();
        var movieDir = tmp.MovieDir();
        var a = Write(Path.Combine(movieDir, "a.srt"), "cu");
        var b = Write(Path.Combine(movieDir, "b.srt"), "moi");
        var log = new RunLog(movieDir, "_subprep_logs", "subprep");
        log.Add(Undo.Rename, a, b);
        var res = Undo.ExecuteUndo(Undo.PlanUndo(log.Save()!), trashFn: new FakeTrash().Call);
        Assert.Equal("cu", File.ReadAllText(a));
        Assert.True(File.Exists(b));
        Assert.NotEmpty(res.Skipped);
    }

    [Fact]
    public void Missing_files_reported()
    {
        using var tmp = new TempDir();
        var movieDir = tmp.MovieDir();
        var log = new RunLog(movieDir, "_subfetch_logs", "subfetch");
        log.Add(Undo.Create, "", Path.Combine(movieDir, "da-xoa.srt"));
        var plan = Undo.PlanUndo(log.Save()!);
        Assert.Equal([Path.Combine(movieDir, "da-xoa.srt")], plan.Missing);
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void Reads_old_bat_logs()
    {
        using var tmp = new TempDir();
        var movieDir = tmp.MovieDir();
        var d = Directory.CreateDirectory(Path.Combine(movieDir, "_subfetch_logs")).FullName;
        var made = Write(Path.Combine(movieDir, "srt_can_sync", "vi", "x.vi.srt"));
        File.WriteAllText(Path.Combine(d, "actions_20250101_120000.csv"),
            "\"Time\",\"Action\",\"From\",\"To\"\r\n\"10:00:00\",\"Create\",\"Release.Name\",\"" + made + "\"\r\n", Utf8Sig);
        var runs = Undo.ListRuns(movieDir, "_subfetch_logs", "subfetch");
        Assert.True(runs[0].Undoable);
        var trash = new FakeTrash();
        Undo.ExecuteUndo(Undo.PlanUndo(runs[0].Path), trashFn: trash.Call);
        Assert.Equal([made], trash.Items);

        var d2 = Directory.CreateDirectory(Path.Combine(movieDir, "_subprep_logs")).FullName;
        File.WriteAllText(Path.Combine(d2, "run_20250101_120000.csv"),
            "\"Time\",\"Action\",\"From\",\"To\",\"Detail\"\r\n\"10:00:00\",\"Encoding\",\"a.srt\",\"\",\"cp1258 -> utf8\"\r\n" +
            "\"10:00:01\",\"Delete\",\"b.srt\",\"\",\"Thung rac\"\r\n", Utf8Sig);
        var plan = Undo.PlanUndo(Path.Combine(d2, "run_20250101_120000.csv"));
        Assert.Empty(plan.Ops);
        Assert.Equal(2, plan.Notes.Count);

        var d3 = Directory.CreateDirectory(Path.Combine(movieDir, "_muxtool_logs")).FullName;
        File.WriteAllText(Path.Combine(d3, "mux_20250101_120000.csv"),
            "\"Time\",\"File\",\"Status\",\"Output\",\"Detail\"\r\n\"10:00:00\",\"a.mkv\",\"OK\",\"a\",\"x\"\r\n", Utf8Sig);
        var runs3 = Undo.ListRuns(movieDir, "_muxtool_logs", "mux");
        Assert.False(runs3[0].Undoable);
        Assert.Contains("cũ", runs3[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_many_runs_newest_first()
    {
        using var tmp = new TempDir();
        var movieDir = tmp.MovieDir();
        var f1 = Write(Path.Combine(movieDir, "1.srt"));
        var f2 = Write(Path.Combine(movieDir, "2.srt"));
        var i = 0;
        foreach (var f in new[] { f1, f2 })
        {
            var log = new RunLog(movieDir, "_subfetch_logs", "subfetch");
            log.Add(Undo.Create, "", f);
            log.Save(now: new DateTime(2026, 1, 1, 10, 0, i++));
        }
        var trash = new FakeTrash();
        var paths = Undo.ListRuns(movieDir, "_subfetch_logs", "subfetch").Select(r => r.Path);
        Undo.UndoRuns(paths, trashFn: trash.Call);
        Assert.Equal([f2, f1], trash.Items);
    }

    [Fact]
    public void Trash_uses_recycle_bin_sender()
    {
        var old = Undo.SendToRecycleBin;
        var called = new List<string>();
        try
        {
            Undo.SendToRecycleBin = called.Add;
            using var tmp = new TempDir();
            var f = Write(tmp["[x] a.srt"]);
            Assert.True(Undo.Trash(f));
            Assert.Equal([f], called);
            Assert.True(File.Exists(f)); // đã "giao" cho Thùng rác giả, không tự xoá
        }
        finally
        {
            Undo.SendToRecycleBin = old;
        }
    }

    [Fact]
    public void Trash_failure_does_not_delete_permanently()
    {
        var old = Undo.SendToRecycleBin;
        try
        {
            Undo.SendToRecycleBin = _ => throw new IOException("khong co Thung rac");
            using var tmp = new TempDir();
            var f = Write(tmp["a.srt"]);
            Assert.False(Undo.Trash(f));
            Assert.True(File.Exists(f));
            Assert.True(Undo.Trash(f, toRecycleBin: false));
            Assert.False(File.Exists(f));
            Assert.False(Undo.Trash(tmp["khong-co"]));
        }
        finally
        {
            Undo.SendToRecycleBin = old;
        }
    }

    // ---- thêm cho bản C#: bộ đọc CSV giống csv của Python ----
    [Fact]
    public void Csv_reader_matches_python_quirks()
    {
        // ô có xuống dòng + ngoặc kép kép, dòng trống bị bỏ, thiếu ô -> null, \r đơn lẻ cũng là xuống dòng
        var rows = PyCsv.DictRows("\"A\",\"B\",\"C\"\r\n\"x\r\ny\",\"say \"\"hi\"\"\",z\r\n\r\n1,2\r3,4,5,6\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal("x\r\ny", rows[0]["A"]);
        Assert.Equal("say \"hi\"", rows[0]["B"]);
        Assert.Equal("z", rows[0]["C"]);
        Assert.Equal("1", rows[1]["A"]);
        Assert.Null(rows[1]["C"]);
        Assert.Equal("5", rows[2]["C"]);
        // strict=False: ký tự sau dấu đóng ngoặc nối vào ô
        Assert.Equal(["ab c", "d"], PyCsv.Rows("\"ab\" c,d")[0]);
    }
}
