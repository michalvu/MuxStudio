using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MuxStudio.Core;
using MuxStudio.Core.Ui;
using Vals = System.Collections.Generic.OrderedDictionary<string, string>;

namespace MuxStudio.App.Views;

/// <summary>
/// Thẻ "① Tải phụ đề" (port gui/fetch_view.py — thay SubFetch). Luồng: <b>Quét thư mục</b> (<see cref="FetchSession.Scan"/>) →
/// <b>Tìm phụ đề</b> (<see cref="FetchUi.GatherAll"/>: gom ứng viên + tự chọn; dòng còn lại hiện "CẦN CHỌN") → bảng ứng viên: nút "+ Chọn" /
/// bấm đúp để chọn, "Bỏ qua ngôn ngữ này", ô "Dùng lại lựa chọn này cho các tập sau cùng season" → <b>Tải phụ đề</b>
/// (<see cref="FetchSession.Execute"/>) → tổng kết. Bước ① KHÔNG sửa thời gian phụ đề.
/// Trình bày theo bản thiết kế: ngôn ngữ / nguồn dạng nhãn, thẻ "TARGET VIDEO", nhãn màu trong bảng, thanh "GIỎ PHỤ ĐỀ".
/// </summary>
public partial class FetchView : UserControl, IStepView
{
    private readonly MainWindow _app;
    private readonly Dictionary<string, CheckBox> _langBoxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _srcBoxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _srcStatus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FetchItem> _iidItem = new(StringComparer.Ordinal);
    private readonly Table _tree;
    private readonly Table _cands;
    private readonly StepBar _steps;
    private FetchSession? _session;
    private List<FetchItem> _items = [];
    private FetchItem? _cur;
    private VideoInfo? _curVideo;
    private bool _checkedSources;
    private bool _executed;

