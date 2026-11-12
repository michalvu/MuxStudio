using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MuxStudio.Core;

/// <summary>Một dòng nhật ký.</summary>
public sealed record LogRow(string Time, string Action, string From = "", string To = "", string Detail = "", string Status = "");

/// <summary>Một lần chạy trên màn hình Hoàn tác.</summary>
public sealed record RunInfo(string Path, string Name, OrderedDictionary<string, int> Counts, bool Undoable, string Note = "");

/// <summary>Một thao tác hoàn tác.</summary>
/// <param name="Kind">trash | move_back | rename_back | rmdir</param>
/// <param name="Target">Cho move_back/rename_back: đường dẫn trả về.</param>
public sealed record UndoOp(string Kind, string Path, string Target = "", int Row = 0);

/// <summary>Kế hoạch hoàn tác một lần chạy (chưa đụng tới file nào).</summary>
public sealed class UndoPlan(string logPath)
{
    public string LogPath { get; } = logPath;

    public List<UndoOp> Ops { get; } = [];

    /// <summary>File không còn → bỏ qua.</summary>
    public List<string> Missing { get; } = [];

    public List<string> Notes { get; } = [];
}

public sealed class UndoResult
{
    public List<string> Done { get; } = [];

    /// <summary>(đường dẫn, lý do).</summary>
    public List<(string Path, string Reason)> Skipped { get; } = [];
}

