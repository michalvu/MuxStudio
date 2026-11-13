using System.Globalization;
using System.IO;
using System.Windows.Controls;
using MuxStudio.Core;
using MuxStudio.Core.Ui;

namespace MuxStudio.App.Views;

/// <summary>Màn hình Hoàn tác chung ba bước (port <c>UndoDialog</c> của gui/app.py): danh sách tối đa 20 lần chạy mỗi bước, chọn một hoặc nhiều
/// lần. File tạo ra → Thùng rác; file đã đổi tên / di chuyển → trả về chỗ cũ. Không bao giờ xoá hẳn file của bạn.</summary>
public sealed class UndoDialog
{
    private readonly MainWindow _app;
    private readonly string _folder;
    private readonly Dlg _d;
    private readonly Table _t;
    private readonly Dictionary<string, UndoEntry> _entries = new(StringComparer.Ordinal);

    public UndoDialog(MainWindow app, string folder)
    {
        _app = app;
        _folder = folder;
        _d = new Dlg(app, "Hoàn tác", 1040, 560);
        _d.Text("Tick lần chạy muốn hoàn tác (mới nhất ở trên). File tạo ra → Thùng rác; file đã đổi tên / di chuyển → trả về chỗ cũ. Không bao giờ xoá hẳn file của bạn.");
        var g = _d.Fill(new DataGrid());
        g.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "WbGrid");
        _t = new Table(g, [new Col("step", "Bước", 150), new Col("when", "Lúc chạy", 150), new Col("what", "Thao tác", 360), new Col("note", "Ghi chú", 300, Stretch: true)],
                       check: "Chọn", single: true);
        Fill();
        _d.Button("Đóng", () => _d.Close(null));
        _d.Button("Hoàn tác các lần đã tick…", DoUndo, true);
    }

    private void Fill()
    {
        _t.Clear();
        _entries.Clear();
        var i = 0;
        foreach (var e in UndoUi.Entries(_folder, _app.Cfg))
        {
            var iid = "u" + (i++).ToString(CultureInfo.InvariantCulture);
            _entries[iid] = e;
            _t.Add(iid, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["step"] = e.Label, ["when"] = e.When, ["what"] = UndoUi.CountsText(e.Run.Counts), ["note"] = e.Run.Note,
            }, e.Run.Undoable ? "" : "dim", e.Run.Undoable ? false : null);
        }
        if (_entries.Count == 0)
        {
            _t.Add("none", new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = "Chưa có lần chạy nào trong thư mục này." }, "dim");
        }
    }

    private void DoUndo()
    {
        var sel = _t.CheckedIds().Where(_entries.ContainsKey).Select(i => _entries[i]).ToList();
        if (sel.Count == 0)
        {
            Dlg.Info(_d.Win, "Hoàn tác", "Tick ít nhất một lần chạy.");
            return;
        }
        var plans = new List<(UndoEntry E, UndoPlan P)>();
        var lines = new List<string>();
        foreach (var e in sel)
        {
            UndoPlan p;
            try
            {
                p = Undo.PlanUndo(e.Run.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException)
            {
                lines.Add(L.T($"[!] {e.Label} {e.When}: không đọc được nhật ký ({ex.Message})"));
                continue;
            }
            plans.Add((e, p));
            lines.Add(L.T($"=== {e.Label} — {e.When}"));
            var pl = UndoUi.PlanLines(p, _folder);
            lines.AddRange(pl.Count > 0 ? pl.Select(x => "   " + L.T(x)) : ["   " + L.T("(không có gì để hoàn tác)")]);
        }
        var rb = _app.Cfg.Bool("DeleteToRecycleBin");
        if (!Dlg.ConfirmList(_d.Win, "Xác nhận hoàn tác", string.Create(CultureInfo.InvariantCulture, $"Sẽ hoàn tác {plans.Count} lần chạy:"), lines, "Hoàn tác",
                             warn: rb ? "" : "Bạn đã TẮT 'Xoá vào Thùng rác' → file tạo ra sẽ bị XOÁ HẲN."))
            return;
        var ordered = plans.OrderByDescending(x => x.E.Stamp, StringComparer.Ordinal).ThenByDescending(x => x.E.Run.Name, StringComparer.Ordinal).ToList();
        _app.Runner.Start("Hoàn tác", job =>
        {
            var total = new UndoResult();
            for (var n = 0; n < ordered.Count; n++)
            {
                var (e, p) = ordered[n];
                job.Progress(n + 1, ordered.Count, $"{e.Label} {e.When}");
                var r = Undo.ExecuteUndo(p, rb);
                foreach (var d in r.Done)
                    job.Ok($"  [↶] {d}");
                foreach (var (path, why) in r.Skipped)
                    job.Warn($"  [-] {path}: {why}");
                total.Done.AddRange(r.Done);
                total.Skipped.AddRange(r.Skipped);
            }
            job.Head(string.Create(CultureInfo.InvariantCulture, $"HOÀN TÁC: {total.Done.Count} thao tác | {total.Skipped.Count} bỏ qua"));
            return total;
        }, _ =>
        {
            Fill();
            foreach (var v in _app.Views)
                v.OnFilesChanged();
        });
    }

    public void Run() => _d.Run();
}
