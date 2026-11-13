using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MuxStudio.Core;
using MuxStudio.Core.Ui;
using Vals = System.Collections.Generic.OrderedDictionary<string, string>;

namespace MuxStudio.App.Views;

/// <summary>Một thẻ luồng trên giao diện (bọc <see cref="StreamCard"/> của lõi): ô "Xoá" của audio đổi được — gạch ngang + màu đỏ ngay khi tick
/// (CHƯA áp dụng: bấm "Áp dụng lựa chọn audio" như bản Python).</summary>
public sealed class StreamCardVm(StreamCard card) : INotifyPropertyChanged
{
    private bool _drop = card.State == "drop";

    public event PropertyChangedEventHandler? PropertyChanged;

    public StreamCard Card { get; } = card;

    public string Badge => Card.Badge == "EXT CẠNH PHIM" ? L.T("NGOÀI") : Card.Badge;

    public string BadgeKind => Card.Kind switch { "video" => "accent", "audio" => "good", "int" => "info", "ext" => "need", _ => "info" };

    public string Title => Card.Title;

    public string Detail => L.T(Card.Detail);

    public string DefaultText => Card.IsDefault ? "★ DEFAULT" : "";

    public bool CanToggle => Card.CanToggle;

    public Visibility ToggleVisibility => CanToggle ? Visibility.Visible : Visibility.Collapsed;

    public string DropLabel => L.T("XOÁ"); // khong dung "Xoá": tieng Anh cua chu do la "Clear" (nut xoa khung log)

    /// <summary>Tick = XOÁ track này (= ô "Xoá" của bảng audio bản Python).</summary>
    public bool Drop
    {
        get => _drop;
        set
        {
            if (_drop == value || !CanToggle)
                return;
            _drop = value;
            foreach (var n in new[] { nameof(Drop), nameof(Kind), nameof(StateKind), nameof(StateText) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }

    /// <summary>Màu nền thẻ: giữ = xanh, xoá = đỏ, video = tím.</summary>
    public string Kind => Card.Kind == "video" ? "accent" : Drop ? "bad" : "good";

    public string StateKind => Drop ? "bad" : Card.Kind == "video" ? "accent" : "good";

    public string StateText => CanToggle && Drop != (Card.State == "drop") ? L.T(Drop ? "XOÁ (chưa áp dụng)" : "GIỮ (chưa áp dụng)")
                               : L.T(Card.StateText);
}

/// <summary>
/// Thẻ "③ Mux" (port gui/mux_view.py — thay MuxTool):
/// <list type="number">
/// <item>Database đặt tên: TMDB / TVDB / Không đổi tên — BẮT BUỘC chọn mỗi lần, KHÔNG chọn sẵn. Cạnh mỗi lựa chọn: sẵn sàng / chưa điền key / …</item>
/// <item>Bảng tổng quan: tên gốc → tên mới (bấm đúp để sửa / chọn kết quả cho dòng "Cần chọn"), audio / phụ đề giữ, audio ghép, thẻ codec.</item>
/// <item>Luồng của file đang chọn (thẻ VIDEO / AUDIO / SUB): tick audio muốn XOÁ, "Áp dụng cho mọi file còn lại", "Giữ cả hai, Default = bitrate
/// cao nhất" (Q3).</item>
/// <item>Bảng chọn phụ đề theo tập; audio ghép (bấm đúp để sửa độ lệch); khung "Output &amp; mkvmerge" (lệnh THẬT của file đang chọn); "Bắt đầu mux";
/// dọn file nguồn.</item>
/// </list>
/// </summary>
public partial class MuxView : UserControl, IStepView
{
    private readonly MainWindow _app;
    private readonly Dictionary<string, RadioButton> _dbRadio = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _dbStatus = new(StringComparer.Ordinal);
    private readonly StepBar _steps;
    private readonly Table _table;
    private readonly Table _dtable;
    private readonly CheckGrid _grid = new();
    private Dictionary<string, int> _gridRows = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _cmds = new(StringComparer.Ordinal);
    private List<StreamCardVm> _cards = [];
    private bool _gridDirty;
    private MuxSession? _session;
    private List<MuxPlan> _plans = [];
    private int? _cur;
    private bool _dbChecked;
    private bool _muxed;
    private bool _trashDone;
    private bool _loading;
    private List<UsedFile> _trashCands = [];

    public MuxView(MainWindow app)
    {
        _app = app;
        InitializeComponent();
        TabAudio.Header = L.T("Audio của file đang chọn");
        TabGrid.Header = L.T("Bảng chọn phụ đề theo tập");
        TabDonor.Header = L.T("Audio ghép từ file khác");
        TabInfo.Header = L.T("Chi tiết");
        DonorHint.Text = L.T("Bấm đúp vào ô \"Độ lệch\" để sửa tay (chỉ cho lượt chạy này; muốn lưu lâu dài thì điền 'Độ lệch chỉnh tay' trong Cài đặt).");
        var pill = (Style)FindResource("PillEmerald");
        foreach (var (k, label) in MuxUi.Dbs)
        {
            var rb = new RadioButton { Style = pill, Content = new TextBlock { Text = L.T(label) }, GroupName = "muxdb", Margin = new Thickness(0, 0, 6, 8), Tag = k };
            rb.Checked += (_, _) =>
            {
                if (!_loading)
                    DbChanged();
            };
            var st = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 8), FontSize = 12 };
            st.SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
            _dbRadio[k] = rb;
            _dbStatus[k] = st;
            DbPanel.Children.Add(rb);
            DbPanel.Children.Add(st);
        }
        _dbStatus["none"].Text = L.T("giữ tên file gốc");
        _steps = new StepBar(StepsPanel,
        [
            ("plan", "Quét & lập kế hoạch", PlanAll), ("run", "Bắt đầu mux", StartMux), ("trash", "Dọn file nguồn đã dùng", OfferTrash),
            ("out", "Mở thư mục Output", OpenOutput),
        ], ["out"]);
        _table = new Table(PlanGrid,
        [
            new Col("src", "Tên gốc", 250, Stretch: true, Name: true), new Col("new", "→ Tên mới (trong Output)", 330, Stretch: true, Name: true),
            new Col("audio", "Audio giữ", 80), new Col("subs", "Phụ đề giữ", 125), new Col("donor", "Audio ghép", 140, Badge: true),
            new Col("tag", "Thẻ codec", 150, Badge: true), new Col("status", "Trạng thái", 240, Stretch: true, Badge: true),
        ], check: "Mux")
        {
            OnSelect = ids =>
            {
                _cur = ids.Count > 0 ? Idx(ids[0]) : null;
                ShowPlan();
                UpdateButtons();
            },
            OnDouble = OnDouble,
            OnToggle = (iid, st) => _plans[Idx(iid)].Selected = st,
            OnToggleAll = st =>
            {
                foreach (var p in _plans)
                    p.Selected = st;
            },
            Tip = Tip,
        };
        _dtable = new Table(DonorGrid,
        [
            new Col("src", "Phim", 280, Stretch: true, Name: true), new Col("donor", "File nguồn", 260, Stretch: true, Name: true), new Col("langs", "Ngôn ngữ", 80),
            new Col("off", "Độ lệch (ms)", 110, "e"), new Col("how", "Cách đo", 260), new Col("fps", "Khác FPS", 170), new Col("note", "Ghi chú", 260),
        ], single: true)
        {
            OnDouble = (iid, _) => DonorDouble(iid),
        };
        GridHost.Child = _grid.View;
        _grid.OnChange = GridChanged;
        GridNote.Text = L.T("Lập kế hoạch trước.");
        ShowPlan();
        ShowRun(0, 0, "");
        OnConfigChanged(false);
    }

    public FrameworkElement Root => this;

    private static int Idx(string iid) => int.Parse(iid.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string Id(char c, int i) => c + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>Database đã chọn ("" = chưa chọn).</summary>
    public string DbChoice => _dbRadio.FirstOrDefault(kv => kv.Value.IsChecked == true).Key ?? "";

    private void SetDb(string db)
    {
        _loading = true;
        foreach (var (k, rb) in _dbRadio)
            rb.IsChecked = k == db;
        _loading = false;
    }

    /// <summary>Lệnh (ffmpeg + mkvmerge) của mọi kế hoạch — dựng ở LUỒNG PHỤ cùng lúc lập kế hoạch (như "Xem lệnh": không ghi gì).</summary>
    private static Dictionary<string, List<string>> Cmds(MuxSession s, IEnumerable<MuxPlan> plans)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in plans)
        {
            try
            {
                d[MuxUi.KeyOf(p)] = s.PreviewCommands(p);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                d[MuxUi.KeyOf(p)] = [];
            }
        }
        return d;
    }

