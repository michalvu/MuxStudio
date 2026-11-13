using System.Globalization;

namespace MuxStudio.Core.Ui;

/// <summary>Phần thuần của thẻ "① Tải phụ đề" (port gui/fetch_view.py).</summary>
public static class FetchUi
{
    public const string ResetText = "↺ Làm lại từ đầu";

    public static readonly IReadOnlyList<(string Key, string Label)> Sources =
        [("subsource", "SubSource"), ("titulky", "Titulky.com (cs)"), ("opensubtitles", "OpenSubtitles")];

    /// <summary><c>lang_label</c>: <c>vi — Vietnamese</c>.</summary>
    public static string LangLabel(string code)
    {
        var n = Naming.LangName(code);
        return n.Length > 0 && n.ToLowerInvariant() != code ? $"{code} — {n}" : code;
    }

    /// <summary><c>item_status</c>: (chữ, nhãn màu) cho cột Trạng thái của một dòng tập × ngôn ngữ.</summary>
    public static (string Text, string Tag) ItemStatus(FetchItem it)
    {
        if (it.Result is not null)
        {
            var what = it.ResultSrc == "có sẵn" ? "đã chép file có sẵn" : $"đã tải ({it.ResultSrc})";
            return ($"{what}: {Subprep.Name(it.Result)}",
                    it.TimingNote.Length == 0 || it.TimingNote.Contains("Khớp", StringComparison.Ordinal) ? "good" : "need");
        }
        if (it.Status == "lấy thất bại")
            return ("lấy thất bại (mọi bản đều không lấy được)", "bad");
        if (it.Already is not null)
        {
            if (it.AlreadyNote.StartsWith("trong MKV", StringComparison.Ordinal))
                return ($"phim đã có ({it.AlreadyNote}) — không tìm", "dim");
            if (it.AlreadyNote.Length > 0)
                return ($"phim đã có ({it.AlreadyNote}): {Subprep.Name(it.Already)} — không tìm", "dim");
            return ($"đã có: {Subprep.Name(it.Already)}", "dim");
        }
        if (it.Existing is not null)
            return ($"dùng file có sẵn ({it.Existing.Reason}): {Subprep.Name(it.Existing.File)}", "good");
        if (it.Skip)
            return ("bỏ qua", "dim");
        if (it.Chosen is not null)
            return ($"đã chọn ({it.ChoiceReason})", "ok");
        if (it.NeedsChoice)
            return ($"CẦN CHỌN — {it.Candidates.Count.ToString(CultureInfo.InvariantCulture)} ứng viên", "need");
        if (it.Gathered)
            return (it.Status.Length > 0 ? it.Status : "không tìm thấy", "bad");
        return ("chưa tìm", "");
    }

    public static string ChosenText(FetchItem it)
    {
        if (it.Chosen is not null)
            return $"[{it.Chosen.Src}] {it.Chosen.Release}";
        if (it.Existing is not null)
            return Subprep.Name(it.Existing.File);
        return "";
    }

    /// <summary><c>candidate_row</c>: một dòng của bảng ứng viên (thay <c>Show-CandidateTable</c>).</summary>
    public static OrderedDictionary<string, string> CandidateRow(Candidate c, int i) => new(StringComparer.Ordinal)
    {
        ["n"] = (i + 1).ToString(CultureInfo.InvariantCulture),
        ["src"] = c.Src,
        ["fps"] = c.FpsLabel,
        ["score"] = ((long)Math.Round(c.Score * 100)).ToString(CultureInfo.InvariantCulture) + "%",
        ["off"] = c.OffsetLabel,
        ["rel"] = c.Release,
        ["info"] = c.Info,
    };

    public static string CandidateTag(Candidate c)
    {
        if (c.Tier == 3)
            return "bad";
        if (c.Off is not null && c.Off.Total < 0)
            return "good";
        return "";
    }