    public FetchView(MainWindow app)
    {
        _app = app;
        InitializeComponent();
        var pill = (Style)FindResource("Pill");
        foreach (var c in Naming.FetchLangSwitches)
        {
            var name = Naming.LangName(c);
            var cb = new CheckBox { Style = pill, Content = new TextBlock { Text = c.ToUpperInvariant() + (name.Length > 0 ? "  " + name : "") } };
            _langBoxes[c] = cb;
            LangPanel.Children.Insert(LangPanel.Children.IndexOf(ExtraBox), cb);
        }
        foreach (var (k, label) in FetchUi.Sources)
        {
            var cb = new CheckBox { Style = (Style)FindResource("PillCyan"), IsChecked = true, Content = new TextBlock { Text = label } };
            var st = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 8), FontSize = 12, MaxWidth = 420,
                                     TextTrimming = TextTrimming.CharacterEllipsis };
            st.SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
            cb.Margin = new Thickness(0, 0, 6, 8);
            _srcBoxes[k] = cb;
            _srcStatus[k] = st;
            SrcPanel.Children.Add(cb);
            SrcPanel.Children.Add(st);
        }
        _steps = new StepBar(StepsPanel,
        [
            ("scan", "Quét thư mục", Scan), ("gather", "Tìm phụ đề", Gather), ("run", "Tải phụ đề", Execute),
            ("next", "Sang ② Chuẩn bị phụ đề →", () => _app.Goto(MainWindow.TabPrep)),
        ], ["next"]);
        _tree = new Table(TreeGrid,
        [
            new Col("kind", "Loại", 140), new Col("fps", "FPS", 65, "e"), new Col("ref", "Phụ đề gốc (mốc)", 250, Stretch: true, Name: true),
            new Col("status", "Trạng thái", 280, Stretch: true, Badge: true), new Col("chosen", "Bản đã chọn", 260, Stretch: true, Name: true),
        ], tree: "Phim / ngôn ngữ", treeWidth: 340, single: true)
        {
            OnSelect = OnTreeSelect, Tip = TreeTip,
        };
        _cands = new Table(CandGrid,
        [
            new Col("n", "#", 36, "e"), new Col("src", "Nguồn", 96, Badge: true), new Col("fps", "FPS", 140, Badge: true),
            new Col("score", "Giống tên", 96, "e", Badge: true), new Col("off", "Lệch", 90, "e", Badge: true),
            new Col("rel", "Bản release", 420, Stretch: true, Name: true), new Col("info", "Thông tin", 220),
        ], single: true)
        {
            OnDouble = (iid, _) => Choose(iid), Tip = CandTip,
        };
        _cands.AddActionColumn("Thao tác", 116, Choose);
        CandTitle.Text = L.T("Chọn một dòng ngôn ngữ ở trên để xem ứng viên.");
        ShowVideo(null);
        OnConfigChanged(false);
    }

    public FrameworkElement Root => this;

    // ------------------------------------------------------------------ cau hinh
    private List<string> Langs() =>
        SettingsUi.LangsFromParts(_langBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true, StringComparer.Ordinal), LangExtra.Text);

    private Dictionary<string, bool> SourcesOn() => _srcBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true, StringComparer.Ordinal);

    public void OnConfigChanged(bool secretsChanged)
    {
        var cfg = _app.Cfg;
        var (sw, extra) = SettingsUi.LangsToParts(cfg.List("Languages"));
        foreach (var (c, cb) in _langBoxes)
            cb.IsChecked = sw.GetValueOrDefault(c);
        LangExtra.Text = extra;
        _srcBoxes["opensubtitles"].IsChecked = cfg.Bool("UseOpenSubtitles");
        _srcBoxes["titulky"].IsChecked = cfg.Bool("UseTitulky");
        Remember.IsChecked = cfg.Bool("RememberChoice");
        if (_session is not null)
            Reset("Cài đặt đã đổi → bấm \"Quét thư mục\" lại để áp dụng.");
        if (secretsChanged)
            _checkedSources = false;
    }

    public void OnFolderChanged() => Reset();

    public void OnFilesChanged()
    {
        if (_session is not null)
            Reset("File trong thư mục đã thay đổi (hoàn tác) → bấm \"Quét thư mục\" lại.");
    }

    public void OnShow()
    {
        if (!_checkedSources)
            CheckSources();
    }

    private void Reset(string note = "")
    {
        _session?.Dispose();
        _session = null;
        _items = [];
        _executed = false;
        _iidItem.Clear();
        _cur = null;
        _tree.Clear();
        _cands.Clear();
        Summary.Text = "";
        CandTitle.Text = L.T("Chọn một dòng ngôn ngữ ở trên để xem ứng viên.");
        ShowVideo(null);
        if (note.Length > 0)
            _app.Log("warn", note);
        UpdateButtons();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Nut "↺ Lam lai tu dau": bang, tong ket va tien do ve nhu chua lam gi. Khong xoa file.
        if (_app.Runner.Busy)
            return;
        var pending = _items.Count(it => it.Result is null && (it.Chosen is not null || it.Existing is not null));
        if (pending > 0 && !Dlg.AskYesNo(this, "Làm lại từ đầu", string.Create(CultureInfo.InvariantCulture,
                $"Còn {pending} dòng đã chọn nhưng CHƯA tải. Xoá hết bảng và tiến độ của lượt này để bắt đầu lại?\n\n(Không xoá file nào.)")))
            return;
        Reset();
        _app.Log("info", "① Đã làm lại từ đầu: các bảng và thanh các bước về mặc định (không xoá file nào).");
    }

    private bool Gathered => _session is not null && _items.Count > 0
                             && _items.All(it => it.Gathered || it.Already is not null || it.Existing is not null || it.Skip);

    public void UpdateButtons()
    {
        var busy = _app.Runner.Busy;
        var has = _session is not null && _items.Count > 0;
        var need = _items.Count(it => it.NeedsChoice);
        var gathered = Gathered;
        _steps.Update([has, gathered, has && _executed, false], [_app.Folder is not null, has, has, true], busy);
        SkipAllBtn.IsEnabled = need > 0 && !busy;
        CheckBtn.IsEnabled = !busy;
        ResetBtn.IsEnabled = _app.Folder is not null && !busy;
        var curOk = _cur is not null && _cur.Candidates.Count > 0 && _cur.Result is null;
        ChooseBtn.IsEnabled = curOk && !busy;
        SkipBtn.IsEnabled = curOk && !busy;
        if (has)
        {
            Hint.Text = L.T(string.Create(CultureInfo.InvariantCulture, $"{_items.Count} dòng (phim × ngôn ngữ)")
                            + (need > 0 ? string.Create(CultureInfo.InvariantCulture, $" — {need} dòng CẦN CHỌN") : ""));
            Hint.SetResourceReference(TextBlock.ForegroundProperty, need > 0 ? "LogWarnBrush" : "LogDimBrush");
        }
        else
        {
            Hint.Text = "";
        }
        // gio phu de: moi ngon ngu da chon / con can chon + nut buoc ke tiep
        if (has)
            Chips.Fill(Basket, WorkbenchUi.LangBasket(_items, _session!.Languages), mono: true);
        else
            Chips.Fill(Basket, [("Bấm \"Quét thư mục\" để bắt đầu.", "dim")]);
        BasketGoText.Text = L.T(_executed ? "Sang ② Chuẩn bị phụ đề →" : gathered ? "Tải phụ đề →" : has ? "Tìm phụ đề →" : "Quét thư mục →");
        BasketGo.IsEnabled = !busy && (has || _app.Folder is not null);
    }

    private void BasketGo_Click(object sender, RoutedEventArgs e)
    {
        var has = _session is not null && _items.Count > 0;
        if (_executed)
            _app.Goto(MainWindow.TabPrep);
        else if (Gathered)
            Execute();
        else if (has)
            Gather();
        else
            Scan();
    }

    // ------------------------------------------------------------------ nguon
    private void CheckSources_Click(object sender, RoutedEventArgs e) => CheckSources();

    private void CheckSources()
    {
        _checkedSources = true;
        var cfg = _app.Cfg;
        var vals = _app.Secrets();
        foreach (var st in _srcStatus.Values)
        {
            st.Text = L.T("đang kiểm tra…");
            st.SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
        }
        _app.Runner.Start("Kiểm tra nguồn phụ đề", job =>
        {
            var outp = new Dictionary<string, ConnStatus>(StringComparer.Ordinal)
            {
                ["subsource"] = vals.ContainsKey("SubSourceApiKey") ? SettingsUi.CheckConnection("subsource", vals, cfg, job) : new ConnStatus(null, "chưa điền key"),
                ["opensubtitles"] = vals.ContainsKey("OsApiKey") ? SettingsUi.CheckConnection("opensubtitles", vals, cfg, job) : new ConnStatus(null, "chưa điền key"),
                // Titulky khong bat buoc tai khoan: chua dien thi khong goi mang, chi ghi "kieu khach"
                ["titulky"] = vals.ContainsKey("TitulkyUsername") ? SettingsUi.CheckConnection("titulky", vals, cfg, job)
                                                                   : new ConnStatus(null, "kiểu khách (chưa điền tài khoản)"),
            };
            return outp;
        }, res =>
        {
            foreach (var (k, stt) in res)
            {
                var txt = L.T(stt.Text); // dich ca cau (khop mau "SubSource: …") roi moi bo tien to ten nguon
                foreach (var pre in new[] { "SubSource:", "OpenSubtitles:", "Titulky:" })
                {
                    if (txt.StartsWith(pre, StringComparison.Ordinal) && txt.IndexOf(": ", StringComparison.Ordinal) is var i and >= 0)
                    {
                        txt = txt[(i + 2)..];
                        break;
                    }
                }
                _srcStatus[k].Text = txt;
                _srcStatus[k].ToolTip = txt;
                _srcStatus[k].SetResourceReference(TextBlock.ForegroundProperty, SettingsView.StatusBrush(stt));
            }
        }, whenFree: true);
    }

    // ------------------------------------------------------------------ 1. quet
    private void Scan()
    {
        var folder = _app.NeedFolder();
        if (folder is null)
            return;
        var langs = Langs();
        if (langs.Count == 0)
        {
            Dlg.Warn(this, "Ngôn ngữ", "Tick ít nhất một ngôn ngữ cần tải.");
            return;
        }
        Reset();
        var cfg = _app.Cfg;
        var secrets = _app.Secrets();
        var srcs = SourcesOn();
        _app.Runner.Start("Quét thư mục (bước ①)", job =>
        {
            var s = new FetchSession(folder, cfg, secrets, job, languages: langs, sources: srcs)
            {
                Captcha = _app.AskCaptcha, // Titulky doi CAPTCHA luc tai -> hop thoai de ban go ma
            };
            try
            {
                s.Scan();
            }
            catch
            {
                s.Dispose();
                throw;
            }
            return s;
        }, s =>
        {
            _session = s;
            _items = [.. s.Items];
            Fill();
            ShowVideo(_items.Count > 0 ? _items[0].Video : null);
            UpdateButtons();
        });
    }

    // ------------------------------------------------------------------ 2. tim
    private void Gather()
    {
        var s = _session;
        if (s is null)
            return;
        var items = _items;
        _app.Runner.Start("Tìm phụ đề", job =>
        {
            s.SetJob(job);
            FetchUi.GatherAll(s, items, job);
        }, () => AfterChange(selectNext: true));
    }

    // ------------------------------------------------------------------ phim dang chon (the "TARGET VIDEO")
    private void ShowVideo(VideoInfo? v)
    {
        _curVideo = v;
        VidTiles.Children.Clear();
        if (v is null)
        {
            VidName.Text = L.T("Chưa quét thư mục phim.");
            VidName.ToolTip = null;
            VidTiles.Children.Add(Chips.Tile("FPS", "—"));
            VidTiles.Children.Add(Chips.Tile("Loại", "—"));
            VidTiles.Children.Add(Chips.Tile("Phụ đề gốc (mốc)", "—"));
            VidLangs.Children.Clear();
            return;
        }
        VidName.Text = Path.GetFileName(v.Path);
        VidName.ToolTip = v.Path;
        VidTiles.Children.Add(Chips.Tile("FPS", v.Fps is { } f && f != 0 ? f.ToString("F3", CultureInfo.InvariantCulture) : "?", "good"));
        VidTiles.Children.Add(Chips.Tile("Loại", L.T(v.KindText)));
        VidTiles.Children.Add(Chips.Tile("Phụ đề gốc (mốc)", v.Ref is not null ? L.T(v.Ref.Source) : L.T("không có"), v.Ref is not null ? "info" : "bad"));
        Chips.Fill(VidLangs, WorkbenchUi.VideoLangs(_items.Where(it => ReferenceEquals(it.Video, v))), mono: true);
    }

    // ------------------------------------------------------------------ chon
    private void OnTreeSelect(List<string> iids)
    {
        FetchItem? it = iids.Count > 0 ? _iidItem.GetValueOrDefault(iids[0]) : null;
        if (it is null && iids.Count > 0 && iids[0].StartsWith('v'))
        {
            var kids = _tree.Children(iids[0]).Select(r => _iidItem[r.Id]).ToList();
            it = kids.FirstOrDefault(x => x.NeedsChoice);
            if (kids.Count > 0)
                ShowVideo(kids[0].Video);
        }
        else if (it is not null)
        {
            ShowVideo(it.Video);
        }
        ShowCandidates(it);
    }

    private static Vals CandRow(Candidate c, int i, FetchItem it)
    {
        var row = FetchUi.CandidateRow(c, i);
        foreach (var (k, v) in WorkbenchUi.CandidateKinds(c))
            row[k] = v;
        var chosen = it.Chosen is not null && c.Ident == it.Chosen.Ident;
        row["act"] = it.Result is not null ? "" : chosen ? "✓ Đã chọn" : "+ Chọn";
        row["~act"] = chosen ? "good" : "";
        return row;
    }

    private void ShowCandidates(FetchItem? it)
    {
        _cur = it;
        _cands.Clear();
        if (it is null)
        {
            CandTitle.Text = L.T("Chọn một dòng ngôn ngữ ở trên để xem ứng viên.");
            UpdateButtons();
            return;
        }
        var head = $"Ứng viên cho: {UiText.ShortenName(Path.GetFileName(it.Video.Path), 64)}  [{it.Lang}]";
        if (it.Video.Fps is { } fps && fps != 0)
            head += $"  — video {fps.ToString("F3", CultureInfo.InvariantCulture)} fps";
        CandTitle.Text = L.T(head);
        for (var i = 0; i < it.Candidates.Count; i++)
        {
            var c = it.Candidates[i];
            var tag = it.Chosen is not null && c.Ident == it.Chosen.Ident ? "good" : FetchUi.CandidateTag(c);
            _cands.Add(string.Create(CultureInfo.InvariantCulture, $"c{i}"), CandRow(c, i, it), tag);
        }
        if (it.Chosen is not null)
        {
            var idx = it.Candidates.FindIndex(c => c.Ident == it.Chosen.Ident);
            if (idx >= 0)
                _cands.Select(string.Create(CultureInfo.InvariantCulture, $"c{idx}"));
        }
        UpdateButtons();
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var sel = _cands.Selected();
        if (sel.Count == 0)
        {
            Dlg.Info(this, "Chọn ứng viên", "Bấm chọn một dòng trong bảng ứng viên (hoặc bấm đúp vào dòng đó).");
            return;
        }
        Choose(sel[0]);
    }

    private void Choose(string candIid)
    {
        var s = _session;
        var it = _cur;
        if (s is null || it is null || _app.Runner.Busy)
            return;
        if (!int.TryParse(candIid.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ci) || ci < 0 || ci >= it.Candidates.Count)
            return;
        var cand = it.Candidates[ci];
        var remember = Remember.IsChecked == true;
        s.Choose(it, cand, remember);
        _app.Log("ok", $"[{it.Lang}] {Path.GetFileName(it.Video.Path)}: chọn [{cand.Src}] {cand.Release}");
        var items = _items;
        _app.Runner.Start("Áp dụng lựa chọn", job =>
        {
            s.SetJob(job);
            if (!remember)
            {
                FetchUi.EnsurePack(s, it);
                return 0;
            }
            return FetchUi.AfterChoice(s, it, items, job);
        }, n =>
        {
            if (n > 0)
                _app.Log("info", string.Create(CultureInfo.InvariantCulture, $"[i] Dùng lại lựa chọn cho {n} tập khác cùng season."));
            AfterChange(selectNext: true);
        });
    }

    private void SkipCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _cur is null)
            return;
        _session.Skip(_cur);
        AfterChange(selectNext: true);
    }

    private void SkipAll_Click(object sender, RoutedEventArgs e)
    {
        var s = _session;
        if (s is null)
            return;
        var need = _items.Where(it => it.NeedsChoice).ToList();
        if (need.Count == 0 || !Dlg.AskYesNo(this, "Bỏ qua", string.Create(CultureInfo.InvariantCulture, $"Bỏ qua {need.Count} dòng còn cần chọn?")))
            return;
        foreach (var it in need)
            s.Skip(it);
        AfterChange();
    }

    // ------------------------------------------------------------------ 3. tai
    private void Execute()
    {
        var s = _session;
        if (s is null)
            return;
        var need = _items.Count(it => it.NeedsChoice);
        if (need > 0 && !Dlg.AskYesNo(this, "Còn dòng cần chọn",
                string.Create(CultureInfo.InvariantCulture, $"Còn {need} dòng CẦN CHỌN (vàng). Bỏ qua các dòng đó và tải phần còn lại?")))
            return;
        var notGathered = _items.Count(it => !(it.Gathered || it.Already is not null || it.Existing is not null || it.Skip));
        if (notGathered > 0 && !Dlg.AskYesNo(this, "Chưa tìm phụ đề", string.Create(CultureInfo.InvariantCulture,
                $"{notGathered} dòng chưa được tìm (chưa bấm \"Tìm phụ đề\"). Vẫn chạy? (các dòng đó sẽ được tính là thiếu)")))
            return;
        var items = _items;
        _app.Runner.Start("Tải phụ đề", job =>
        {
            s.SetJob(job);
            return s.Execute(items);
        }, rep =>
        {
            _executed = true;
            Summary.Text = L.T(FetchUi.SummaryText(rep));
            AfterChange();
        });
    }

    // ------------------------------------------------------------------ bang
    private static Vals Row(string kind, string fps, string refv, string status, string chosen, string tag) => new(StringComparer.Ordinal)
    {
        ["kind"] = kind, ["fps"] = fps, ["ref"] = refv, ["status"] = status, ["chosen"] = chosen,
        ["~status"] = tag switch { "head" => "", "ok" => "info", _ => tag },
    };

    private void Fill()
    {
        _tree.Clear();
        _iidItem.Clear();
        var order = new List<VideoInfo>();
        var byVideo = new Dictionary<VideoInfo, List<FetchItem>>(ReferenceEqualityComparer.Instance);
        foreach (var it in _items)
        {
            if (!byVideo.TryGetValue(it.Video, out var l))
            {
                byVideo[it.Video] = l = [];
                order.Add(it.Video);
            }
            l.Add(it);
        }
        var n = 0;
        for (var vi = 0; vi < order.Count; vi++)
        {
            var v = order[vi];
            var its = byVideo[v];
            var refv = v.Ref is not null ? v.Ref.Source : L.T("không có (không đo được thời gian)");
            var vid = string.Create(CultureInfo.InvariantCulture, $"v{vi}");
            var fps = v.Fps is { } f && f != 0 ? f.ToString("F3", CultureInfo.InvariantCulture) : "?";
            var vtag = its.Any(x => x.NeedsChoice) ? "need" : "head";
            _tree.Add(vid, Row(v.KindText, fps, refv, FetchUi.VideoSummary(its), "", vtag), vtag, text: Path.GetFileName(v.Path));
            foreach (var it in its)
            {
                var iid = string.Create(CultureInfo.InvariantCulture, $"i{n++}");
                _iidItem[iid] = it;
                var (txt, tag) = FetchUi.ItemStatus(it);
                _tree.Add(iid, Row("", "", "", txt, FetchUi.ChosenText(it), tag), tag, text: FetchUi.LangLabel(it.Lang), parent: vid);
            }
        }
    }

    private void RefreshRows()
    {
        foreach (var (iid, it) in _iidItem)
        {
            var (txt, tag) = FetchUi.ItemStatus(it);
            _tree.Update(iid, Row("", "", "", txt, FetchUi.ChosenText(it), tag), tag);
        }
        foreach (var vr in _tree.Rows.Where(r => r.Parent is null).ToList())
        {
            var its = _tree.Children(vr.Id).Select(r => _iidItem[r.Id]).ToList();
            var vtag = its.Any(x => x.NeedsChoice) ? "need" : "head";
            _tree.Update(vr.Id, new Vals(StringComparer.Ordinal) { ["status"] = FetchUi.VideoSummary(its), ["~status"] = vtag == "head" ? "" : vtag }, vtag);
        }
    }

    private void AfterChange(bool selectNext = false)
    {
        RefreshRows();
        var cur = _cur;
        string? nxt = null;
        if (selectNext)
            nxt = _iidItem.FirstOrDefault(kv => kv.Value.NeedsChoice).Key;
        if (nxt is not null)
        {
            _tree.Select(nxt);
            ShowCandidates(_iidItem[nxt]);
            ShowVideo(_iidItem[nxt].Video);
        }
        else
        {
            ShowCandidates(cur);
            ShowVideo(_curVideo);
        }
        UpdateButtons();
    }

    private string? TreeTip(string iid, string col)
    {
        if (!_iidItem.TryGetValue(iid, out var it))
        {
            var refv = iid.StartsWith('v') ? _tree.Full(iid, "ref") : "";
            return col == "ref" && refv.Length > 0 ? L.T("Phụ đề gốc (mốc thời gian): ") + refv : null;
        }
        if (col == "status" && it.TimingNote.Length > 0)
            return it.TimingNote;
        if (col == "status" && it.Existing is not null)
            return $"File có sẵn: {it.Existing.File}\n({it.Existing.Reason}) — sẽ được CHÉP (không di chuyển).";
        if (col == "chosen" && it.Chosen is not null)
            return $"[{it.Chosen.Src}] {it.Chosen.Release}\n{it.Chosen.Info}";
        return null;
    }

    private string? CandTip(string iid, string col)
    {
        var it = _cur;
        if (it is null || !int.TryParse(iid.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) || i < 0 || i >= it.Candidates.Count)
            return null;
        var c = it.Candidates[i];
        if (col == "off")
        {
            if (c.Off is null)
                return "Chưa đo được độ lệch (không có phụ đề gốc, hoặc preview không đủ mốc).";
            return c.Off.Total > 0 ? "Ước tính từ preview của SubSource (~)" : $"Đo thật với phụ đề gốc: {c.Off.Votes.ToString(CultureInfo.InvariantCulture)} mốc khớp";
        }
        return col == "rel" ? c.Release : null;
    }

    public void Close()
    {
        _session?.Dispose();
        _session = null;
    }
}