    // ------------------------------------------------------------------ trang thai
    public void OnConfigChanged(bool secretsChanged)
    {
        var mode = _app.Cfg.Str("RememberAudioChoice");
        ApplyRest.IsChecked = mode is "ask" or "always";
        ApplyRest.Visibility = mode == "never" ? Visibility.Collapsed : Visibility.Visible;
        ApplyRest.IsEnabled = mode != "always";
        if (secretsChanged)
            _dbChecked = false;
        if (_session is not null)
            Reset("Cài đặt đã đổi → bấm \"Quét & lập kế hoạch\" lại để áp dụng.");
    }

    public void OnFolderChanged() => Reset();

    public void OnFilesChanged()
    {
        if (_session is not null)
            Reset("File trong thư mục đã thay đổi (hoàn tác) → lập kế hoạch lại.");
    }

    public void OnShow()
    {
        if (!_dbChecked)
            CheckDb();
    }

    private void Reset(string note = "")
    {
        _session?.Dispose();
        _session = null;
        _plans = [];
        _cmds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        _cur = null;
        _muxed = _trashDone = false;
        _trashCands = [];
        Fill();
        Summary.Text = "";
        ShowRun(0, 0, "");
        if (note.Length > 0)
            _app.Log("warn", note);
        UpdateButtons();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Nut "↺ Lam lai tu dau": moi bang, tong ket, tien do VA lua chon database ve nhu chua lam gi. Khong xoa file.
        if (_app.Runner.Busy)
            return;
        var pending = _plans.Count(p => p.Selected && p.Result is null && p.Status is "ok" or "need_choice");
        if (pending > 0 && !Dlg.AskYesNo(this, "Làm lại từ đầu", string.Create(CultureInfo.InvariantCulture,
                $"Còn {pending} file đã tick CHƯA mux. Xoá hết bảng và tiến độ của lượt này để bắt đầu lại?\n\n(Không xoá file nào.)")))
            return;
        SetDb(""); // database BAT BUOC chon lai moi luot
        Reset();
        Inner.SelectedIndex = 0;
        _app.Log("info", "③ Đã làm lại từ đầu: các bảng và thanh các bước về mặc định (không xoá file nào).");
        _app.UpdateState();
    }

