using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MuxStudio.Core;

// ==========================================================================
//  JOB: log + tiến độ + dừng   (port class Job / Cancelled / NULL_JOB của core/tools.py)
// ==========================================================================

/// <summary>Một dòng log: mức (<c>info ok warn error dim head</c>) + nội dung — = tuple <c>(muc, noi dung)</c> của Python.</summary>
public readonly record struct LogLine(string Level, string Message);

/// <summary>Tiến độ <c>(done, total, label)</c> — = tham số của <c>Job.progress</c>.</summary>
public readonly record struct JobProgress(int Done, int Total, string Label);

/// <summary><see cref="IProgress{T}"/> gọi NGAY trên luồng báo (khác <see cref="Progress{T}"/> đẩy sang SynchronizationContext).
/// Dùng cho test và chỗ không có giao diện.</summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>Người dùng bấm "Dừng ngay" (port <c>tools.Cancelled</c>). Là <see cref="OperationCanceledException"/> để
/// <c>catch (OperationCanceledException)</c> bắt được cả huỷ qua <see cref="CancellationToken"/>.</summary>
public sealed class Cancelled : OperationCanceledException
{
    public Cancelled()
        : base("")
    {
    }

    public Cancelled(CancellationToken token)
        : base("", token)
    {
    }
}

/// <summary>
/// Truyền log/tiến độ từ lõi về giao diện và nhận lệnh dừng (port <c>tools.Job</c>).
/// <list type="bullet">
/// <item><see cref="StopAfterCurrent"/>: dừng sau file đang chạy (lõi kiểm tra giữa các file).</item>
/// <item><see cref="CancelNow"/>: dừng ngay — huỷ <see cref="Token"/> và kết thúc mọi tiến trình con đang chạy (cả cây con:
/// ffmpeg/alass…). Huỷ <c>cancellationToken</c> truyền vào hàm tạo cũng có tác dụng như vậy.</item>
/// </list>
/// </summary>
public sealed class Job : IDisposable
{
    private readonly IProgress<LogLine>? _log;
    private readonly IProgress<JobProgress>? _progress;
    private readonly Func<string, string>? _redact;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenRegistration _killOnCancel;
    private readonly HashSet<Process> _procs = [];
    private readonly List<LogLine> _lines = [];
    private readonly Lock _lock = new();
    private volatile bool _stopAfterCurrent;

    public Job(IProgress<LogLine>? log = null, IProgress<JobProgress>? progress = null, Func<string, string>? redact = null,
               CancellationToken cancellationToken = default)
    {
        _log = log;
        _progress = progress;
        _redact = redact;
        _cts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        _killOnCancel = _cts.Token.Register(KillAll);
    }

    /// <summary><c>NULL_JOB</c>: không log, không bao giờ bị huỷ (trừ khi ai đó gọi <see cref="CancelNow"/> trên nó).</summary>
    public static Job Null { get; } = new();

    /// <summary>Bị huỷ khi bấm "Dừng ngay" (<see cref="CancelNow"/>).</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Bản sao log (để lưu / test) — <c>job.lines</c>.</summary>
    public IReadOnlyList<LogLine> Lines
    {
        get
        {
            lock (_lock)
                return [.. _lines];
        }
    }

    // ---- log ----
    public void Log(string msg, string level = "info")
    {
        if (_redact is not null)
            msg = _redact(msg);
        var line = new LogLine(level, msg);
        lock (_lock)
            _lines.Add(line);
        _log?.Report(line);
    }

    public void Info(string msg) => Log(msg, "info");

    public void Ok(string msg) => Log(msg, "ok");

    public void Warn(string msg) => Log(msg, "warn");

    public void Error(string msg) => Log(msg, "error");

    public void Dim(string msg) => Log(msg, "dim");

    public void Head(string msg) => Log(msg, "head");

    public void Progress(int done, int total, string label = "") => _progress?.Report(new JobProgress(done, total, label));

