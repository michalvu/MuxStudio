using System.Diagnostics;
using System.Text;
using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>
/// §6.1 tiến trình con + đường dẫn + tìm công cụ. Port phần <c>tools</c> của <c>tests/test_tools_probe.py</c> và
/// <c>test_unsupported_alass_flags</c> (<c>tests/test_subprep.py</c>, phần không cần PrepSession). <c>python -c ...</c> được thay
/// bằng exe con <see cref="Child"/>.
/// </summary>
public class ToolsTests
{
    // ---------------------------------------------------------------- run
    [Fact]
    public void Run_uses_argument_list_no_shell()
    {
        var psi = Tools.StartInfo(["prog.exe", "a b", "[YTS.MX] x\"y", ""], cwd: null);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal("prog.exe", psi.FileName);
        Assert.Equal(["a b", "[YTS.MX] x\"y", ""], psi.ArgumentList);
        Assert.Equal("", psi.Arguments);

        // con in UTF-8 như mkvmerge / ffprobe (không phụ thuộc bảng mã ANSI của máy, vd cp1250: không có "à")
        var r = Tools.Run(Child.Cmd("print", "xin chào [YTS.MX]"));
        Assert.Equal(0, r.Code);
        Assert.Equal("xin chào [YTS.MX]", r.Stdout.Trim());

        // tham số tới nơi nguyên vẹn: dấu cách, [ ], ngoặc kép, rỗng, \ cuối, tiếng Việt
        string[] args = ["a b", "[YTS.MX] Phim (2020)", "x\"y", "", @"C:\thư mục\", "tiếng Việt"];
        var a = Tools.Run(Child.Cmd("args", args));
        Assert.Equal(args, a.Stdout.Split('\n')[..^1]);
    }

    [Fact]
    public void Run_decodes_utf8_with_replace()
    {
        var hex = Convert.ToHexString(Encoding.ASCII.GetBytes("Chyba: soubor ")) + "C5A120FF";
        var r = Tools.Run(Child.Cmd("hex", hex));
        Assert.Contains("š", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("\uFFFD", r.Stdout, StringComparison.Ordinal);
        // như decode("utf-8"): không tự nhận BOM / đổi sang UTF-16
        Assert.Equal("\uFEFFA", Tools.Run(Child.Cmd("hex", "EFBBBF41")).Stdout);
        Assert.Equal("\uFFFD\uFFFDA\0", Tools.Run(Child.Cmd("hex", "FFFE4100")).Stdout);
    }

    [Fact]
    public void Run_missing_program_and_timeout()
    {
        Assert.Equal(127, Tools.Run(["khong-co-chuong-trinh-nay-123"]).Code);
        var sw = Stopwatch.StartNew();
        var r = Tools.Run(Child.Cmd("sleep", "5"), timeout: TimeSpan.FromSeconds(0.5));
        Assert.Equal(-9, r.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), sw.Elapsed.ToString());
    }

    [Fact]
    public void Run_exit_code_is_unsigned_like_python_and_output_joins_streams()
    {
        // 0xC0000135 (thiếu DLL): Python đọc 3221225781 -> "code <= 1" (mkv_json) phải là SAI
        Assert.Equal(3221225781L, Tools.Run(Child.Cmd("exit", "-1073741515")).Code);
        Assert.Equal(3, Tools.Run(Child.Cmd("exit", "3")).Code);
        var e = Tools.Run(Child.Cmd("stderr", "lỗi rồi"));
        Assert.Equal(("", "lỗi rồi"), (e.Stdout, e.Stderr.Trim()));
        Assert.Equal("lỗi rồi", e.Output);
        Assert.Equal("a\n\nb", new RunResult(0, "a\n", "b\n").Output); // stdout + "\n" + stderr, rồi strip()
        Assert.Equal("a", new RunResult(0, "\x1c a \x1f", "").Output);
    }

    [Fact]
    public void Cancel_now_kills_child_process()
    {
        var job = new Job();
        string? res = null;
        var t = new Thread(() =>
        {
            try
            {
                Tools.Run(Child.Cmd("sleep", "30"), job);
                res = "xong";
            }
            catch (Cancelled)
            {
                res = "dung";
            }
        });
        t.Start();
        WaitUntil(() => job.RunningCount > 0);
        var sw = Stopwatch.StartNew();
        job.CancelNow();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal("dung", res);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.Throws<Cancelled>(() => Tools.Run(Child.Cmd("print", "x"), job)); // đã dừng: không mở tiến trình mới
    }

    [Fact]
    public void Cancel_now_kills_whole_process_tree()
    {
        using var tmp = new TempDir();
        var pidFile = tmp["chau.pid"];
        var job = new Job();
        var t = new Thread(() =>
        {
            try
            {
                Tools.Run(Child.Cmd("tree", "30", pidFile), job);
            }
            catch (Cancelled)
            {
            }
        });
        t.Start();
        WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
        var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        job.CancelNow();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)));
        WaitUntil(() => !IsRunning(pid));
        Assert.False(IsRunning(pid)); // ffmpeg do alass mở cũng phải chết theo
    }

    [Fact]
    public void Cancellation_token_passed_to_job_cancels_run()
    {
        using var cts = new CancellationTokenSource();
        using var job = new Job(cancellationToken: cts.Token);
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                Tools.Run(Child.Cmd("sleep", "30"), job);
            }
            catch (Exception e)
            {
                err = e;
            }
        });
        t.Start();
        WaitUntil(() => job.RunningCount > 0);
        cts.Cancel();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<Cancelled>(err);
        Assert.True(job.StopNowRequested && job.ShouldStop());
        Assert.IsAssignableFrom<OperationCanceledException>(err); // catch (OperationCanceledException) bắt được
    }

    [Fact]
    public void Job_log_and_redact()
    {
        var outp = new List<LogLine>();
        var prog = new List<JobProgress>();
        var job = new Job(new SyncProgress<LogLine>(outp.Add), new SyncProgress<JobProgress>(prog.Add), s => s.Replace("BIMAT", "•••", StringComparison.Ordinal));
        job.Warn("key=BIMAT");
        Assert.Equal([new LogLine("warn", "key=•••")], outp);
        Assert.Equal(outp, job.Lines);
        job.Progress(1, 3, "a.mkv");
        Assert.Equal([new JobProgress(1, 3, "a.mkv")], prog);
        foreach (var (f, lv) in new (Action<string>, string)[] { (job.Info, "info"), (job.Ok, "ok"), (job.Error, "error"), (job.Dim, "dim"), (job.Head, "head") })
        {
            f("x");
            Assert.Equal(lv, job.Lines[^1].Level);
        }
        // dừng sau file đang chạy: should_stop đúng nhưng check() không ném
        Assert.False(job.ShouldStop());
        job.StopAfterCurrent();
        Assert.True(job.ShouldStop() && job.StopAfterCurrentRequested && !job.StopNowRequested);
        job.Check();
        Job.Null.Info("không ai nghe"); // NULL_JOB: không lỗi
    }

    // ---------------------------------------------------------------- duong dan
    [Fact]
    public void Walk_files_brackets_and_skip()
    {
        using var tmp = new TempDir();
        var root = tmp["[YTS.MX] Phim (2020)"];
        Directory.CreateDirectory(Path.Combine(root, "Output"));
        Directory.CreateDirectory(Path.Combine(root, "Season [01]"));
        File.WriteAllBytes(Path.Combine(root, "a [1080p].mkv"), []);
        File.WriteAllBytes(Path.Combine(root, "Season [01]", "b.MKV"), []);
        File.WriteAllBytes(Path.Combine(root, "Output", "c.mkv"), []);
        File.WriteAllBytes(Path.Combine(root, "d.txt"), []);
        var got = Tools.WalkFiles(root, true, [".mkv"], [Path.Combine(root, "Output")]);
        Assert.Equal(["a [1080p].mkv", "b.MKV"], got.Select(Path.GetFileName));
        Assert.Equal(["a [1080p].mkv"], Tools.WalkFiles(root, false, [".mkv"]).Select(Path.GetFileName));
        Assert.True(Tools.IsUnder(Path.Combine(root, "Output", "x"), Path.Combine(root, "Output")));
        Assert.False(Tools.IsUnder(Path.Combine(root, "Outputs"), Path.Combine(root, "Output")));

        // như str(Path(...)): '/' -> '\', bỏ '.' và dấu tách cuối; exts rỗng = mọi file; thư mục không có -> []
        var fwd = root.Replace('\\', '/') + "/./";
        Assert.Equal(Path.Combine(root, "a [1080p].mkv"), Tools.WalkFiles(fwd, false, [".MKV"])[0]);
        Assert.Equal(4, Tools.WalkFiles(root, true, []).Count);
        Assert.Empty(Tools.WalkFiles(tmp["khong-co"]));
        Assert.Empty(Tools.WalkFiles(root, true, null, [root])); // bỏ qua chính gốc
    }

    [Fact]
    public void Walk_files_follows_junction_like_os_walk()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp["real", "sub"]);
        File.WriteAllBytes(tmp["real", "sub", "x.mkv"], []);
        Directory.CreateDirectory(tmp["lib"]);
        File.WriteAllBytes(tmp["lib", "a.mkv"], []);
        var mk = Tools.Run(["cmd.exe", "/c", "mklink", "/J", tmp["lib", "jn"], tmp["real"]]);
        Assert.Equal(0, mk.Code);
        Assert.False(Py.IsLink(tmp["lib", "jn"])); // junction không phải symlink (os.path.islink == False)
        Assert.Equal([tmp["lib", "a.mkv"], tmp["lib", "jn", "sub", "x.mkv"]], Tools.WalkFiles(tmp["lib"], true, [".mkv"]));
        Directory.Delete(tmp["lib", "jn"]); // xoá junction, không đụng vào đích
    }

    [Fact]
    public void Copy_file_brackets()
    {
        using var tmp = new TempDir();
        var src = tmp["[Judas] a.srt"];
        File.WriteAllText(src, "x", Py.Utf8NoBom);
        File.SetLastWriteTimeUtc(src, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var dst = tmp["srt_can_sync", "vi", "[Judas] b [1080p].vi.srt"];
        Tools.CopyFile(src, dst);
        Assert.Equal("x", File.ReadAllText(dst, Py.Utf8Strict));
        Assert.True(File.GetLastWriteTimeUtc(dst).Year > 2000); // shutil.copyfile chỉ chép nội dung
        File.WriteAllText(src, "yy", Py.Utf8NoBom);
        Tools.CopyFile(src, dst); // chép đè
        Assert.Equal("yy", File.ReadAllText(dst, Py.Utf8Strict));
        Assert.Throws<IOException>(() => Tools.CopyFile(src, src.ToUpperInvariant())); // SameFileError, file nguồn còn nguyên
        Assert.Equal("yy", File.ReadAllText(src, Py.Utf8Strict));
    }

    [Fact]
    public void Join_safe_same_as_python() =>
        PyCases.Check("join_safe", x =>
        {
            var a = (List<object?>)x!;
            return Tools.JoinSafe((string?)a[0], (string)a[1]!);
        });

    // ---------------------------------------------------------------- tim cong cu
    [Fact]
    public void Find_tool_order()
    {
        using var tmp = new TempDir();
        var cfg = new Config();
        var mkv = tmp["mkvtoolnix"];
        Directory.CreateDirectory(mkv);
        var exe = Path.Combine(mkv, Tools.ExeName("mkvmerge"));
        File.WriteAllBytes(exe, []);
        cfg["MkvToolNixDir"] = mkv;
        Assert.Equal(exe, Tools.FindTool("mkvmerge", cfg));
        var explicitExe = tmp["khac", Tools.ExeName("mkvmerge")];
        Directory.CreateDirectory(tmp["khac"]);
        File.WriteAllBytes(explicitExe, []);
        cfg["MkvmergePath"] = explicitExe;
        Assert.Equal(explicitExe, Tools.FindTool("mkvmerge", cfg));
        Assert.Equal(exe, Tools.FindTool("mkvmerge", cfg, useConfigPath: false));
        // ffmpeg/ffprobe tim canh alass truoc (Find-Near)
        var near = tmp["alass"];
        Directory.CreateDirectory(near);
        File.WriteAllBytes(Path.Combine(near, Tools.ExeName("ffprobe")), []);
        var empty = new Config([new("AlassDir", ""), new("MkvToolNixDir", ""), new("ExtraToolDirs", new List<object?>())]);
        Assert.Equal(Path.Combine(near, Tools.ExeName("ffprobe")), Tools.FindTool("ffprobe", empty, near: near));
    }

    [Fact]
    public void Find_all_tools_looks_near_alass_and_search_dirs_dedupe()
    {
        using var tmp = new TempDir();
        var al = tmp["[al] ass"];
        Directory.CreateDirectory(al);
        foreach (var n in new[] { "alass", "ffmpeg" })
            File.WriteAllBytes(Path.Combine(al, Tools.ExeName(n)), []);
        var cfg = new Config([new("AlassDir", al), new("MkvToolNixDir", tmp["khong-co"]), new("ExtraToolDirs", new List<object?>())]);
        var found = Tools.FindAllTools(cfg);
        Assert.Equal(["mkvmerge", "mkvextract", "alass", "ffmpeg", "ffprobe"], found.Keys);
        Assert.Equal(Path.Combine(al, "alass.exe"), found["alass"]);
        Assert.Equal(Path.Combine(al, "ffmpeg.exe"), found["ffmpeg"]);

        var c2 = new Config([new("AlassDir", @"C:\X"), new("ExtraToolDirs", new List<object?> { "c:/x", @"C:\Y", "" })]);
        var dirs = Tools.ToolSearchDirs("ffmpeg", c2, targetDir: tmp.Path, near: @"C:\Near");
        Assert.Equal([@"C:\Near", @"C:\X", @"C:\Y"], dirs.Take(3));
        Assert.Contains(tmp.Path, dirs);
        Assert.Contains(Path.Combine(tmp.Path, "alass"), dirs);
        Assert.Equal(dirs.Count, dirs.Select(Py.NormCase).Distinct().Count());
        Assert.DoesNotContain(Tools.ToolSearchDirs("mkvmerge", c2), d => d.EndsWith("alass", StringComparison.Ordinal));
    }

    [Fact]
    public void Mkvmerge_flag_names()
    {
        Assert.Equal(("--default-track-flag", "--forced-display-flag"), Tools.MkvmergeFlagNames(74));
        Assert.Equal(("--default-track", "--forced-track"), Tools.MkvmergeFlagNames(73));
        PyCases.Check("flag_names", x =>
        {
            var (a, b) = Tools.MkvmergeFlagNames((int)(long)x!);
            return PyData.L(a, b);
        });
    }

    [Fact]
    public void Unsupported_alass_flags()
    {
        var fl = Tools.AlassSupportedFlags(["x"], ["--no-split", "--split-penalty", "7", "--disable-fps-guessing"],
                                           helpText: "USAGE\n --no-split\n --Disable-FPS-Guessing");
        Assert.Equal(["--no-split", "--disable-fps-guessing"], fl.Ok);
        Assert.Equal(["--split-penalty"], fl.Unsupported);
        Assert.Equal(["--a", "1"], Tools.AlassSupportedFlags(["x"], ["--a", "1"], helpText: "").Ok); // khong doc duoc --help
        // đọc --help thật từ alass giả
        var real = Tools.AlassSupportedFlags(Child.Cmd("alass"), ["--no-split", "--speed", "3", "--disable-fps-guessing"]);
        Assert.Equal(["--no-split", "--disable-fps-guessing"], real.Ok);
        Assert.Equal(["--speed"], real.Unsupported);
        Assert.True(real.HelpRead);
    }

    [Fact]
    public void Alass_flags_same_as_python() =>
        PyCases.Check("alass_flags", x =>
        {
            var a = (List<object?>)x!;
            var f = Tools.AlassSupportedFlags(["x"], ((List<object?>)a[1]!).Cast<string>(), helpText: (string)a[0]!);
            return PyData.L(f.Ok.Cast<object?>().ToList(), f.Unsupported.Cast<object?>().ToList(), f.HelpRead);
        });

    // ---------------------------------------------------------------- "Cong cu ngoai" trong Cai dat (tool_info)
    [Fact]
    public void Tool_info_found_missing_broken_and_wrong_file()
    {
        using var tmp = new TempDir();
        var i = Tools.GetToolInfo("alass", Child.Cmd("alass")); // lenh = danh sach (alass gia)
        Assert.True(i.Ok);
        Assert.Equal("2.0.0", i.Version);
        Assert.Equal("2.0.0", i.Text);
        Assert.Equal("không tìm thấy", Tools.GetToolInfo("alass", (string?)null).Text);
        Assert.Equal("không có file này", Tools.GetToolInfo("ffprobe", tmp["không có.exe"]).Problem);
        var bad = tmp["[x] hỏng.exe"];
        File.WriteAllText(bad, "không phải chương trình", Py.Utf8NoBom);
        var b = Tools.GetToolInfo("ffprobe", bad);
        Assert.False(b.Ok);
        Assert.Contains("KHÔNG chạy được", b.Problem, StringComparison.Ordinal);
        Assert.Contains("Unblock", b.Problem, StringComparison.Ordinal);
        Assert.Equal(Tools.ToolNames.Order(), Tools.ToolRoles.Keys.Order());
        Assert.True(Tools.ToolRoles["mkvmerge"].Required && Tools.ToolRoles["alass"].Required);
    }

    [Fact]
    public void Tool_info_with_real_tools_if_installed()
    {
        // PATH, rồi như nút "Tự tìm" (trên Windows mkvmerge thường ở Desktop\mkvtoolnix, KHÔNG có trong PATH)
        var real = new[] { "mkvmerge", "mkvextract", "ffmpeg", "ffprobe" }.ToDictionary(n => n, n => Py.Which(n) ?? Tools.FindTool(n, new Config()));
        if (real.Values.Any(v => v is null))
            return; // = pytest.skip("thiếu mkvmerge / ffmpeg thật")
        Assert.StartsWith("v", Tools.GetToolInfo("mkvmerge", real["mkvmerge"]).Version, StringComparison.Ordinal);
        Assert.True(Tools.GetToolInfo("mkvextract", real["mkvextract"]).Ok);
        var fp = Tools.GetToolInfo("ffprobe", real["ffprobe"]);
        Assert.True(fp.Ok && fp.Version.Length > 0);
        var wrong = Tools.GetToolInfo("ffprobe", real["ffmpeg"]); // chon nham ffmpeg vao o ffprobe
        Assert.False(wrong.Ok);
        Assert.StartsWith("không phải ffprobe", wrong.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("Copyright", wrong.Problem, StringComparison.Ordinal);
        var v = Tools.MkvmergeVersion(real["mkvmerge"]!);
        Assert.True(v.Ok && v.Major >= 1, v.Text);
    }

    [Fact]
    public void Tool_info_and_mkvmerge_version_same_as_python()
    {
        PyCases.Check("tool_info", x =>
        {
            var a = (List<object?>)x!;
            var i = Tools.ToolInfoOf((string)a[0]!, "x y", new RunResult(Convert.ToInt64(a[1], System.Globalization.CultureInfo.InvariantCulture), (string)a[2]!, (string)a[3]!));
            return PyData.L(i.Name, i.Path, i.Ok, i.Version, i.Problem, i.Text);
        });
        PyCases.Check("mkvmerge_version", x =>
        {
            var a = (List<object?>)x!;
            var v = Tools.MkvmergeVersionOf(new RunResult(Convert.ToInt64(a[0], System.Globalization.CultureInfo.InvariantCulture), (string)a[1]!, (string)a[2]!));
            return PyData.L(v.Ok, v.Major, v.Text);
        });
    }

    // ---------------------------------------------------------------- tien ich
    private static void WaitUntil(Func<bool> cond, double seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds))
                throw new TimeoutException("chờ quá lâu");
            Thread.Sleep(20);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