    public void UpdateButtons()
    {
        var busy = _app.Runner.Busy;
        var db = DbChoice;
        var has = _plans.Count > 0;
        _steps.Update([has, has && _muxed, _trashDone || (_muxed && _trashCands.Count == 0), false],
                      [db.Length > 0 && _app.Folder is not null, db.Length > 0 && has, _trashCands.Count > 0, true], busy);
        CmdBtn.IsEnabled = has && !busy;
        ResetBtn.IsEnabled = _app.Folder is not null && !busy;
        KeepAllBtn.IsEnabled = _plans.Any(MuxUi.MultiEn) && !busy;
        DbCheckBtn.IsEnabled = !busy;
        var p = Plan();
        ApplyAudioBtn.IsEnabled = p is not null && p.Audio.Count >= 2 && !busy;
        KeepBothBtn.IsEnabled = p is not null && MuxUi.MultiEn(p) && !busy;
        GridApplyBtn.IsEnabled = _gridRows.Count > 0 && !busy;
        RunText.Text = L.T(has ? "Bắt đầu mux" : "Quét & lập kế hoạch");
        RunBtn.IsEnabled = !busy && db.Length > 0 && _app.Folder is not null;
        if (db.Length == 0)
        {
            Chips.Fill(Hint, [("← Chọn TMDB / TVDB / Không đổi tên trước.", "need")]);
        }
        else if (has)
        {
            int N(string k) => _plans.Count(x => x.Status == k);
            Chips.Fill(Hint,
            [
                (string.Create(CultureInfo.InvariantCulture, $"{N("ok")} sẵn sàng"), "good"),
                (string.Create(CultureInfo.InvariantCulture, $"{N("need_choice")} cần chọn"), N("need_choice") > 0 ? "need" : "dim"),
                (string.Create(CultureInfo.InvariantCulture, $"{N("skip") + N("exists")} bỏ qua"), "dim"),
                (string.Create(CultureInfo.InvariantCulture, $"{N("fail")} lỗi"), N("fail") > 0 ? "bad" : "dim"),
            ]);
        }
        else
        {
            Hint.Children.Clear();
        }
    }

    private MuxPlan? Plan() => _cur is { } c && c >= 0 && c < _plans.Count ? _plans[c] : null;

    // ------------------------------------------------------------------ database
    private void DbCheck_Click(object sender, RoutedEventArgs e) => CheckDb();

    private void CheckDb()
    {
        _dbChecked = true;
        var vals = _app.Secrets();
        var cfg = _app.Cfg;
        foreach (var k in new[] { "tmdb", "tvdb" })
        {
            _dbStatus[k].Text = L.T("đang kiểm tra…");
            _dbStatus[k].SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
        }
        _app.Runner.Start("Kiểm tra TMDB / TVDB", job =>
        {
            var outp = new Dictionary<string, ConnStatus>(StringComparer.Ordinal);
            foreach (var (k, sk) in new[] { ("tmdb", "TmdbApiKey"), ("tvdb", "TvdbApiKey") })
                outp[k] = vals.ContainsKey(sk) ? SettingsUi.CheckConnection(k, vals, cfg, job) : new ConnStatus(null, "chưa điền key");
            return outp;
        }, res =>
        {
            foreach (var (k, stt) in res)
            {
                var txt = L.T(stt.Text); // dich ca cau (khop mau "TMDB: …") roi moi bo tien to ten database
                if ((txt.StartsWith("TMDB:", StringComparison.Ordinal) || txt.StartsWith("TVDB:", StringComparison.Ordinal)) && txt.IndexOf(": ", StringComparison.Ordinal) is var i and >= 0)
                    txt = txt[(i + 2)..];
                _dbStatus[k].Text = txt;
                _dbStatus[k].SetResourceReference(TextBlock.ForegroundProperty, SettingsView.StatusBrush(stt));
            }
        }, whenFree: true);
    }

    private void DbChanged()
    {
        if (_plans.Count > 0 && _session is not null)
        {
            var db = DbChoice;
            var files = _plans.Select(p => p.Src).ToList();
            var s = _session;
            var sel = _plans.ToDictionary(MuxUi.KeyOf, p => p.Selected, StringComparer.Ordinal);
            _app.Runner.Start($"Lập lại kế hoạch ({(db != "none" ? db.ToUpperInvariant() : "giữ tên gốc")})", job =>
            {
                s.SetJob(job);
                var ps = s.PlanAll(db, files);
                return (ps, Cmds(s, ps));
            }, r => SetPlans(r.ps, sel, r.Item2));
        }
        UpdateButtons();
        _app.UpdateState();
    }