    // ---- dung ----

    /// <summary><c>job.stop_after_current.set()</c>.</summary>
    public void StopAfterCurrent() => _stopAfterCurrent = true;

    /// <summary><c>job.stop_after_current.is_set()</c>.</summary>
    public bool StopAfterCurrentRequested => _stopAfterCurrent;

    /// <summary><c>job.stop_now.is_set()</c>.</summary>
    public bool StopNowRequested => _cts.IsCancellationRequested;

    /// <summary><c>check</c>: đã bấm "Dừng ngay" → ném <see cref="Cancelled"/>.</summary>
    public void Check()
    {
        if (_cts.IsCancellationRequested)
            throw new Cancelled(_cts.Token);
    }

    /// <summary><c>should_stop</c>.</summary>
    public bool ShouldStop() => _stopAfterCurrent || _cts.IsCancellationRequested;

    public void Register(Process p)
    {
        lock (_lock)
            _procs.Add(p);
        if (_cts.IsCancellationRequested) // huỷ đúng lúc vừa mở tiến trình: diệt luôn
            Tools.TryKill(p);
    }

    public void Unregister(Process p)
    {
        lock (_lock)
            _procs.Remove(p);
    }

    /// <summary>Số tiến trình con đang chạy (test: <c>job._procs</c>).</summary>
    internal int RunningCount
    {
        get
        {
            lock (_lock)
                return _procs.Count;
        }
    }

    /// <summary><c>cancel_now</c>: huỷ, diệt mọi tiến trình con (cả cây), chờ chúng thoát tối đa 3 giây.</summary>
    public void CancelNow()
    {
        _cts.Cancel(); // -> KillAll
        Process[] procs;
        lock (_lock)
            procs = [.. _procs];
        var deadline = Environment.TickCount64 + 3000;
        foreach (var p in procs)
        {
            try
            {
                if (!p.WaitForExit((int)Math.Max(0, deadline - Environment.TickCount64)))
                    Tools.TryKill(p);
            }
            catch (SystemException)
            {
                // tiến trình đã thoát / đã được giải phóng (InvalidOperationException, Win32Exception)
            }
        }
    }

    private void KillAll()
    {
        Process[] procs;
        lock (_lock)
            procs = [.. _procs];
        foreach (var p in procs)
            Tools.TryKill(p);
    }

    public void Dispose()
    {
        _killOnCancel.Dispose();
        _cts.Dispose();
    }
}

// ==========================================================================
//  Kết quả
// ==========================================================================

/// <summary>Kết quả chạy một lệnh (port <c>tools.RunResult</c>). <see cref="Code"/> như <c>returncode</c> của Python trên
/// Windows: số KHÔNG âm (vd thiếu DLL = 3221225781, không phải -1073741515); quá thời gian = -9; không chạy được = 127.</summary>
public sealed record RunResult(long Code, string Stdout, string Stderr)
{
    /// <summary><c>output</c>: stdout + stderr (nối bằng xuống dòng), bỏ khoảng trắng hai đầu.</summary>
    public string Output => Py.StripWs(Stdout + (Stdout.Length > 0 && Stderr.Length > 0 ? "\n" : "") + Stderr);
}

/// <summary><c>tools.ToolVersion</c>.</summary>
public sealed record ToolVersion(bool Ok, int Major, string Text);

/// <summary>Kết quả kiểm tra một công cụ (ô "Công cụ ngoài" trong Cài đặt) — <c>tools.ToolInfo</c>.</summary>
public sealed record ToolInfo(string Name, string? Path, bool Ok, string Version = "", string Problem = "")
{
    public string Text => Ok ? (Version.Length > 0 ? Version : "chạy được") : Problem;
}

/// <summary><c>tools.AlassFlags</c>.</summary>
public sealed record AlassFlags(List<string> Ok, List<string> Unsupported, bool HelpRead);