/// <summary>
/// Nhật ký thao tác + hoàn tác nhiều lần chạy, chung cho ba bước. Port <c>core/undo.py</c>.
/// <list type="bullet">
/// <item>File: <c>_subfetch_logs\actions_*.csv</c>, <c>_subprep_logs\run_*.csv</c>, <c>_muxtool_logs\mux_*.csv</c>.</item>
/// <item>Cột: <c>Time, Action, From, To, Detail, Status</c> — đọc được cả nhật ký cũ của .bat.</item>
/// <item>Hoàn tác: file tạo ra → Thùng rác; file đã di chuyển/đổi tên → trả về chỗ cũ, theo thứ tự ngược.
/// Khác .bat: không bao giờ xoá hẳn (TĐ-16).</item>
/// </list>
/// </summary>
public static class Undo
{
    public static readonly IReadOnlyDictionary<string, string> StepPrefix = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["subfetch"] = "actions_",
        ["subprep"] = "run_",
        ["mux"] = "mux_",
    };

    public static readonly IReadOnlyList<string> Columns = ["Time", "Action", "From", "To", "Detail", "Status"];

    // Hành động ghi được và cách hoàn tác
    /// <summary>Tạo file mới (To) → Thùng rác.</summary>
    public const string Create = "Create";

    /// <summary>Chép file (From → To) → Thùng rác file To.</summary>
    public const string Copy = "Copy";

    /// <summary>Di chuyển (From → To) → trả To về From.</summary>
    public const string Move = "Move";

    /// <summary>Đổi tên (From → To) → đổi To về tên cũ.</summary>
    public const string Rename = "Rename";

    /// <summary>Tạo thư mục mới (To) → xoá nếu rỗng.</summary>
    public const string Mkdir = "Mkdir";

    /// <summary>Đã xoá vào Thùng rác (From) → không tự khôi phục được.</summary>
    public const string Delete = "Delete";

    /// <summary>Nhật ký cũ của SubPrep → không hoàn tác được.</summary>
    public const string Encoding = "Encoding";

    // ------------------------------------------------------------------
    //  Xoá an toàn
    // ------------------------------------------------------------------

    /// <summary>Gửi một file/thư mục vào Thùng rác (= <c>send2trash</c>); lỗi → ngoại lệ. Test thay bằng hàm giả.</summary>
    public static Action<string> SendToRecycleBin { get; set; } = RecycleBin.Send;

    private static bool LExists(string p)
    {
        try
        {
            return File.Exists(p) || Directory.Exists(p) || new FileInfo(p).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Xoá một file/thư mục: vào Thùng rác hoặc xoá hẳn khi tắt <c>DeleteToRecycleBin</c>.
    /// Khác <c>Remove-FileSafe</c>: Thùng rác lỗi thì KHÔNG tự chuyển sang xoá hẳn — trả <c>false</c>.
    /// </summary>
    public static bool Trash(string path, bool toRecycleBin = true)
    {
        if (!LExists(path))
            return false;
        if (toRecycleBin)
        {
            try
            {
                SendToRecycleBin(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        try
        {
            var isLink = new FileInfo(path).LinkTarget is not null;
            if (Directory.Exists(path) && !isLink)
                Directory.Delete(path); // như os.rmdir: chỉ thư mục rỗng
            else
                File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    //  Ghi nhật ký
    // ------------------------------------------------------------------

    /// <summary>Chỉ giữ <paramref name="keep"/> nhật ký mới nhất (theo tên, giảm dần).</summary>
    public static void RotateLogs(string logDir, string prefix, int keep)
    {
        if (keep <= 0 || !Directory.Exists(logDir))
            return;
        foreach (var name in LogNames(logDir, prefix).Skip(keep))
        {
            try
            {
                File.Delete(Path.Combine(logDir, name));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static List<string> LogNames(string dir, string prefix)
    {
        var names = new DirectoryInfo(dir).EnumerateFiles()
            .Select(f => f.Name)
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.ToLowerInvariant().EndsWith(".csv", StringComparison.Ordinal))
            .ToList();
        names.Sort(StringComparer.Ordinal);
        names.Reverse();
        return names;
    }

    // ------------------------------------------------------------------
    //  Đọc nhật ký + hoàn tác
    // ------------------------------------------------------------------

    /// <summary><c>csv.DictReader</c> trên file <c>utf-8-sig</c>. Ô thiếu = <c>null</c> (restval của Python).</summary>
    public static List<Dictionary<string, string?>> ReadRun(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        var text = Py.Utf8Strict.GetString(bytes, start, bytes.Length - start);
        return PyCsv.DictRows(text);
    }

    private static string Get(Dictionary<string, string?> r, string key) => r.TryGetValue(key, out var v) && v is not null ? v : "";

    /// <summary>Các lần chạy (mới nhất trước) cho màn hình Hoàn tác.</summary>
    public static List<RunInfo> ListRuns(string root, string logDirName, string step)
    {
        var d = Path.Combine(root, logDirName);
        var prefix = StepPrefix[step];
        if (!Directory.Exists(d))
            return [];
        var outp = new List<RunInfo>();
        foreach (var name in LogNames(d, prefix))
        {
            var p = Path.Combine(d, name);
            var stem = Path.GetFileNameWithoutExtension(name);
            List<Dictionary<string, string?>> rows;
            try
            {
                rows = ReadRun(p);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                outp.Add(new RunInfo(p, stem, new OrderedDictionary<string, int>(StringComparer.Ordinal), false, "không đọc được nhật ký"));
                continue;
            }
            var counts = new OrderedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                var a = Get(r, "Action");
                if (a.Length > 0)
                    counts[a] = counts.GetValueOrDefault(a) + 1;
            }
            var undoable = counts.Keys.Any(a => a is Create or Copy or Move or Rename or Mkdir);
            var note = undoable ? "" : rows.Count > 0 && !rows[0].ContainsKey("Action")
                ? "nhật ký cũ của .bat — không có thông tin để hoàn tác"
                : "không có thao tác hoàn tác được";
            outp.Add(new RunInfo(p, stem, counts, undoable, note));
        }
        return outp;
    }

    /// <summary>Lập kế hoạch hoàn tác một lần chạy (chưa đụng tới file nào) — để hiện hộp thoại xác nhận.</summary>
    public static UndoPlan PlanUndo(string logPath)
    {
        var rows = ReadRun(logPath);
        var plan = new UndoPlan(logPath);
        int nDelete = 0, nEnc = 0;
        // 1. File tạo ra / chép ra -> Thùng rác
        for (var i = 0; i < rows.Count; i++)
        {
            var a = Get(rows[i], "Action");
            if (a is Create or Copy)
            {
                var to = Get(rows[i], "To");
                if (to.Length > 0 && File.Exists(to))
                    plan.Ops.Add(new UndoOp("trash", to, Row: i));
                else if (to.Length > 0)
                    plan.Missing.Add(to);
            }
            else if (a == Delete)
            {
                nDelete++;
            }
            else if (a == Encoding)
            {
                nEnc++;
            }
        }
        // 2. Di chuyển / đổi tên -> trả về, theo thứ tự NGƯỢC
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var r = rows[i];
            var a = Get(r, "Action");
            var src = Get(r, "From");
            var to = Get(r, "To");
            if (a == Move)
            {
                if (File.Exists(to))
                    plan.Ops.Add(new UndoOp("move_back", to, src, i));
                else
                    plan.Missing.Add(to);
            }
            else if (a == Rename)
            {
                if (File.Exists(to))
                {
                    var back = Path.Combine(Path.GetDirectoryName(to) ?? "", Py.Basename(src));
                    plan.Ops.Add(new UndoOp("rename_back", to, back, i));
                }
                else
                {
                    plan.Missing.Add(to);
                }
            }
        }
        // 3. Thư mục mới tạo -> xoá nếu rỗng (sau cùng, thư mục sâu trước)
        var dirs = rows.Where(r => Get(r, "Action") == Mkdir && Get(r, "To").Length > 0).Select(r => Get(r, "To"))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(x => x.Count(ch => ch == Path.DirectorySeparatorChar));
        foreach (var d in dirs)
            plan.Ops.Add(new UndoOp("rmdir", d));
        if (nDelete > 0)
            plan.Notes.Add($"{nDelete} file đã được xoá vào Thùng rác — muốn lấy lại thì khôi phục trong Thùng rác.");
        if (nEnc > 0)
            plan.Notes.Add($"{nEnc} lần chuyển mã của .bat cũ đã ghi đè file nguồn — không hoàn tác được.");
        return plan;
    }

    /// <summary>Thực hiện kế hoạch hoàn tác. Không bao giờ xoá hẳn file của bạn khi <paramref name="toRecycleBin"/>.</summary>
    public static UndoResult ExecuteUndo(UndoPlan plan, bool toRecycleBin = true, Func<string, bool, bool>? trashFn = null,
        bool removeLog = true)
    {
        trashFn ??= Trash;
        var res = new UndoResult();
        foreach (var op in plan.Ops)
        {
            try
            {
                if (op.Kind == "trash")
                {
                    if (trashFn(op.Path, toRecycleBin))
                        res.Done.Add(op.Path);
                    else
                        res.Skipped.Add((op.Path, "không xoá được"));
                }
                else if (op.Kind is "move_back" or "rename_back")
                {
                    if (!File.Exists(op.Path))
                    {
                        res.Skipped.Add((op.Path, "không còn file"));
                        continue;
                    }
                    if (LExists(op.Target))
                    {
                        if (op.Kind == "rename_back")
                        {
                            res.Skipped.Add((op.Path, $"đã có file trùng tên cũ: {Py.Basename(op.Target)}"));
                            continue;
                        }
                        // Move (SubFetch): chỗ cũ đã có file mới -> đưa file đó vào Thùng rác rồi trả về
                        if (!trashFn(op.Target, toRecycleBin))
                        {
                            res.Skipped.Add((op.Path, "không dọn được file đang nằm ở chỗ cũ"));
                            continue;
                        }
                    }
                    var dir = Path.GetDirectoryName(op.Target);
                    Directory.CreateDirectory(string.IsNullOrEmpty(dir) ? "." : dir);
                    File.Move(op.Path, op.Target);
                    res.Done.Add(op.Target);
                }
                else if (op.Kind == "rmdir")
                {
                    if (Directory.Exists(op.Path) && !Directory.EnumerateFileSystemEntries(op.Path).Any())
                    {
                        Directory.Delete(op.Path);
                        res.Done.Add(op.Path);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                res.Skipped.Add((op.Path, e.Message));
            }
        }
        if (removeLog)
        {
            try
            {
                File.Delete(plan.LogPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return res;
    }

    /// <summary>Hoàn tác nhiều lần chạy, mới nhất trước.</summary>
    public static UndoResult UndoRuns(IEnumerable<string> logPaths, bool toRecycleBin = true, Func<string, bool, bool>? trashFn = null)
    {
        var total = new UndoResult();
        foreach (var p in logPaths.OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal))
        {
            var r = ExecuteUndo(PlanUndo(p), toRecycleBin, trashFn);
            total.Done.AddRange(r.Done);
            total.Skipped.AddRange(r.Skipped);
        }
        return total;
    }
}

/// <summary>
/// Nhật ký của MỘT lần chạy. <see cref="Save"/> ghi CSV (UTF-8 có BOM, mọi ô có ngoặc kép — như <c>Export-Csv</c>).
/// </summary>
public sealed class RunLog
{
    public RunLog(string root, string logDirName, string step, int keep = 20)
    {
        if (!Undo.StepPrefix.ContainsKey(step))
            throw new ArgumentException(step, nameof(step));
        Root = root;
        LogDir = System.IO.Path.Combine(root, logDirName);
        Step = step;
        Keep = keep;
    }

    public string Root { get; }

    public string LogDir { get; }

    public string Step { get; }

    public int Keep { get; }

    public List<LogRow> Rows { get; } = [];

    /// <summary>Sau lần <see cref="Save"/> đầu tiên: các lần sau GHI LẠI cùng file.</summary>
    public string? Path { get; private set; }

    public void Add(string action, string from = "", string to = "", string detail = "", string status = "") =>
        Rows.Add(new LogRow(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), action, from, to, detail, status));

    /// <summary>
    /// Ghi CSV. Gọi nhiều lần trong một lượt thì ghi lại CÙNG một file — một lượt chạy = một mục trên màn hình Hoàn tác,
    /// và không mất nhật ký nếu chương trình bị tắt giữa chừng.
    /// </summary>
    public string? Save(DateTime? now = null)
    {
        if (Rows.Count == 0)
            return null;
        Directory.CreateDirectory(LogDir);
        var prefix = Undo.StepPrefix[Step];
        var path = Path;
        if (path is null)
        {
            var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            path = System.IO.Path.Combine(LogDir, $"{prefix}{stamp}.csv");
            var n = 2;
            while (File.Exists(path) || Directory.Exists(path))
            {
                path = System.IO.Path.Combine(LogDir, $"{prefix}{stamp}_{n}.csv");
                n++;
            }
            Path = path;
        }
        var sb = new StringBuilder();
        PyCsv.WriteRowQuoteAll(sb, Undo.Columns);
        foreach (var r in Rows)
            PyCsv.WriteRowQuoteAll(sb, [r.Time, r.Action, r.From, r.To, r.Detail, r.Status]);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // utf-8-sig
        Undo.RotateLogs(LogDir, prefix, Keep);
        return path;
    }
}

/// <summary>Đọc/ghi CSV như module <c>csv</c> của Python (dialect excel, <c>newline=""</c>).</summary>
internal static class PyCsv
{
    /// <summary><c>csv.writer(quoting=QUOTE_ALL, lineterminator="\r\n")</c>.</summary>
    public static void WriteRowQuoteAll(StringBuilder sb, IEnumerable<string> fields)
    {
        var first = true;
        foreach (var f in fields)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append('"').Append(f.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        sb.Append("\r\n");
    }

    private enum St
    {
        StartRecord,
        StartField,
        InField,
        InQuoted,
        QuoteInQuoted,
        EatCrnl,
    }

    /// <summary><c>csv.reader</c>: danh sách bản ghi; dòng trống = bản ghi rỗng.</summary>
    public static List<List<string>> Rows(string text)
    {
        var rows = new List<List<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var st = St.StartRecord;

        void SaveField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            rows.Add(fields);
            fields = [];
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (st)
            {
                case St.StartRecord:
                    if (c is '\r' or '\n')
                    {
                        EndRecord(); // dòng trống -> []
                        st = St.EatCrnl;
                        i++;
                        continue;
                    }
                    st = St.StartField;
                    continue; // xử lý lại c
                case St.StartField:
                    if (c is '\r' or '\n')
                    {
                        SaveField();
                        EndRecord();
                        st = St.EatCrnl;
                    }
                    else if (c == '"')
                    {
                        st = St.InQuoted;
                    }
                    else if (c == ',')
                    {
                        SaveField();
                    }
                    else
                    {
                        field.Append(c);
                        st = St.InField;
                    }
                    break;
                case St.InField:
                    if (c is '\r' or '\n')
                    {
                        SaveField();
                        EndRecord();
                        st = St.EatCrnl;
                    }
                    else if (c == ',')
                    {
                        SaveField();
                        st = St.StartField;
                    }
                    else
                    {
                        field.Append(c);
                    }
                    break;
                case St.InQuoted:
                    if (c == '"')
                        st = St.QuoteInQuoted;
                    else
                        field.Append(c);
                    break;
                case St.QuoteInQuoted:
                    if (c == '"')
                    {
                        field.Append('"');
                        st = St.InQuoted;
                    }
                    else if (c == ',')
                    {
                        SaveField();
                        st = St.StartField;
                    }
                    else if (c is '\r' or '\n')
                    {
                        SaveField();
                        EndRecord();
                        st = St.EatCrnl;
                    }
                    else
                    {
                        field.Append(c); // strict=False: ký tự sau dấu đóng ngoặc được giữ
                        st = St.InField;
                    }
                    break;
                case St.EatCrnl:
                    if (c is '\r' or '\n')
                        break;
                    st = St.StartRecord;
                    continue;
            }
            i++;
        }
        if (st is St.StartField or St.InField or St.QuoteInQuoted || (st == St.InQuoted))
        {
            SaveField();
            EndRecord();
        }
        else if (fields.Count > 0)
        {
            EndRecord();
        }
        return rows;
    }

    /// <summary><c>csv.DictReader</c>: dòng đầu là tên cột; bỏ bản ghi rỗng; thiếu ô → <c>null</c> (restval); ô thừa bị bỏ.</summary>
    public static List<Dictionary<string, string?>> DictRows(string text)
    {
        var rows = Rows(text);
        var outp = new List<Dictionary<string, string?>>();
        if (rows.Count == 0)
            return outp;
        var names = rows[0];
        foreach (var row in rows.Skip(1))
        {
            if (row.Count == 0)
                continue;
            var d = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var k = 0; k < names.Count && k < row.Count; k++)
                d[names[k]] = row[k];
            for (var k = row.Count; k < names.Count; k++)
                d[names[k]] = null;
            outp.Add(d);
        }
        return outp;
    }
}

/// <summary>Gửi vào Thùng rác qua <c>SHFileOperationW</c> (như send2trash cũ): im lặng, không hỏi, không hiện lỗi.</summary>
internal static partial class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint WFunc;
        public IntPtr PFrom;
        public IntPtr PTo;
        public ushort FFlags;
        public int FAnyOperationsAborted;
        public IntPtr HNameMappings;
        public IntPtr LpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShFileOpStruct op);

    public static void Send(string path)
    {
        var full = Path.GetFullPath(path);
        var pFrom = Marshal.StringToHGlobalUni(full + "\0"); // danh sách kết thúc bằng hai ký tự NUL
        try
        {
            var op = new ShFileOpStruct
            {
                WFunc = FoDelete,
                PFrom = pFrom,
                FFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
            };
            var rc = SHFileOperation(ref op);
            if (rc != 0)
                throw new IOException($"SHFileOperation lỗi 0x{rc:X} khi đưa vào Thùng rác: {full}");
            if (op.FAnyOperationsAborted != 0)
                throw new IOException($"Đưa vào Thùng rác bị huỷ: {full}");
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
        }
    }
}