    public static string VideoSummary(IReadOnlyList<FetchItem> items)
    {
        var need = items.Count(it => it.NeedsChoice);
        var done = items.Count(it => it.Result is not null || it.Already is not null || it.Existing is not null || it.Chosen is not null);
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{done}/{items.Count} ngôn ngữ xong/đã chọn") };
        if (need > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{need} cần chọn"));
        return string.Join(", ", parts);
    }

    /// <summary><c>summary_text</c>: bảng tổng kết (§5.1 mục 5).</summary>
    public static string SummaryText(FetchReport rep)
    {
        var s = string.Create(CultureInfo.InvariantCulture,
            $"Tải được: {rep.Ok}  |  Dùng file có sẵn: {rep.UsedExisting}  |  Đã có từ trước: {rep.Already}  |  Thiếu: {rep.Missing}  |  Bỏ qua: {rep.Skipped}");
        if (rep.OsRemaining >= 0)
            s += string.Create(CultureInfo.InvariantCulture, $"  |  OpenSubtitles còn {rep.OsRemaining}/{rep.OsAllowed} lượt tải hôm nay");
        if (rep.Stopped)
            s += "  |  (đã dừng giữa chừng)";
        return s;
    }

    /// <summary><c>gather_all</c>: gom ứng viên + tự chọn cho mọi dòng chưa xong (KHÔNG hỏi: dòng cần bạn chọn để lại).</summary>
    public static void GatherAll(FetchSession s, IReadOnlyList<FetchItem> items, Job job)
    {
        var todo = items.Where(it => !(it.Already is not null || it.Existing is not null || it.Skip || it.Result is not null || it.Chosen is not null))
                        .ToList();
        for (var n = 1; n <= todo.Count; n++)
        {
            var it = todo[n - 1];
            if (job.ShouldStop())
            {
                job.Warn("[!] Đã dừng tìm phụ đề (các dòng còn lại để sau).");
                break;
            }
            job.Progress(n, todo.Count, $"{Subprep.Name(it.Video.Path)} [{it.Lang}]");
            job.Head($"=== {Subprep.Name(it.Video.Path)} [{it.Lang}]");
            s.Gather(it);
            if (it.Candidates.Count > 0)
            {
                s.AutoSelect(it);
                EnsurePack(s, it);
            }
        }
    }

    /// <summary><c>ensure_pack</c>: bản đã chọn là gói season SubSource chưa tải → tải trước (miễn phí) để đo thẳng file của từng tập.</summary>
    public static bool EnsurePack(FetchSession s, FetchItem it)
    {
        var c = it.Chosen;
        if (c is null || c.Src != "SubSource" || !c.IsPack || s.Ss is null)
            return false;
        if (s.Ss.IsDownloaded(Py.ToInt(c.PayloadDict.Get("subtitleId"))))
            return false;
        return s.Prefetch(c);
    }

    /// <summary><c>after_choice</c>: sau khi bạn chọn cho một dòng: tải trước gói (nếu là gói season), đo lại các tập cùng season, rồi
    /// dùng lại lựa chọn cho các dòng còn "cần chọn" (<see cref="ChoiceMemory"/>). Trả số dòng được tự chọn thêm.</summary>
    public static int AfterChoice(FetchSession s, FetchItem it, IReadOnlyList<FetchItem> items, Job job)
    {
        var gotPack = EnsurePack(s, it);
        var rest = items.Where(x => !ReferenceEquals(x, it) && x.NeedsChoice && x.MemoryKey == it.MemoryKey).ToList();
        if (gotPack && rest.Count > 0)
        {
            job.Info(string.Create(CultureInfo.InvariantCulture, $"[i] Đã tải gói season → đo lại {rest.Count} tập cùng season."));
            s.Remeasure(rest);
        }
        var n = 0;
        if (s.Cfg.Bool("RememberChoice"))
        {
            foreach (var x in rest)
            {
                if (x.NeedsChoice && s.AutoSelect(x) is not null)
                    n++;
            }
        }
        return n;
    }
}