/// <summary>Công cụ dùng ở bước nào, có bắt buộc không — giá trị của <c>TOOL_ROLES</c>.</summary>
public sealed record ToolRole(string Role, bool Required);

/// <summary>
/// Công cụ ngoài + tiến trình con (port <c>core/tools.py</c>).
/// Thay <c>Find-Exe</c> (MT), <c>Find-MkvTool</c>/<c>Find-Alass</c>/<c>Find-Near</c> (SP), vòng tìm mkvmerge đầu SubFetch,
/// <c>Join-Safe</c> (cả 3), <c>Save-ToDest</c> (SF).
/// <para>Quy tắc an toàn (§6.1): gọi tiến trình con bằng DANH SÁCH tham số (<see cref="ProcessStartInfo.ArgumentList"/>), không
/// qua shell, <c>CreateNoWindow</c>, đọc output UTF-8 (byte sai → U+FFFD). Đường dẫn dùng nguyên văn — không bao giờ đưa vào glob.</para>
/// </summary>
public static partial class Tools
{
    /// <summary><c>LEGACY_POOL_FOLDER</c>: thư mục kho phụ đề của các bản ≤ 1.1.0 (và của SubFetch.bat). Kho đã bỏ ở bản 1.2.0
    /// (TĐ-38) nhưng thư mục này vẫn bị loại khi quét, để phụ đề / video còn nằm trong đó không bị nhận nhầm.</summary>
    public const string LegacyPoolFolder = "srt_pool";

    public static readonly bool IsWindows = OperatingSystem.IsWindows();

    // ==========================================================================
    //  CHẠY TIẾN TRÌNH CON
    // ==========================================================================