    // ------------------------------------------------------------------ lap ke hoach
    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_plans.Count > 0)
            StartMux();
        else
            PlanAll();
    }

    private void PlanAll()
    {
        var folder = _app.NeedFolder();
        var db = DbChoice;
        if (folder is null || db.Length == 0)
            return;
        var cfg = _app.Cfg;
        var secrets = _app.Secrets();
        _app.Runner.Start("Lập kế hoạch mux", job =>
        {
            var s = _session;
            if (s is null || Py2.NormCase(s.Td) != Py2.NormCase(folder))
            {
                s?.Dispose();
                s = new MuxSession(folder, cfg, secrets, job);
                _session = s;
            }
            else
            {
                s.SetJob(job);
            }
            var ps = s.PlanAll(db);
            return (ps, Cmds(s, ps));
        }, r =>
        {
            _muxed = _trashDone = false; // ke hoach moi = luot moi
            _trashCands = [];
            SetPlans(r.ps, new Dictionary<string, bool>(StringComparer.Ordinal), r.Item2);
            if (_cur is null && _plans.Count > 0)
                _table.Select(Id('p', 0));
        });
    }

    private void SetPlans(List<MuxPlan> plans, IReadOnlyDictionary<string, bool> sel, Dictionary<string, List<string>>? cmds = null)
    {
        var curKey = Plan() is { } cp ? MuxUi.KeyOf(cp) : null;
        foreach (var p in plans)
        {
            var k = MuxUi.KeyOf(p);
            if (sel.TryGetValue(k, out var v))
                p.Selected = v;
            else if (p.Status is not ("ok" or "need_choice"))
                p.Selected = false;
        }
        _plans = [.. plans];
        if (cmds is not null)
            _cmds = cmds;
        var idx = _plans.FindIndex(p => MuxUi.KeyOf(p) == curKey);
        _cur = idx >= 0 ? idx : null;
        Fill();
        UpdateButtons();
    }

    private void Replan(string title, Action<MuxSession>? mutate = null, IReadOnlyList<MuxPlan>? only = null)
    {
        var s = _session;
        if (s is null)
            return;
        var plans = _plans.ToList();
        var sel = plans.ToDictionary(MuxUi.KeyOf, p => p.Selected, StringComparer.Ordinal);
        var targets = only?.Select(MuxUi.KeyOf).ToHashSet(StringComparer.Ordinal);
        _app.Runner.Start(title, job =>
        {
            s.SetJob(job);
            mutate?.Invoke(s);
            var ps = plans.Select(p => targets is null || targets.Contains(MuxUi.KeyOf(p)) ? s.PlanFile(p.Src) : p).ToList();
            return (ps, Cmds(s, ps));
        }, r => SetPlans(r.ps, sel, r.Item2));
    }

    // ------------------------------------------------------------------ bang tong quan
    private string OutDir => _session?.OutDir ?? Path.Combine(_app.Folder ?? "", "Output");

    private void Fill()
    {
        _table.Clear();
        for (var i = 0; i < _plans.Count; i++)
        {
            var (vals, tag) = MuxUi.OverviewValues(_plans[i], OutDir);
            foreach (var (k, v) in WorkbenchUi.OverviewKinds(_plans[i], tag))
                vals[k] = v;
            _table.Add(Id('p', i), vals, tag, _plans[i].Selected);
        }
        if (_cur is { } c)
            _table.Select(Id('p', c));
        ShowPlan();
        FillGrid();
        FillDonor();
    }

    private void OnDouble(string iid, string col)
    {
        _cur = Idx(iid);
        var p = Plan();
        if (p is null)
            return;
        if (col is "new" or "src" || (col == "status" && p.Name.Status == "need_choice"))
            EditName(p);
        else if (col is "audio" or "status")
            Inner.SelectedIndex = 0;
        else if (col == "subs")
            Inner.SelectedIndex = 1;
        else if (col == "donor")
            Inner.SelectedIndex = 2;
    }

    private string? Tip(string iid, string col)
    {
        var i = Idx(iid);
        if (i < 0 || i >= _plans.Count)
            return null;
        var p = _plans[i];
        if (col == "status" && p.Reasons.Count > 0)
            return string.Join("\n", p.Reasons.Select(L.T));
        if (col == "new")
            return L.T(p.Name.Note) + "\n" + L.T("Bấm đúp để sửa tên" + (p.Name.Status == "need_choice" ? " / chọn kết quả" : ""));
        if (col == "donor" && p.Donor.Timing is not null)
            return p.Donor.Timing.How;
        return null;
    }

    /// <summary>File đang chọn: thẻ luồng, chi tiết, khung Output &amp; lệnh mkvmerge.</summary>
    private void ShowPlan()
    {
        var p = Plan();
        MkvVer.Text = _session is { Ver.Ok: true } s ? s.Ver.Text : "";
        if (p is null)
        {
            AudioTitle.Text = L.T("Chọn một file ở bảng trên.");
            AudioTitle.SetResourceReference(TextBlock.ForegroundProperty, "LogInfoBrush");
            _cards = [];
            StreamList.ItemsSource = null;
            Info.Text = "";
            OutPath.Text = "";
            Cli.Text = L.T(_plans.Count == 0 ? "Lập kế hoạch trước." : "Chọn một file ở bảng trên.");
            return;
        }
        Info.Text = string.Join("\n", MuxUi.PlanDetails(p, _session?.OutDir ?? "").Select(L.T));
        var n = _app.Density switch { "compact" => 40, "large" => 90, _ => 60 };
        var head = string.Create(CultureInfo.InvariantCulture, $"{UiText.ShortenName(Path.GetFileName(p.Src), n)} — {p.Audio.Count} audio");
        if (MuxUi.MultiEn(p))
            head += "  ⚠ 2+ audio tiếng Anh: tick track muốn xoá";
        else if (p.NeedsAudioTable)
            head += " — tick track muốn XOÁ rồi bấm \"Áp dụng\"";
        AudioTitle.Text = L.T(head);
        AudioTitle.SetResourceReference(TextBlock.ForegroundProperty, MuxUi.MultiEn(p) ? "LogWarnBrush" : "LogInfoBrush");
        _cards = [.. WorkbenchUi.Streams(p).Select(c => new StreamCardVm(c))];
        StreamList.ItemsSource = _cards;
        OutPath.Text = p.OutPath is not null ? "Output\\" + MuxUi.OutRel(p, OutDir) : "—";
        var cmds = _cmds.GetValueOrDefault(MuxUi.KeyOf(p)) ?? [];
        Cli.Text = cmds.Count > 0 ? string.Join("\n\n", cmds) : L.T("File này không có lệnh (bị bỏ qua / lỗi — xem cột Trạng thái).");
    }

    /// <summary>Tiến độ của lượt "Mux" trong khung Output (cửa sổ chính gọi khi tiến độ đổi).</summary>
    public void ShowRun(int done, int total, string label)
    {
        RunBar.Value = total > 0 ? Math.Max(0, Math.Min(1, (double)done / total)) : 0;
        RunPct.Text = total > 0 ? UiText.ProgressText(done, total) : "";
        RunState.Text = total > 0 ? UiText.EllipsizeMiddle(label, 60) : L.T(_muxed ? MuxUi.SummaryText(_lastRep ?? new MuxReport()) : "Sẵn sàng.");
    }

    private MuxReport? _lastRep;

    // ------------------------------------------------------------------ audio
    private void ApplyAudio_Click(object sender, RoutedEventArgs e)
    {
        var p = Plan();
        if (p is null)
            return;
        var drop = _cards.Where(c => c.Card.Kind == "audio" && c.CanToggle && c.Drop).Select(c => c.Card.Id).ToList();
        var keepable = p.Audio.Count(a => !MuxUi.FixedAudio.Contains(a.Reason));
        if (keepable > 0 && drop.Count >= keepable)
        {
            Dlg.Warn(this, "Audio", "Không thể xoá HẾT audio của file — bỏ tick ít nhất một track.");
            return;
        }
        var rest = ApplyRest.IsChecked == true && _app.Cfg.Str("RememberAudioChoice") != "never";
        Replan("Áp dụng lựa chọn audio", s => s.SetAudioChoice(p, drop, rest), rest ? null : [p]);
    }

    private void KeepBoth_Click(object sender, RoutedEventArgs e)
    {
        if (Plan() is { } p)
            Replan("Giữ cả hai audio", s => s.KeepAllAudio(p), [p]);
    }

    private void KeepAllEn_Click(object sender, RoutedEventArgs e)
    {
        if (Dlg.AskYesNo(this, "Giữ mọi audio tiếng Anh", "Mọi file có ≥ 2 audio tiếng Anh: giữ cả hai, Default = track bitrate cao nhất?"))
            Replan("Giữ mọi audio tiếng Anh", s => s.KeepAllAudio(null));
    }

    // ------------------------------------------------------------------ phu de theo tap
    private void FillGrid()
    {
        var cfg = _app.Cfg;
        _gridRows = new Dictionary<string, int>(StringComparer.Ordinal);
        _gridDirty = false;
        if (_plans.Count == 0)
        {
            _grid.Clear();
            GridNote.Text = L.T("Lập kế hoạch trước.");
            return;
        }
        if (!cfg.Bool("UseSubPicker"))
        {
            _grid.Clear();
            GridNote.Text = L.T("Bảng chọn phụ đề đang tắt (Cài đặt › ③ Mux › 'Bảng chọn phụ đề theo tập').");
            return;
        }
        var data = MuxUi.GridData(_plans, _session?.SubChoices);
        if (data.Rows.Count < cfg.Int("SubPickerMinFiles"))
        {
            _grid.Clear();
            GridNote.Text = L.T(string.Create(CultureInfo.InvariantCulture, $"Chỉ hiện khi có ≥ {cfg.Int("SubPickerMinFiles")} file có phụ đề."));
            return;
        }
        _gridRows = data.Rows.ToDictionary(r => r, Idx, StringComparer.Ordinal);
        _grid.SetModel(new GridModel(data.Rows, data.Cols, data.Auto, data.Current), data.Headers, data.Labels, data.Tips);
        ShowGridNote();
    }

    private void ShowGridNote()
    {
        var n = _grid.Model?.ChangedRows().Count ?? 0;
        var txt = string.Create(CultureInfo.InvariantCulture, $"{_gridRows.Count} file. Tick sẵn theo quy tắc tự động; ô vàng = khác quy tắc.");
        if (n > 0)
            txt += string.Create(CultureInfo.InvariantCulture, $"  {n} file đã đổi") + (_gridDirty ? " — bấm \"Áp dụng bảng chọn\"" : "");
        GridNote.Text = L.T(txt);
        GridNote.SetResourceReference(TextBlock.ForegroundProperty, _gridDirty ? "LogWarnBrush" : "LogInfoBrush");
    }

    private void GridChanged()
    {
        _gridDirty = true;
        ShowGridNote();
    }

    private void GridAll_Click(object sender, RoutedEventArgs e) => GridSetAll(true);

    private void GridNone_Click(object sender, RoutedEventArgs e) => GridSetAll(false);

    private void GridSetAll(bool state)
    {
        if (_grid.Model is null)
            return;
        _grid.Model.SetAll(state);
        _grid.Redraw();
        GridChanged();
    }

    private void GridReset_Click(object sender, RoutedEventArgs e)
    {
        if (_grid.Model is null)
            return;
        _grid.Model.Reset();
        _grid.Redraw();
        GridChanged();
    }

    private void GridApply_Click(object sender, RoutedEventArgs e)
    {
        var m = _grid.Model;
        if (m is null || _session is null)
            return;
        var plans = _plans;
        var rows = new Dictionary<string, int>(_gridRows, StringComparer.Ordinal);
        var choices = rows.Where(kv => m.RowDiffers(kv.Key))
                          .ToDictionary(kv => kv.Key, kv => MuxUi.GridChoice(plans[kv.Value], m.RowValues(kv.Key)), StringComparer.Ordinal);
        Replan("Áp dụng bảng chọn phụ đề", s =>
        {
            s.ResetSubChoices();
            foreach (var (r, (ext, ints)) in choices)
                s.SetSubChoice(plans[rows[r]], ext, ints);
        });
    }

    // ------------------------------------------------------------------ audio ghep
    private void FillDonor()
    {
        _dtable.Clear();
        for (var i = 0; i < _plans.Count; i++)
        {
            var p = _plans[i];
            if (p.Donor.Tracks.Count > 0 || p.Donor.Donor is not null || (p.Donor.Note.Length > 0 && _app.Cfg.Bool("UseDonorAudio")))
            {
                var tag = p.Donor.Tracks.Count > 0 ? "" : "dim";
                if (p.Donor.Timing is { Sure: false } && p.Donor.Tracks.Count > 0)
                    tag = "need";
                _dtable.Add(Id('d', i), MuxUi.DonorValues(p), tag);
            }
        }
    }

    private void DonorDouble(string iid)
    {
        var p = _plans[Idx(iid)];
        if (p.Donor.Tracks.Count == 0 && p.Donor.Donor is null)
            return;
        var cur = p.Donor.Tracks.Count > 0 ? p.Donor.Tracks[0].OffsetMs.ToString(CultureInfo.InvariantCulture) : "";
        var txt = Dlg.AskString(this, "Độ lệch audio ghép",
                                $"{p.Rel}\n\nĐộ lệch (ms) cho audio ghép — để trống = dùng kết quả đo / giá trị trong Cài đặt:",
                                p.Donor.Manual ? cur : "");
        if (txt is null)
            return;
        double? ms;
        try
        {
            ms = MuxUi.ParseMs(txt);
        }
        catch (FormatException ex)
        {
            Dlg.Warn(this, "Độ lệch", ex.Message);
            return;
        }
        Replan("Sửa độ lệch audio ghép", s => s.SetDonorOffset(p, ms), [p]);
    }

    // ------------------------------------------------------------------ ten
    private void EditName(MuxPlan p)
    {
        var db = DbChoice;
        var act = NameDialog.Run(this, p, db);
        if (act is null)
            return;
        switch (act.Kind)
        {
            case "choose":
                Replan("Chọn tên", s => s.ChooseName(p, act.Id));
                break;
            case "keep":
                Replan("Giữ tên gốc", s => s.KeepOriginalName(p), [p]);
                break;
            case "set":
                Replan("Sửa tên", s => s.SetName(p, act.Text), [p]);
                break;
            case "search":
                var s0 = _session;
                if (s0 is null)
                    return;
                var sel = _plans.ToDictionary(MuxUi.KeyOf, x => x.Selected, StringComparer.Ordinal);
                var plans = _plans.ToList();
                _app.Runner.Start($"Tìm lại '{act.Text}'", job =>
                {
                    s0.SetJob(job);
                    if (s0.SearchName(p, act.Text, act.Year) is null)
                        job.Warn("[!] Database đã chọn không dùng được (chưa có key / key bị từ chối).");
                    var ps = plans.Select(x => ReferenceEquals(x, p) ? s0.PlanFile(x.Src) : x).ToList();
                    return (ps, Cmds(s0, ps));
                }, r =>
                {
                    SetPlans(r.ps, sel, r.Item2);
                    var np = _plans.FirstOrDefault(x => MuxUi.KeyOf(x) == MuxUi.KeyOf(p));
                    if (np is not null)
                        Dispatcher.BeginInvoke(() => EditName(np), System.Windows.Threading.DispatcherPriority.Background);
                });
                break;
        }
    }

    // ------------------------------------------------------------------ chay
    private void Cmd_Click(object sender, RoutedEventArgs e)
    {
        var s = _session;
        if (s is null)
            return;
        var p = Plan();
        var ps = p is not null ? [p] : _plans.Where(x => x.Status is "ok" or "need_choice").Take(20).ToList();
        _app.Runner.Start("Dựng lệnh", job =>
        {
            s.SetJob(job);
            var lines = new List<string>();
            foreach (var x in ps)
            {
                var cmds = s.PreviewCommands(x);
                if (cmds.Count > 0)
                {
                    lines.Add($"### {x.Rel}");
                    lines.AddRange(cmds);
                    lines.Add("");
                }
            }
            return lines;
        }, lines =>
        {
            if (lines.Count == 0)
            {
                Dlg.Info(this, "Xem lệnh", "File này không có lệnh (bị bỏ qua / lỗi — xem cột Trạng thái).");
                return;
            }
            Dlg.ShowText(this, "Xem lệnh (không ghi file nào)", string.Join("\n", lines),
                         "Đúng các lệnh ffmpeg (đổi tốc độ audio ghép) + mkvmerge sẽ chạy. Kết quả ghi ra tên tạm '<tên>.muxstudio-tmp.mkv' rồi mới đổi thành tên thật.");
        });
    }

    private void StartMux()
    {
        var s = _session;
        if (s is null || DbChoice.Length == 0)
            return;
        var sel = _plans.Where(p => p.Selected && p.Result is null).ToList();
        if (sel.Count == 0)
        {
            Dlg.Info(this, "Mux", "Không có file nào được tick (file đã mux xong được bỏ tick tự động).");
            return;
        }
        var needName = sel.Count(p => p.Name.Status == "need_choice");
        var keepNames = false;
        if (needName > 0)
        {
            var ans = Dlg.AskYesNoCancel(this, "Còn dòng cần chọn tên", string.Create(CultureInfo.InvariantCulture,
                $"Còn {needName} file CẦN CHỌN tên (vàng).\n\nCó = giữ tên file gốc cho các file đó rồi mux luôn.\nKhông = quay lại để chọn."));
            if (ans != true)
                return;
            keepNames = true;
        }
        var needAudio = sel.Count(p => p.Status == "need_choice" && p.Name.Status != "need_choice");
        var nOk = sel.Count(p => p.Status is "ok" or "need_choice") - needAudio;
        var msg = string.Create(CultureInfo.InvariantCulture, $"Mux {nOk} file vào '{s.OutDir}'?");
        if (needAudio > 0)
            msg += string.Create(CultureInfo.InvariantCulture, $"\n\n{needAudio} file còn CẦN CHỌN audio sẽ KHÔNG được mux (xem cột Trạng thái).");
        if (!Dlg.AskYesNo(this, "Bắt đầu mux", msg))
            return;
        var plans = _plans.ToList();
        var selmap = plans.ToDictionary(MuxUi.KeyOf, p => p.Selected && p.Result is null, StringComparer.Ordinal);
        _app.Runner.Start("Mux", job =>
        {
            s.SetJob(job);
            var ps = keepNames ? s.KeepOriginalNames(plans) : plans;
            foreach (var p in ps)
                p.Selected = selmap.GetValueOrDefault(MuxUi.KeyOf(p));
            var rep = s.Execute(ps);
            return (ps, rep, Cmds(s, ps));
        }, res =>
        {
            var (ps, rep, cmds) = res;
            foreach (var p in ps)
            {
                if (p.Result is { Ok: true })
                    p.Selected = false;
            }
            _muxed = true;
            _trashDone = false;
            _lastRep = rep;
            var seen = _trashCands.Select(u => u.Path.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            _trashCands = [.. _trashCands.Where(u => File.Exists(u.Path)), .. Core.Mux.UsedSources(ps, s.OutDir).Where(u => !seen.Contains(u.Path.ToLowerInvariant()))];
            SetPlans(ps, ps.ToDictionary(MuxUi.KeyOf, p => p.Selected, StringComparer.Ordinal), cmds);
            Summary.Text = L.T(MuxUi.SummaryText(rep));
            RunBar.Value = 1;
            RunState.Text = L.T(MuxUi.SummaryText(rep));
            if (_trashCands.Count > 0 && _app.Cfg.Bool("AskTrashSourcesAfterMux"))
                Dispatcher.BeginInvoke(OfferTrash, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    /// <summary>Mux xong: hỏi đưa file nguồn đã dùng (MKV gốc, phụ đề ngoài, nguồn audio ghép) vào Thùng rác — tick từng file.</summary>
    private void OfferTrash()
    {
        var cands = _trashCands.Where(u => File.Exists(u.Path)).ToList();
        var s = _session;
        if (cands.Count == 0 || s is null)
        {
            _trashCands = [];
            _trashDone = true;
            UpdateButtons();
            Dlg.Info(this, "Dọn file nguồn",
                     "Không còn file nguồn nào để dọn (chỉ đề xuất file của các file đã mux THÀNH CÔNG, không đụng file vẫn cần cho file chưa mux).");
            return;
        }
        var chosen = TrashDialog.Run(this, cands, _app.Cfg.Bool("DeleteToRecycleBin"), _app.Folder);
        _trashDone = true;
        UpdateButtons();
        if (chosen is null || chosen.Count == 0)
            return;
        _app.Runner.Start("Dọn file nguồn đã mux", job =>
        {
            s.SetJob(job);
            return s.TrashSources(chosen);
        }, rep =>
        {
            _trashCands = [.. _trashCands.Where(u => File.Exists(u.Path))];
            if (rep.Failed.Count > 0)
                Dlg.Warn(this, "Dọn file nguồn", string.Create(CultureInfo.InvariantCulture, $"Không xoá được {rep.Failed.Count} file (xem nhật ký)."));
            UpdateButtons();
        });
    }

    private void OpenOutput()
    {
        var d = _app.Folder is not null ? Path.Combine(_app.Folder, "Output") : null;
        if (!Shell.Open(d))
            Dlg.Info(this, "Output", "Chưa có thư mục Output (chưa mux file nào).");
    }

    public void Close()
    {
        _session?.Dispose();
        _session = null;
    }
}

/// <summary>Kết quả của <see cref="NameDialog"/>: <c>choose</c> (Id) | <c>keep</c> | <c>set</c> (Text) | <c>search</c> (Text, Year).</summary>
public sealed record NameAction(string Kind, long Id = 0, string Text = "", int? Year = null);

/// <summary>Dòng "Cần chọn" / sửa tên (port <c>NameDialog</c>): 5 kết quả gần nhất, "Tìm lại với tên…", "Giữ tên gốc", "Sửa tay…".</summary>
public static class NameDialog
{
    public static NameAction? Run(DependencyObject owner, MuxPlan plan, string db)
    {
        var nm = plan.Name;
        var d = new Dlg(owner, "Tên mới", 900, 540);
        d.Text($"File: {plan.Rel}\n{L.T(nm.Note)}");
        var dbn = nm.Db.Length > 0 ? nm.Db : db;
        var stem = Path.GetFileNameWithoutExtension(plan.Src);
        // hang "Tim lai voi ten" o duoi bang
        var sf = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(sf, Dock.Bottom);
        d.Body.Children.Add(sf);
        var g = d.Fill(new DataGrid());
        g.SetResourceReference(FrameworkElement.StyleProperty, "WbGrid");
        var t = new Table(g, [new Col("title", "Tên", 360, Stretch: true, Name: true), new Col("year", "Năm", 70), new Col("id", "ID", 140), new Col("orig", "Tên gốc", 240, Name: true)],
                          single: true);
        for (var i = 0; i < nm.Candidates.Count; i++)
        {
            var c = nm.Candidates[i];
            t.Add("c" + i.ToString(CultureInfo.InvariantCulture), new Vals(StringComparer.Ordinal)
            {
                ["title"] = c.Title, ["year"] = c.Year?.ToString(CultureInfo.InvariantCulture) ?? "????", ["id"] = $"{dbn}id-{c.Id.ToString(CultureInfo.InvariantCulture)}",
                ["orig"] = c.Original,
            });
        }
        if (nm.Candidates.Count == 0)
            t.Add("none", new Vals(StringComparer.Ordinal) { ["title"] = L.T("(không có kết quả để chọn)"), ["year"] = "", ["id"] = "", ["orig"] = "" }, "dim");
        void Choose(string? iid)
        {
            if (iid is null)
            {
                var sel = t.Selected();
                iid = sel.Count > 0 ? sel[0] : null;
            }
            if (iid is null || !iid.StartsWith('c'))
            {
                Dlg.Info(d.Win, "Chọn", "Bấm chọn một kết quả trong bảng.");
                return;
            }
            d.Close(new NameAction("choose", nm.Candidates[int.Parse(iid.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture)].Id));
        }
        t.OnDouble = (iid, _) => Choose(iid);

        EpisodeInfo? ep = null;
        try
        {
            ep = Episodes.ParseEpisode(stem);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
        {
            ep = null;
        }
        var rn = Naming.SplitReleaseName(stem);
        var guess = ep is { Series.Length: > 0 } ? ep.Series : rn.Title.Length > 0 ? rn.Title : stem;
        var gyear = (ep is not null ? ep.Year : rn.Year)?.ToString(CultureInfo.InvariantCulture) ?? "";
        sf.Children.Add(new TextBlock { Text = L.T("Tìm lại với tên:"), VerticalAlignment = VerticalAlignment.Center });
        var q = new Wpf.Ui.Controls.TextBox { Text = guess, Width = 360, Margin = new Thickness(6, 0, 6, 0) };
        sf.Children.Add(q);
        sf.Children.Add(new TextBlock { Text = L.T("Năm:"), VerticalAlignment = VerticalAlignment.Center });
        var y = new Wpf.Ui.Controls.TextBox { Text = gyear, Width = 80, Margin = new Thickness(6, 0, 6, 0) };
        sf.Children.Add(y);
        var canSearch = db is "tmdb" or "tvdb";
        var sb = new Wpf.Ui.Controls.Button { Content = L.T("Tìm lại"), IsEnabled = canSearch };
        sb.Click += (_, _) =>
        {
            var title = q.Text.Trim();
            if (title.Length == 0)
                return;
            int? year;
            try
            {
                year = MuxUi.ParseYear(y.Text);
            }
            catch (FormatException e)
            {
                Dlg.Warn(d.Win, "Năm", e.Message);
                return;
            }
            d.Close(new NameAction("search", Text: title, Year: year));
        };
        sf.Children.Add(sb);
        if (!canSearch)
        {
            var nt = new TextBlock { Text = L.T("(đang chọn 'Không đổi tên')"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            nt.SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
            sf.Children.Add(nt);
        }
        d.Button("Huỷ", () => d.Close(null));
        d.Button("Sửa tay…", () =>
        {
            var txt = Dlg.AskString(d.Win, "Sửa tay",
                "Tên file mới (không gồm .mkv, không gồm thẻ codec — thẻ codec tự gắn thêm; ký tự Windows cấm sẽ bị bỏ):", nm.Base);
            if (!string.IsNullOrWhiteSpace(txt))
                d.Close(new NameAction("set", Text: txt.Trim()));
        });
        d.Button("Giữ tên gốc", () => d.Close(new NameAction("keep")));
        if (nm.Candidates.Count > 0)
            d.Button("Chọn kết quả này", () => Choose(null), true);
        return d.Run() as NameAction;
    }
}

/// <summary>Danh sách file nguồn đã dùng cho file mux thành công, tick sẵn — bấm nút mới đưa vào Thùng rác (port <c>TrashDialog</c>).</summary>
public static class TrashDialog
{
    public static List<UsedFile>? Run(DependencyObject owner, IReadOnlyList<UsedFile> cands, bool recycleBin, string? root)
    {
        var d = new Dlg(owner, "Dọn file nguồn đã mux xong", 1080, 560);
        d.Text(string.Create(CultureInfo.InvariantCulture, $"{cands.Count} file nguồn đã dùng cho các file mux THÀNH CÔNG (kết quả nằm trong ")
               + "Output). Tick file muốn xoá. File vẫn cần cho file chưa mux không có trong danh sách.");
        d.Text(recycleBin ? "→ vào Thùng rác (khôi phục được: chuột phải trong Thùng rác › Restore)."
                          : "⚠ Bạn đã TẮT 'Xoá vào Thùng rác' → các file này sẽ bị XOÁ HẲN.", recycleBin ? "ok" : "error");
        var g = d.Fill(new DataGrid());
        g.SetResourceReference(FrameworkElement.StyleProperty, "WbGrid");
        var t = new Table(g,
        [
            new Col("kind", "Loại", 150, Badge: true), new Col("file", "File", 470, Stretch: true, Name: true), new Col("size", "Dung lượng", 90, "e"),
            new Col("used", "Đã dùng cho", 300, Stretch: true, Name: true),
        ], check: "Xoá", single: true);
        for (var i = 0; i < cands.Count; i++)
        {
            var vals = MuxUi.TrashValues(cands[i], root);
            vals["~kind"] = cands[i].Kind switch { "video" => "accent", "sub" => "need", _ => "info" };
            t.Add("t" + i.ToString(CultureInfo.InvariantCulture), vals, check: true);
        }
        d.Button("Không xoá", () => d.Close(null));
        d.Button(recycleBin ? "Đưa các file đã tick vào Thùng rác" : "XOÁ HẲN các file đã tick",
                 () => d.Close(t.CheckedIds().Select(i => cands[int.Parse(i.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture)]).ToList()), true);
        return d.Run() as List<UsedFile>;
    }
}
