using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MuxStudio.Core;
using MuxStudio.Core.Ui;

namespace MuxStudio.App.Views;

/// <summary>Một lựa chọn của ô chọn: giá trị gốc (tiếng Việt — mã so sánh) + chữ hiển thị (đã dịch) — = <c>W.TrChoice</c>.</summary>
public sealed record Choice(string Value, string Shown)
{
    public static Choice Of(string value) => new(value, L.T(value));
}

/// <summary>
/// Thẻ "② Chuẩn bị phụ đề" (port gui/subprep_view.py — thay SubPrep): đếm file; mốc đồng bộ (phụ đề gốc trong <c>srt_goc</c> hoặc audio của
/// file phim, bảng audio của MỌI file); ngôn ngữ (tự nhận diện / một ngôn ngữ); trích phụ đề gốc; xem trước + đổi tên; lập cặp + chạy đồng bộ
/// (sức khoẻ, dịch đầu / cuối, chênh, còn lệch, mốc khớp, recap đã cắt, cờ); lọc "chỉ hiện file cần xem", xuất CSV, "Xem lệnh"; dọn dẹp.
/// </summary>
public partial class PrepView : UserControl, IStepView
{
    private readonly MainWindow _app;
    private readonly StepBar _steps;
    private readonly Table _atable;
    private readonly Table _rtable;
    private readonly Table _stable;
    private PrepSession? _session;
    private List<RenameRow> _renames = [];
    private List<SyncPair> _pairs = [];
    private List<VideoAudio> _audioFiles = [];
    private PrepCounts? _counts;
    private bool _renamesPlanned;
    private bool _pairsPlanned;
    private bool _cleaned;
    private bool _loading = true;
    private string _amode = "";

    public PrepView(MainWindow app)
    {
        _app = app;
        InitializeComponent();
        TabRename.Header = L.T(PrepUi.InnerRename);
        TabSync.Header = L.T(PrepUi.InnerSync);
        SetLangOptions(PrepUi.LangOptions(), PrepUi.AutoLang);
        _steps = new StepBar(StepsPanel,
        [
            ("extract", "Trích phụ đề gốc", PlanExtract), ("preview", "Quét phụ đề", PlanRename), ("rename", "Đổi tên", DoRename),
            ("pairs", "Lập cặp đồng bộ", PlanSync), ("sync", "Chạy đồng bộ", DoSync), ("cleanup", "Dọn dẹp", Cleanup),
            ("next", "Sang ③ Mux →", () => _app.Goto(MainWindow.TabMux)),
        ], ["next"]);
        _atable = new Table(AudioGrid,
        [
            new Col("ep", "Tập", 70), new Col("file", "File phim", 320, Stretch: true, Name: true), new Col("idx", "Số thứ tự", 70, "e"),
            new Col("lang", "Ngôn ngữ", 80), new Col("codec", "Codec", 80), new Col("ch", "Kênh", 50, "e"), new Col("title", "Tên track", 220, Stretch: true),
            new Col("def", "Default", 60, Badge: true), new Col("ref", "Làm mốc", 260, Badge: true),
        ], single: true)
        {
            OnSelect = AudioSelected,
        };
        AudioGrid.MaxHeight = 28 * (app.Density switch { "compact" => 3, "large" => 8, _ => 4 }) + 34;
        _rtable = new Table(RenameGrid,
        [
            new Col("ep", "Tập", 80), new Col("old", "Tên cũ", 380, Stretch: true, Name: true), new Col("new", "→ Tên mới", 380, Stretch: true, Name: true),
            new Col("lang", "Ngôn ngữ", 90, Badge: true), new Col("why", "Lý do / cảnh báo", 280, Stretch: true, Badge: true),
        ], check: "Đổi")
        {
            OnToggle = (iid, st) =>
            {
                _renames[Idx(iid)].Selected = st;
                UpdateButtons();
            },
            OnToggleAll = st =>
            {
                foreach (var iid in _rtable!.Checked.Keys)
                    _renames[Idx(iid)].Selected = st;
                UpdateButtons();
            },
            Tip = (iid, _) => iid.StartsWith('r') ? PrepUi.RenameTip(_renames[Idx(iid)]) : null,
        };
        _stable = new Table(SyncGrid,
        [
            new Col("sub", "Phụ đề", 260, Stretch: true, Name: true), new Col("ref", "Mốc", 220, Name: true), new Col("kind", "Loại mốc", 96, Badge: true),
            new Col("out", "Kết quả (cạnh phim)", 240, Stretch: true, Name: true), new Col("forecast", "Dự báo", 220), new Col("health", "Sức khoẻ", 110, Badge: true),
            new Col("first", "Dịch đầu", 75, "e"), new Col("last", "Dịch cuối", 75, "e"), new Col("spread", "Chênh", 65, "e"),
            new Col("resid", "Còn lệch", 75, "e"), new Col("votes", "Mốc khớp", 75, "e"), new Col("cut", "Cắt recap", 80, "e", Badge: true), new Col("flags", "Cờ / lý do", 360),
        ], check: "Chạy")
        {
            OnToggle = (iid, st) =>
            {
                _pairs[Idx(iid)].Selected = st;
                UpdateButtons();
            },
            OnToggleAll = st =>
            {
                foreach (var iid in _stable!.Checked.Keys)
                    _pairs[Idx(iid)].Selected = st;
                UpdateButtons();
            },
            Tip = (iid, _) => iid.StartsWith('p') ? PrepUi.PairTip(_pairs[Idx(iid)]) : null,
            OnDouble = (iid, _) => ShowCommands(iid),
        };
        _loading = false;
        OnConfigChanged(false);
    }