    /// <summary>Cách mở tiến trình của <see cref="Run"/> (tách ra để test kiểm tra: không shell, không cửa sổ, danh sách tham số).</summary>
    internal static ProcessStartInfo StartInfo(IReadOnlyList<string> argv, string? cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // đóng ngay sau khi mở = stdin=DEVNULL
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);
        if (cwd is not null)
            psi.WorkingDirectory = cwd;
        return psi;
    }

    /// <summary>
    /// <c>run</c>: chạy một lệnh. KHÔNG dùng shell. Không tìm thấy / không chạy được chương trình → mã 127.
    /// Quá <paramref name="timeout"/> → diệt (cả cây tiến trình), mã -9. Đã bấm "Dừng ngay" (trước hoặc trong lúc chạy) →
    /// <see cref="Cancelled"/>.
    /// </summary>
    public static RunResult Run(IEnumerable<string> args, Job? job = null, TimeSpan? timeout = null, string? cwd = null)
    {
        var argv = args.ToList();
        job ??= Job.Null;
        job.Check();
        Process p;
        try
        {
            p = Process.Start(StartInfo(argv, cwd)) ?? throw new Win32Exception("không mở được tiến trình");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return new RunResult(127, "", e.Message); // Python: FileNotFoundError / PermissionError / OSError
        }
        long code;
        var outBuf = new MemoryStream();
        var errBuf = new MemoryStream();
        using (p)
        {
            p.StandardInput.Close();
            job.Register(p);
            try
            {
                // đọc nguyên byte rồi mới giải mã (như communicate() + decode): không tự nhận BOM / đổi bảng mã
                var all = Task.WhenAll(p.StandardOutput.BaseStream.CopyToAsync(outBuf),
                                       p.StandardError.BaseStream.CopyToAsync(errBuf), p.WaitForExitAsync());
                if (timeout is { } t && !all.Wait(t))
                {
                    TryKill(p);
                    all.Wait();
                    code = -9;
                }
                else
                {
                    all.Wait();
                    code = unchecked((uint)p.ExitCode); // GetExitCodeProcess trả DWORD, Python cũng đọc là số không âm
                }
            }
            finally
            {
                job.Unregister(p);
            }
        }
        job.Check();
        return new RunResult(code, Py.Utf8Replace.GetString(outBuf.GetBuffer(), 0, (int)outBuf.Length),
                             Py.Utf8Replace.GetString(errBuf.GetBuffer(), 0, (int)errBuf.Length));
    }

    /// <summary>Diệt tiến trình và mọi tiến trình con của nó; đã thoát / đã giải phóng thì thôi.</summary>
    internal static void TryKill(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
        }
    }

    // ==========================================================================
    //  ĐƯỜNG DẪN
    // ==========================================================================

    /// <summary><c>join_safe</c> (<c>Join-Safe</c>): nối đường dẫn, vế trái rỗng → <c>null</c>.</summary>
    public static string? JoinSafe(string? @base, string child)
    {
        if (string.IsNullOrWhiteSpace(@base))
            return null;
        return Py.Join(@base, child);
    }

    /// <summary><c>copy_file</c> (<c>Save-ToDest</c>): chép NỘI DUNG bằng đường dẫn nguyên văn (tên có <c>[ ]</c> vẫn đúng), tạo
    /// thư mục đích. Như <c>shutil.copyfile</c>: chép đè, không giữ thời gian sửa; nguồn và đích là một file → lỗi.</summary>
    public static void CopyFile(string src, string dst)
    {
        var d = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(d))
            Directory.CreateDirectory(d);
        if (string.Equals(Py.NormCase(Py.AbsPath(src)), Py.NormCase(Py.AbsPath(dst)), StringComparison.Ordinal) && File.Exists(src))
            throw new IOException($"'{src}' and '{dst}' are the same file"); // shutil.SameFileError
        using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var t = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        s.CopyTo(t);
    }

    /// <summary><c>is_under</c>: <paramref name="path"/> nằm trong (hoặc chính là) <paramref name="folder"/> — so không phân biệt
    /// hoa thường như Windows.</summary>
    public static bool IsUnder(string path, string folder)
    {
        var p = Py.NormCase(Py.AbsPath(path));
        var f = Py.NormCase(Py.AbsPath(folder));
        return p == f || p.StartsWith(f.TrimEnd('\\', '/') + "\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>walk_files</c>: liệt kê file (không dùng glob). <paramref name="skipDirs"/>: thư mục bỏ qua (cả cây con).
    /// Sắp xếp theo đường dẫn, không phân biệt hoa thường (như <c>Sort-Object FullName</c>).
    /// Như <c>os.walk</c>: thư mục không đọc được thì bỏ qua; đi vào junction nhưng không đi vào symlink.
    /// </summary>
    public static List<string> WalkFiles(string root, bool recursive = true, IEnumerable<string>? exts = null,
                                         IEnumerable<string>? skipDirs = null)
    {
        var extList = exts?.ToList();
        HashSet<string>? want = extList is { Count: > 0 } ? extList.Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal) : null;
        var skips = (skipDirs ?? []).Select(s => Py.NormCase(Py.AbsPath(s))).ToList();
        var outp = new List<string>();

        bool Skipped(string d)
        {
            var nd = Py.NormCase(Py.AbsPath(d));
            return skips.Any(s => nd == s || nd.StartsWith(s.TrimEnd('\\', '/') + "\\", StringComparison.Ordinal));
        }

        bool Wanted(string name) => want is null || want.Contains(Py.SplitExt(name).Ext.ToLowerInvariant());

        if (!Directory.Exists(root))
            return outp;
        if (recursive)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dirpath = stack.Pop();
                if (Skipped(dirpath))
                    continue;
                List<FileSystemInfo> entries;
                try
                {
                    entries = [.. new DirectoryInfo(dirpath).EnumerateFileSystemInfos()];
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue; // os.walk(onerror=None): bỏ qua thư mục không đọc được
                }
                foreach (var e in entries)
                {
                    if ((e.Attributes & FileAttributes.Directory) != 0)
                    {
                        var sub = Py.Join(dirpath, e.Name);
                        if (!Skipped(sub) && !Py.IsLink(sub))
                            stack.Push(sub);
                    }
                    else if (Wanted(e.Name))
                    {
                        outp.Add(Py.PathJoin(dirpath, e.Name));
                    }
                }
            }
        }
        else
        {
            foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if ((e.Attributes & FileAttributes.Directory) != 0)
                    continue;
                var full = Py.Join(root, e.Name);
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && !File.Exists(full))
                    continue; // DirEntry.is_file() đi theo symlink: symlink hỏng không phải file
                if (Wanted(e.Name))
                    outp.Add(Py.PurePath(full));
            }
        }
        return [.. outp.OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal)];
    }

    // ==========================================================================
    //  TÌM CÔNG CỤ ("Tự tìm")
    // ==========================================================================

    /// <summary><c>TOOLS</c>.</summary>
    public static readonly IReadOnlyList<string> ToolNames = ["mkvmerge", "mkvextract", "ffmpeg", "ffprobe", "alass"];

    /// <summary><c>CONFIG_PATH_KEY</c>: ô đường dẫn trong Cài đặt của từng công cụ.</summary>
    public static readonly IReadOnlyDictionary<string, string> ConfigPathKey = new OrderedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["mkvmerge"] = "MkvmergePath",
        ["mkvextract"] = "MkvextractPath",
        ["ffmpeg"] = "FfmpegPath",
        ["ffprobe"] = "FfprobePath",
        ["alass"] = "AlassPath",
    };

    /// <summary><c>exe_name</c>.</summary>
    public static string ExeName(string name) => IsWindows ? name + ".exe" : name;

    /// <summary><c>desktop_dir</c>: Desktop THẬT (OneDrive có thể chuyển Desktop sang <c>...\OneDrive\Desktop</c>).</summary>
    public static string? DesktopDir()
    {
        if (IsWindows)
        {
            var d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); // CSIDL_DESKTOPDIRECTORY
            if (d.Length > 0)
                return d;
        }
        var home = Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } up
            ? up
            : Environment.GetEnvironmentVariable("HOMEPATH") is { } hp ? (Environment.GetEnvironmentVariable("HOMEDRIVE") ?? "") + hp : "~";
        return home.Length > 0 ? Py.Join(home, "Desktop") : null;
    }

    /// <summary><c>_registry_mkvtoolnix_dirs</c>: <c>InstallLocation</c> của các mục gỡ cài đặt có tên chứa "mkvtoolnix".</summary>
    private static List<string> RegistryMkvToolNixDirs()
    {
        var outp = new List<string>();
        if (!IsWindows)
            return outp;
        (RegistryKey Hive, string Path)[] roots =
        [
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ];
        foreach (var (hive, path) in roots)
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k is null)
                    continue;
                foreach (var sub in k.GetSubKeyNames())
                {
                    if (!sub.ToLowerInvariant().Contains("mkvtoolnix", StringComparison.Ordinal))
                        continue;
                    using var sk = k.OpenSubKey(sub);
                    // QueryValueEx của winreg không tự mở rộng %biến%
                    if (sk?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string { Length: > 0 } loc)
                        outp.Add(loc);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return outp;
    }

    /// <summary><c>tool_search_dirs</c>: thứ tự thư mục tìm (hợp các danh sách của ba file .bat).</summary>
    public static List<string> ToolSearchDirs(string name, Config cfg, string? targetDir = null, string? near = null)
    {
        var desk = DesktopDir();
        var prof = Environment.GetEnvironmentVariable("USERPROFILE");
        var td = string.IsNullOrEmpty(targetDir) ? null : targetDir;
        var mkv = name is "mkvmerge" or "mkvextract";
        var dirs = new List<string?>();
        if (name is "ffmpeg" or "ffprobe" && !string.IsNullOrEmpty(near))
            dirs.Add(near); // Find-Near: canh alass.exe truoc
        if (mkv)
        {
            dirs.Add(cfg.Str("MkvToolNixDir"));
        }
        else
        {
            dirs.Add(cfg.Str("AlassDir"));
            if (name == "ffprobe")
                dirs.Add(cfg.Str("MkvToolNixDir"));
        }
        dirs.AddRange(cfg.List("ExtraToolDirs"));
        var sub = mkv ? "mkvtoolnix" : "alass";
        dirs.Add(JoinSafe(desk, sub));
        dirs.Add(JoinSafe(prof, Py.Join("Desktop", sub)));
        if (!mkv)
            dirs.Add(JoinSafe(desk, Py.Join("alass-windows64", "bin", "alass")));
        dirs.Add(td);
        dirs.Add(JoinSafe(td, sub));
        if (mkv)
        {
            foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)" })
                dirs.Add(JoinSafe(Environment.GetEnvironmentVariable(env), "MKVToolNix"));
            dirs.Add(JoinSafe(Environment.GetEnvironmentVariable("LOCALAPPDATA"), Py.Join("Programs", "MKVToolNix")));
            dirs.AddRange(RegistryMkvToolNixDirs());
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outp = new List<string>();
        foreach (var d in dirs)
        {
            if (!string.IsNullOrEmpty(d) && seen.Add(Py.NormCase(d)))
                outp.Add(d);
        }
        return outp;
    }

    /// <summary><c>find_tool</c>: tìm một công cụ. Đường dẫn trong Cài đặt (nếu có) được ưu tiên.</summary>
    public static string? FindTool(string name, Config cfg, string? targetDir = null, string? near = null, bool useConfigPath = true)
    {
        if (useConfigPath && ConfigPathKey.TryGetValue(name, out var key))
        {
            var p = cfg.Str(key);
            if (p.Length > 0 && File.Exists(p))
                return Py.AbsPath(p);
        }
        var exe = ExeName(name);
        foreach (var d in ToolSearchDirs(name, cfg, targetDir, near))
        {
            var p = Py.Join(d, exe);
            if (File.Exists(p))
                return Py.AbsPath(p);
        }
        return Py.Which(exe) ?? Py.Which(name);
    }

    /// <summary><c>find_all_tools</c>: ffmpeg/ffprobe tìm cạnh alass trước.</summary>
    public static OrderedDictionary<string, string?> FindAllTools(Config cfg, string? targetDir = null)
    {
        var alass = FindTool("alass", cfg, targetDir);
        var near = alass is null ? null : Path.GetDirectoryName(alass);
        return new OrderedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["mkvmerge"] = FindTool("mkvmerge", cfg, targetDir),
            ["mkvextract"] = FindTool("mkvextract", cfg, targetDir),
            ["alass"] = alass,
            ["ffmpeg"] = FindTool("ffmpeg", cfg, targetDir, near),
            ["ffprobe"] = FindTool("ffprobe", cfg, targetDir, near),
        };
    }

    [GeneratedRegex(@"v(\d+)\.")]
    private static partial Regex MkvMajorRe();

    /// <summary>
    /// <c>mkvmerge_version</c>: <c>mkvmerge --version</c> → bản chính (vd 82). Tìm thấy mà không chạy được → <c>Ok = false</c>
    /// (thường do thiếu DLL hoặc Windows chặn file tải từ Internet: chuột phải › Properties › Unblock).
    /// </summary>
    public static ToolVersion MkvmergeVersion(string mkvmerge, Job? job = null) =>
        MkvmergeVersionOf(Run([mkvmerge, "--version"], job, TimeSpan.FromSeconds(30)));

    /// <summary>Phần đọc kết quả của <see cref="MkvmergeVersion"/> (tách ra để đối chiếu với Python).</summary>
    internal static ToolVersion MkvmergeVersionOf(RunResult r)
    {
        var txt = r.Output;
        var ok = r.Code == 0 && txt.ToLowerInvariant().Contains("mkvmerge", StringComparison.Ordinal);
        var m = MkvMajorRe().Match(txt);
        return new ToolVersion(ok, m.Success ? Py.Int(m.Groups[1].Value) : 0, txt.Length > 0 ? Py.SplitLines(txt)[0] : "");
    }

    /// <summary><c>TOOL_ROLES</c>: công cụ dùng ở bước nào — hiện trong Cài đặt và khi báo thiếu công cụ.</summary>
    public static readonly IReadOnlyDictionary<string, ToolRole> ToolRoles = new OrderedDictionary<string, ToolRole>(StringComparer.Ordinal)
    {
        ["mkvmerge"] = new("tạo file MKV (bước ③); đọc FPS / track (bước ① ②)", true),
        ["mkvextract"] = new("trích phụ đề gốc từ MKV (bước ① ②), đo audio ghép (bước ③)", false),
        ["alass"] = new("đồng bộ phụ đề (bước ②)", true),
        ["ffprobe"] = new("audio track của file mẫu (bước ②), bitrate + HDR (bước ③)", false),
        ["ffmpeg"] = new("trích WAV khi đồng bộ theo audio (bước ②), đổi tốc độ audio ghép (bước ③)", false),
    };

    private static readonly IReadOnlyDictionary<string, string[]> InfoArgs = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["mkvmerge"] = ["--version"], ["mkvextract"] = ["--version"], ["ffmpeg"] = ["-version"], ["ffprobe"] = ["-version"],
        ["alass"] = ["--help"],
    };

    private static readonly IReadOnlyDictionary<string, string> InfoMark = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mkvmerge"] = "mkvmerge", ["mkvextract"] = "mkvextract", ["ffmpeg"] = "ffmpeg version", ["ffprobe"] = "ffprobe version",
        ["alass"] = "alass",
    };

    /// <summary><c>BLOCKED_HINT</c>.</summary>
    public const string BlockedHint = "thiếu DLL hoặc bị Windows chặn: chuột phải vào file › Properties › Unblock";

    /// <summary><c>tool_info</c> với một đường dẫn (rỗng / <c>null</c> → "không tìm thấy").</summary>
    public static ToolInfo GetToolInfo(string name, string? path, Job? job = null) =>
        GetToolInfo(name, string.IsNullOrEmpty(path) ? null : [path], job);

    /// <summary>
    /// <c>tool_info</c>: chạy thử công cụ (<c>--version</c> / <c>-version</c> / <c>alass --help</c>): có chạy được không, phiên bản,
    /// có đúng là công cụ đó không (vd chọn nhầm <c>ffmpeg.exe</c> vào ô ffprobe). <paramref name="cmd"/> có thể là một lệnh
    /// nhiều phần (vd alass giả). Không tìm thấy → <c>Ok = false</c>.
    /// </summary>
    public static ToolInfo GetToolInfo(string name, IReadOnlyList<string>? cmd, Job? job = null)
    {
        if (cmd is null || cmd.Count == 0)
            return new ToolInfo(name, null, false, Problem: "không tìm thấy");
        var shown = string.Join(' ', cmd);
        if (cmd.Count == 1 && !File.Exists(cmd[0]))
            return new ToolInfo(name, shown, false, Problem: "không có file này");
        var r = Run([.. cmd, .. InfoArgs.GetValueOrDefault(name, ["--version"])], job, TimeSpan.FromSeconds(30));
        return ToolInfoOf(name, shown, r);
    }

    [GeneratedRegex(@"\bv(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex MkvVerRe();

    [GeneratedRegex(@"version\s+(\S+)")]
    private static partial Regex FfVerRe();

    [GeneratedRegex(@"alass\S*\s+v?(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlassVerRe();

    /// <summary>Phần đọc kết quả của <see cref="GetToolInfo(string, IReadOnlyList{string}?, Job?)"/> (tách ra để đối chiếu với Python).</summary>
    internal static ToolInfo ToolInfoOf(string name, string shown, RunResult r)
    {
        var outp = r.Output;
        if (r.Code == 127 || outp.Length == 0)
            return new ToolInfo(name, shown, false, Problem: $"tìm thấy nhưng KHÔNG chạy được ({BlockedHint})");
        if (!outp.ToLowerInvariant().Contains(InfoMark.GetValueOrDefault(name, name), StringComparison.Ordinal))
        {
            var line = Py.SplitLines(outp)[0];
            var cut = line.IndexOf(" Copyright", StringComparison.Ordinal);
            var first = Py.Slice(cut >= 0 ? line[..cut] : line, 0, 60);
            return new ToolInfo(name, shown, false, Problem: $"không phải {ExeName(name)}? (chương trình trả lời: {first})");
        }
        var ver = "";
        if (name is "mkvmerge" or "mkvextract")
        {
            var m = MkvVerRe().Match(outp);
            ver = m.Success ? "v" + m.Groups[1].Value : "";
        }
        else if (name is "ffmpeg" or "ffprobe")
        {
            var m = FfVerRe().Match(outp);
            ver = m.Success ? Py.Slice(m.Groups[1].Value, 0, 24) : "";
        }
        else if (name == "alass")
        {
            var m = AlassVerRe().Match(outp);
            ver = m.Success ? m.Groups[1].Value : "";
        }
        return new ToolInfo(name, shown, true, ver);
    }

    /// <summary><c>alass_supported_flags</c> với lệnh alass là một đường dẫn.</summary>
    public static AlassFlags AlassSupportedFlags(string alassCmd, IEnumerable<string> wanted, Job? job = null, string? helpText = null) =>
        AlassSupportedFlags([alassCmd], wanted, job, helpText);

    /// <summary>
    /// <c>alass_supported_flags</c> (<c>Get-SupportedAlassFlags</c>): cờ <c>--xxx</c> nào bản alass này hiểu (đọc <c>alass --help</c>).
    /// Không đọc được <c>--help</c> → dùng đủ tham số (như .bat). Khác .bat: cờ không được hỗ trợ thì bỏ luôn GIÁ TRỊ đi kèm nó
    /// (vd <c>--split-penalty 7</c>), tránh để <c>7</c> lạc thành tên file.
    /// </summary>
    public static AlassFlags AlassSupportedFlags(IReadOnlyList<string> alassCmd, IEnumerable<string> wanted, Job? job = null,
                                                 string? helpText = null)
    {
        var w = wanted.ToList();
        if (w.Count == 0)
            return new AlassFlags([], [], true);
        helpText ??= Run([.. alassCmd, "--help"], job, TimeSpan.FromSeconds(30)).Output;
        if (helpText.Length == 0)
            return new AlassFlags(w, [], false);
        var ok = new List<string>();
        var no = new List<string>();
        var dropping = false;
        var low = helpText.ToLowerInvariant();
        foreach (var f in w)
        {
            if (f.StartsWith("--", StringComparison.Ordinal))
            {
                dropping = !low.Contains(f.ToLowerInvariant(), StringComparison.Ordinal); // -match cua PowerShell: khong phan biet hoa thuong
                (dropping ? no : ok).Add(f);
            }
            else if (!dropping)
            {
                ok.Add(f); // gia tri di kem tham so truoc do
            }
        }
        return new AlassFlags(ok, no, true);
    }

    /// <summary><c>mkvmerge_flag_names</c>: từ v74 mkvmerge đổi tên cờ (§6.2).</summary>
    public static (string DefaultFlag, string ForcedFlag) MkvmergeFlagNames(int major) =>
        major >= 74 ? ("--default-track-flag", "--forced-display-flag") : ("--default-track", "--forced-track");
}