    public FrameworkElement Root => this;

    private static int Idx(string iid) => int.Parse(iid.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>Mốc = audio của file phim (cho thẻ trạng thái ở cột trái).</summary>
    public bool IsAudioMode => ModeAudio.IsChecked == true;

    /// <summary>Số file đếm lần gần nhất (thẻ "WORKBENCH STATE"); <c>null</c> = chưa đếm.</summary>
    public PrepCounts? Counts => _counts;

    private string Mode() => IsAudioMode ? PrepUi.ModeAudio : PrepUi.ModeSub;

    // ------------------------------------------------------------------ ngon ngu
    private void SetLangOptions(IReadOnlyList<string> opts, string selected)
    {
        _loading = true;
        var items = opts.Select(Choice.Of).ToList();
        LangBox.ItemsSource = items;
        LangBox.SelectedItem = items.FirstOrDefault(c => c.Value == selected) ?? items[0];
        _loading = false;
    }

    private string LangValue() => (LangBox.SelectedItem as Choice)?.Value ?? PrepUi.AutoLang;

    private string Lang() => PrepUi.LangFromOption(LangValue());

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LangBox.SelectedItem is not Choice c || c.Value != PrepUi.OtherLang)
            return;
        var code = (Dlg.AskString(this, "Ngôn ngữ khác", "Nhập mã ngôn ngữ (vd ja, pt, ru):") ?? "").Trim().ToLowerInvariant().TrimStart('.');
        if (code.Length == 0)
        {
            SetLangOptions(((IEnumerable<Choice>)LangBox.ItemsSource).Select(x => x.Value).ToList(), PrepUi.AutoLang);
            return;
        }
        code = Naming.LangFromWord(code) is { Length: > 0 } w ? w : code;
        Naming.RegisterLanguage(code);
        var opts = PrepUi.LangOptions([code]);
        SetLangOptions(opts, opts.FirstOrDefault(o => PrepUi.LangFromOption(o) == code) ?? PrepUi.AutoLang);
    }

    // ------------------------------------------------------------------ moc
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _steps is null)
            return;
        var audio = IsAudioMode;
        // moc = audio: khong can phu de goc -> an han buoc "Trich phu de goc" (cac buoc con lai danh so lai)
        _steps.SetHidden(audio ? ["extract"] : []);
        AudioBox.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        if (audio && _audioFiles.Count == 0)
            LoadStreams();
        ShowExtractHint();
        ShowCounts();
        _pairs = [];
        _pairsPlanned = false;
        FillPairs();
        UpdateButtons();
        _app.UpdateState();
    }

    /// <summary>Dòng nhắc "srt_goc đang trống": chỉ khi mốc = phụ đề gốc.</summary>
    private void ShowExtractHint()
    {
        var c = _counts;
        if (Mode() == PrepUi.ModeSub && c is not null && c.Refs == 0 && c.Videos > 0)
        {
            ExtractHint.Text = L.T($"'{_app.Cfg.Str("ExtractFolder")}' đang trống → bấm \"1. Trích phụ đề gốc\" (hoặc chọn mốc = Audio của file phim).");
            ExtractHint.Visibility = Visibility.Visible;
        }
        else
        {
            ExtractHint.Text = "";
            ExtractHint.Visibility = Visibility.Collapsed;
        }
    }

    private void SetAMode(string m)
    {
        _amode = m;
        _loading = true;
        AModeAuto.IsChecked = m == "auto";
        AModeIndex.IsChecked = m == "index";
        AModeLang.IsChecked = m == "lang";
        _loading = false;
    }

    private void AMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _amode = AModeAuto.IsChecked == true ? "auto" : AModeIndex.IsChecked == true ? "index" : AModeLang.IsChecked == true ? "lang" : "";
        MarkAudio();
    }

    private void ALang_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
            MarkAudio();
    }

    private void AudioSelected(List<string> ids)
    {
        if (ids.Count > 0 && !ids[0].EndsWith("_x", StringComparison.Ordinal))
            SetAMode("index");
        MarkAudio();
    }

    private AudioChoice? AudioChoiceNow()
    {
        var cfg = _app.Cfg;
        switch (_amode)
        {
            case "auto":
                return new AudioChoice("auto");
            case "index":
                var sel = _atable.Selected().Where(i => !i.EndsWith("_x", StringComparison.Ordinal)).ToList();
                return new AudioChoice("index", sel.Count > 0 ? PrepUi.AudioIidIndex(sel[0]) : cfg.Int("SyncAudioIndex"));
            case "lang":
                var lg = ALang.Text.Trim().ToLowerInvariant();
                return new AudioChoice("lang", lg.Length > 0 ? lg : cfg.Str("SyncAudioLang"));
            default:
                return null;
        }
    }

    public void OnConfigChanged(bool secretsChanged)
    {
        var cfg = _app.Cfg;
        ModeSub.Content = new TextBlock { Text = L.T($"Phụ đề gốc trong '{cfg.Str("ExtractFolder")}'") }; // TextBlock: giu dau "_"
        var m = cfg.Str("SyncAudioMode");
        SetAMode(m == "ask" ? "" : m);
        _loading = true;
        ALang.Text = cfg.Str("SyncAudioLang");
        _loading = false;
        if (_session is not null)
            Reset("Cài đặt đã đổi → lập lại bảng ở thẻ ②.");
        RefreshCounts();
    }

    public void OnFolderChanged()
    {
        Reset();
        RefreshCounts();
        ReloadAudio();
    }

    public void OnFilesChanged()
    {
        Reset("File trong thư mục đã thay đổi (hoàn tác) → lập lại bảng ở thẻ ②.");
        RefreshCounts();
        ReloadAudio();
    }

    private void ReloadAudio()
    {
        if (Mode() == PrepUi.ModeAudio && _app.Folder is not null)
            LoadStreams();
    }

    public void OnShow() => RefreshCounts();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Nut "↺ Lam lai tu dau": moi bang + tien do ve nhu chua lam gi (phien moi = nhat ky moi). Khong xoa file.
        if (_app.Runner.Busy)
            return;
        var pending = _renames.Count(r => r.Ok && r.Selected && r.Status.Length == 0) + _pairs.Count(p => p.Ok && p.Selected && p.Result is null);
        if (pending > 0 && !Dlg.AskYesNo(this, "Làm lại từ đầu", string.Create(CultureInfo.InvariantCulture,
                $"Còn {pending} dòng CHƯA chạy trong các bảng. Xoá hết bảng và tiến độ của lượt này để bắt đầu lại?\n\n(Không xoá file nào.)")))
            return;
        Reset();
        OnlyBad.IsChecked = false;
        Inner.SelectedIndex = 0;
        _app.Log("info", "② Đã làm lại từ đầu: các bảng và thanh các bước về mặc định (không xoá file nào).");
        RefreshCounts();
        ReloadAudio();
    }

    private void Reset(string note = "")
    {
        _session?.Dispose();
        _session = null;
        _renames = [];
        _pairs = [];
        _counts = null;
        ShowCounts();
        _renamesPlanned = _pairsPlanned = _cleaned = false;
        _audioFiles = [];
        _atable.Clear();
        AudioTitle.Text = L.T("Audio track của các file phim:");
        AudioTitle.SetResourceReference(TextBlock.ForegroundProperty, "LogInfoBrush");
        FillRenames();
        FillPairs();
        RenameInfo.Text = "";
        SyncInfo.Children.Clear();
        if (note.Length > 0)
            _app.Log("warn", note);
        UpdateButtons();
    }

    /// <summary>Một phiên cho cả lượt (đổi tên + đồng bộ + dọn dẹp ghi chung MỘT nhật ký). Chạy ở luồng phụ.</summary>
    private PrepSession Session(Job job)
    {
        var folder = _app.Folder!;
        if (_session is null || Py2.NormCase(_session.Td) != Py2.NormCase(folder))
        {
            _session?.Dispose();
            _session = new PrepSession(folder, _app.Cfg, job);
        }
        else
        {
            _session.SetJob(job);
        }
        return _session;
    }

    public void UpdateButtons()
    {
        var busy = _app.Runner.Busy;
        var folder = _app.Folder is not null;
        var okRen = _renames.Any(r => r.Ok && r.Selected && r.Status.Length == 0);
        var okPairs = _pairs.Any(p => p.Ok && p.Selected && p.Result is null);
        var processed = _session is not null && _session.Processed.Count > 0;
        var c = _counts;
        var synced = _pairsPlanned && !okPairs;
        _steps.Update(
        [
            Mode() == PrepUi.ModeAudio || (c is not null && c.Refs > 0), // moc = audio: khong can phu de goc
            _renamesPlanned, _renamesPlanned && !okRen, _pairsPlanned, synced,
            _cleaned, // chi ✓ khi da qua hop thoai don dep cua LUOT NAY
            false,
        ], [folder, folder, okRen, folder, okPairs, processed, true], busy);
        RecountBtn.IsEnabled = folder && !busy;
        ResetBtn.IsEnabled = folder && !busy;
        CmdBtn.IsEnabled = _pairs.Any(p => p.Ok) && !busy;
        ExportBtn.IsEnabled = _pairs.Count > 0 && !busy;
    }

    // ------------------------------------------------------------------ 1. dem
    private void Recount_Click(object sender, RoutedEventArgs e) => RefreshCounts();

    /// <summary>Đếm lại file (việc TỰ ĐỘNG, không đè dòng trạng thái): ô thống kê của thẻ ② và thẻ "WORKBENCH STATE".</summary>
    public void RefreshCounts()
    {
        if (_app.Folder is null)
        {
            ShowCounts();
            return;
        }
        _app.Runner.Start("Đếm file (bước ②)", job => Session(job).Counts(), c =>
        {
            _counts = c;
            ShowCounts();
            ShowExtractHint();
            UpdateButtons();
            _app.UpdateState();
        }, whenFree: true, quiet: true);
    }

    /// <summary>Ba ô thống kê: số phim, số phụ đề trong <c>srt_can_sync</c>, số phụ đề gốc trong <c>srt_goc</c> (đỏ khi trống mà mốc = phụ đề).</summary>
    private void ShowCounts()
    {
        var cfg = _app.Cfg;
        var c = _counts;
        CountTiles.Children.Clear();
        string N(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "—";
        CountTiles.Children.Add(Chips.Tile("Phim", N(c?.Videos), c is { Videos: > 0 } ? "good" : "", 150));
        CountTiles.Children.Add(Chips.Tile($"Phụ đề trong '{cfg.Str("SubFolderName")}'", N(c?.Subs), c is { Subs: > 0 } ? "info" : "", 230));
        CountTiles.Children.Add(Chips.Tile($"Phụ đề gốc trong '{cfg.Str("ExtractFolder")}'", N(c?.Refs),
                                           c is null ? "" : c.Refs > 0 ? "info" : Mode() == PrepUi.ModeSub ? "bad" : "dim", 230));
        if (_app.Folder is null)
            CountTiles.Children.Add(Chips.Make("Chọn thư mục phim ở đầu cửa sổ.", "dim"));
    }

    // ------------------------------------------------------------------ 2. audio cua moi file
    private void LoadStreams()
    {
        if (_app.Folder is null)
            return;
        _app.Runner.Start("Đọc audio track của các file phim", job => Session(job).AllAudioStreams(), files =>
        {
            _audioFiles = files;
            FillAudio();
        }, whenFree: true);
    }

    private void FillAudio()
    {
        _atable.Clear();
        var (txt, level) = PrepUi.AudioSummary(_audioFiles);
        AudioTitle.Text = L.T(_audioFiles.Count != 1 ? txt : $"Audio track của file phim: {UiText.ShortenName(Path.GetFileName(_audioFiles[0].Video), 90)}");
        AudioTitle.SetResourceReference(TextBlock.ForegroundProperty, Dlg.LevelBrush(level == "info" ? "" : level));
        foreach (var (iid, vals, tag) in PrepUi.AudioRows(_audioFiles, AudioChoiceNow()))
        {
            vals["~ref"] = vals["ref"].Length > 0 ? "good" : "";
            vals["~def"] = vals["def"].Length > 0 ? "info" : "";
            _atable.Add(iid, vals, tag);
        }
        if (_amode == "index") // so thu tu trong Cai dat -> chon san dong do cua file dau
            _atable.Select(PrepUi.AudioIid(0, _app.Cfg.Int("SyncAudioIndex")));
    }

    /// <summary>Đổi cách chọn audio / chọn dòng khác → cập nhật cột "Làm mốc" (không dựng lại bảng: giữ dòng đang chọn).</summary>
    private void MarkAudio()
    {
        foreach (var (iid, vals, _) in PrepUi.AudioRows(_audioFiles, AudioChoiceNow()))
        {
            if (_atable.Full(iid, "ref") != vals["ref"])
                _atable.Update(iid, new Dictionary<string, string>(StringComparer.Ordinal) { ["ref"] = vals["ref"], ["~ref"] = vals["ref"].Length > 0 ? "good" : "" });
        }
    }

    // ------------------------------------------------------------------ 4. trich
    private void PlanExtract()
    {
        if (_app.NeedFolder() is null)
            return;
        _app.Runner.Start("Lập kế hoạch trích phụ đề gốc", job => Session(job).PlanExtractRefs(), ExtractDialog);
    }

    private void ExtractDialog(List<ExtractRow> rows)
    {
        if (rows.Count == 0)
        {
            Dlg.Info(this, "Trích phụ đề gốc", "Không có file MKV nào để trích (xem nhật ký ở dưới).");
            return;
        }
        var d = new Dlg(this, "Trích phụ đề gốc từ MKV", 1100, 560);
        var nOk = rows.Count(r => r.Ok);
        d.Text(string.Create(CultureInfo.InvariantCulture, $"{nOk}/{rows.Count} file sẽ được trích phụ đề không SDH (theo thứ tự ngôn ngữ trong Cài đặt; ")
               + $"chỉ có PGS thì đọc mốc thời gian) vào '{_app.Cfg.Str("ExtractFolder")}'. Bỏ tick dòng không muốn.");
        var g = d.Fill(new DataGrid());
        g.SetResourceReference(StyleProperty, "WbGrid");
        var t = new Table(g,
        [
            new Col("video", "File phim", 400, Stretch: true, Name: true), new Col("track", "Track", 300), new Col("dest", "Ghi thành", 260, Name: true),
            new Col("why", "Lý do bỏ qua", 200),
        ], check: "Trích");
        for (var i = 0; i < rows.Count; i++)
        {
            var (vals, tag) = PrepUi.ExtractValues(rows[i]);
            t.Add(string.Create(CultureInfo.InvariantCulture, $"x{i}"), vals, tag, rows[i].Ok ? true : null);
        }
        d.Button("Trích các dòng đã tick", () =>
        {
            var chk = t.Checked;
            for (var i = 0; i < rows.Count; i++)
                rows[i].Selected = chk.GetValueOrDefault(string.Create(CultureInfo.InvariantCulture, $"x{i}"));
            d.Close(true);
        }, true);
        d.Button("Huỷ", () => d.Close(null));
        if (d.Run() is not true)
            return;
        _app.Runner.Start("Trích phụ đề gốc", job => Session(job).ExtractRefs(rows), _ => RefreshCountsLater());
    }

    private void RefreshCountsLater()
    {
        Dispatcher.BeginInvoke(RefreshCounts, System.Windows.Threading.DispatcherPriority.Background);
        UpdateButtons();
    }

    // ------------------------------------------------------------------ 5. doi ten
    private void PlanRename()
    {
        if (_app.NeedFolder() is null)
            return;
        var mode = Mode();
        var lang = Lang();
        _app.Runner.Start("Quét phụ đề (xem trước đổi tên)", job => Session(job).PlanRename(mode, lang), rows =>
        {
            _renames = rows;
            _renamesPlanned = true;
            _cleaned = false; // quet lai = luot moi: buoc "Don dep" khong con ✓ cua luot truoc
            if (_pairsPlanned && !_pairs.Any(p => p.Ok && p.Selected && p.Result is null))
            {
                _pairs = []; // cac cap cu da chay xong -> bang dong bo lam lai
                _pairsPlanned = false;
                FillPairs();
                SyncInfo.Children.Clear();
            }
            FillRenames();
            Inner.SelectedIndex = 0;
            UpdateButtons();
        });
    }

    private void FillRenames()
    {
        _rtable.Clear();
        int nOk = 0, nWarn = 0;
        for (var i = 0; i < _renames.Count; i++)
        {
            var r = _renames[i];
            var (vals, tag) = PrepUi.RenameValues(r);
            vals["~lang"] = r.Lang.Length > 0 ? "info" : "bad";
            vals["~why"] = vals["why"].Length > 0 ? tag : "";
            _rtable.Add(string.Create(CultureInfo.InvariantCulture, $"r{i}"), vals, tag, r.Ok && r.Status.Length == 0 ? r.Selected : null);
            nOk += r.Ok ? 1 : 0;
            nWarn += r.Warn.Length > 0 ? 1 : 0;
        }
        if (_renames.Count > 0)
        {
            var txt = string.Create(CultureInfo.InvariantCulture, $"{nOk} file sẽ đổi tên, {_renames.Count - nOk} bỏ qua");
            if (nWarn > 0)
                txt += string.Create(CultureInfo.InvariantCulture, $" — {nWarn} file nằm trong thư mục ngôn ngữ KHÁC lựa chọn (đỏ)");
            RenameInfo.Text = L.T(txt);
            RenameInfo.SetResourceReference(TextBlock.ForegroundProperty, nWarn > 0 ? "LogErrorBrush" : "LogInfoBrush");
        }
    }

    private void DoRename()
    {
        var rows = _renames;
        var todo = rows.Where(r => r.Ok && r.Selected && r.Status.Length == 0).ToList();
        if (todo.Count == 0)
            return;
        var warn = todo.Count(r => r.Warn.Length > 0);
        var msg = string.Create(CultureInfo.InvariantCulture, $"Đổi tên {todo.Count} file?");
        if (warn > 0)
            msg += string.Create(CultureInfo.InvariantCulture, $"\n\n⚠ {warn} file nằm trong thư mục ngôn ngữ KHÁC lựa chọn của bạn — kiểm tra kỹ!");
        if (!Dlg.AskYesNo(this, "Đổi tên", msg))
            return;
        _app.Runner.Start("Đổi tên phụ đề", job => Session(job).ExecuteRename(rows), rep =>
        {
            FillRenames();
            RenameInfo.Text = L.T(string.Create(CultureInfo.InvariantCulture, $"Đã đổi tên {rep.Ok} file | {rep.Skipped} bỏ qua | {rep.Errors} lỗi"));
            _pairs = [];
            _pairsPlanned = false;
            FillPairs();
            UpdateButtons();
            if (rep.Ok > 0)
            {
                _app.Log("info", "→ Bấm \"Lập cặp đồng bộ\".");
                Inner.SelectedIndex = 1;
            }
            RefreshCountsLater();
        });
    }

    // ------------------------------------------------------------------ 6. dong bo
    private void PlanSync()
    {
        if (_app.NeedFolder() is null)
            return;
        var mode = Mode();
        var lang = Lang();
        AudioChoice? audio = null;
        if (mode == PrepUi.ModeAudio)
        {
            audio = AudioChoiceNow();
            if (audio is null)
            {
                Dlg.Warn(this, "Chọn audio làm mốc", "Chọn cách lấy audio làm mốc: Tự động / Theo số thứ tự / Theo ngôn ngữ (ở bảng audio phía trên).");
                return;
            }
        }
        _app.Runner.Start("Lập cặp đồng bộ", job => Session(job).PlanSync(mode, audio, lang), pairs =>
        {
            _pairs = pairs;
            _pairsPlanned = true;
            FillPairs();
            Inner.SelectedIndex = 1;
            UpdateButtons();
        });
    }

    private void OnlyBad_Click(object sender, RoutedEventArgs e) => FillPairs();

    private void FillPairs()
    {
        _stable.Clear();
        var only = OnlyBad.IsChecked == true;
        for (var i = 0; i < _pairs.Count; i++)
        {
            var p = _pairs[i];
            if (only && !PrepUi.PairNeedsReview(p))
                continue;
            var (vals, tag) = PrepUi.PairValues(p);
            foreach (var (k, v) in WorkbenchUi.PairKinds(p, tag))
                vals[k] = v;
            _stable.Add(string.Create(CultureInfo.InvariantCulture, $"p{i}"), vals, tag, p.Ok && p.Result is null ? p.Selected : null);
        }
        // nhan dem: truoc khi chay = se dong bo / bo qua; sau khi chay = TOT / CAN XEM / CO VAN DE / loi
        Chips.Fill(SyncInfo, WorkbenchUi.SyncChips(_pairs));
    }

    private void Cmd_Click(object sender, RoutedEventArgs e) => ShowCommands(null);

    private void ShowCommands(string? iid)
    {
        var s = _session;
        if (s is null)
            return;
        if (iid is null)
        {
            var sel = _stable.Selected();
            iid = sel.Count > 0 ? sel[0] : null;
        }
        var ps = iid is not null ? [_pairs[Idx(iid)]] : _pairs.Where(p => p.Ok).Take(20).ToList();
        var one = iid is not null;
        _app.Runner.Start("Dựng lệnh", job =>
        {
            s.SetJob(job);
            var lines = new List<string>();
            foreach (var p in ps)
            {
                var cmds = s.PreviewCommands(p);
                if (cmds.Count > 0)
                {
                    lines.Add($"### {Path.GetFileName(p.Sub)}");
                    lines.AddRange(cmds);
                    lines.Add("");
                }
            }
            return lines;
        }, lines =>
        {
            if (lines.Count == 0)
            {
                Dlg.Info(this, "Xem lệnh", "Dòng này không có lệnh (bị bỏ qua — xem cột Cờ / lý do).");
                return;
            }
            Dlg.ShowText(this, "Xem lệnh (không ghi file nào)", string.Join("\n", lines),
                         "Đúng các lệnh ffmpeg / alass sẽ chạy cho " + (one ? "dòng đã chọn." : "các cặp (tối đa 20)."));
        });
    }

    private void DoSync()
    {
        var pairs = _pairs;
        var todo = pairs.Count(p => p.Ok && p.Selected && p.Result is null);
        if (todo == 0)
            return;
        if (!Dlg.AskYesNo(this, "Chạy đồng bộ", string.Create(CultureInfo.InvariantCulture,
                $"Đồng bộ {todo} phụ đề bằng alass?\n\n(Kết quả ghi cạnh file phim; file trong srt_can_sync không bị sửa.)")))
            return;
        _app.Runner.Start("Đồng bộ phụ đề", job => Session(job).ExecuteSync([.. pairs.Where(p => p.Result is null)]), _ =>
        {
            FillPairs();
            UpdateButtons();
            if (_session is { Processed.Count: > 0 } && _app.Cfg.Bool("AskCleanup"))
                Dispatcher.BeginInvoke(Cleanup, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_pairs.Count == 0)
            return;
        var dlg = new SaveFileDialog { Title = L.T("Xuất CSV"), FileName = "dong_bo.csv", DefaultExt = ".csv", Filter = "CSV (*.csv)|*.csv" };
        if (_app.Folder is not null)
            dlg.InitialDirectory = _app.Folder;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true)
            return;
        var n = PrepUi.ExportCsv(_pairs, dlg.FileName);
        _app.Log("ok", string.Create(CultureInfo.InvariantCulture, $"Đã xuất {n} dòng: {dlg.FileName}"));
    }

    // ------------------------------------------------------------------ 7. don dep
    private void Cleanup()
    {
        var s = _session;
        // ban C#: chi don phu de cua ngon ngu dang chon o o "Ngon ngu" (xem HANDOFF_DOTNET.md, GĐ3)
        var plan = s?.CleanupTargets(Lang());
        if (s is null || plan is null)
        {
            Dlg.Info(this, "Dọn dẹp", "Không có file nguồn nào để dọn (chỉ dọn file của cặp đã đồng bộ thành công).");
            return;
        }
        var cfg = _app.Cfg;
        var d = new Dlg(this, "Dọn dẹp file nguồn", 900, 520);
        d.Text(string.Create(CultureInfo.InvariantCulture, $"{plan.Done} phụ đề đã đồng bộ xong. Xoá file nguồn đã dùng? Nơi xoá: {plan.Where}."),
               plan.ToRecycleBin ? "" : "error");
        var root = _app.Folder ?? "";
        var txt = L.T(string.Create(CultureInfo.InvariantCulture, $"— '{cfg.Str("SubFolderName")}' ({plan.SubFiles.Count} file):\n"))
                  + string.Join("\n", PrepUi.CleanupLines(plan, "subs", root).Select(x => "   " + x))
                  + L.T(string.Create(CultureInfo.InvariantCulture, $"\n\n— '{cfg.Str("ExtractFolder")}' ({plan.RefFiles.Count} file):\n"))
                  + string.Join("\n", PrepUi.CleanupLines(plan, "refs", root).Select(x => "   " + x));
        d.Fill(Dlg.Console(txt));
        d.Button("Không xoá", () => d.Close("none"));
        d.Button($"Chỉ '{cfg.Str("ExtractFolder")}'", () => d.Close("refs"));
        d.Button($"Chỉ '{cfg.Str("SubFolderName")}'", () => d.Close("subs"));
        d.Button("Xoá cả hai", () => d.Close("both"), true);
        var which = d.Run() as string;
        _cleaned = true; // da hoi (ke ca "Khong xoa") -> buoc ke tiep: sang ③
        UpdateButtons();
        if (which is null or "none")
            return;
        _app.Runner.Start("Dọn dẹp", job => Session(job).ExecuteCleanup(plan, which), rep =>
        {
            if (rep.Failed.Count > 0)
                Dlg.Warn(this, "Dọn dẹp", string.Create(CultureInfo.InvariantCulture, $"Không xoá được {rep.Failed.Count} file (xem nhật ký)."));
            RefreshCountsLater();
        });
    }

    public void Close()
    {
        _session?.Dispose();
        _session = null;
    }
}

/// <summary><c>os.path.normcase(os.path.abspath(p))</c> cho giao diện (bản trong lõi là <c>internal</c>).</summary>
internal static class Py2
{
    public static string NormCase(string p) => Path.GetFullPath(p).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
