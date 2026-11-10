"""Phần logic của giao diện — KHÔNG cần tkinter / màn hình (chạy được cả trên máy không có tkinter).

* ``widgets``: ``ellipsize_middle`` (= ``Limit-Text`` của SubFetch, đáp án lấy từ hàm gốc chạy bằng pwsh),
  ``GridModel`` (bảng chọn phụ đề theo tập), ``Runner`` (luồng phụ, log/tiến độ qua queue, Dừng sau file / Dừng ngay,
  xếp hàng việc tự động, lỗi không làm chết vòng đọc).
* ``settings_view``: chữ ↔ giá trị cho MỌI khoá cấu hình, ``DonorOffsetMs``, ngôn ngữ, "Kiểm tra kết nối" (API giả),
  bảng "Nhập từ .bat" (bí mật luôn che).
* ``fetch_view`` / ``subprep_view`` / ``mux_view`` / ``app``: dòng bảng, trạng thái, màu, bảng chọn phụ đề, CSV, Hoàn tác.
* Lõi: ``set_job`` (một phiên sống qua nhiều lần bấm nút).
"""
from __future__ import annotations

import sys
import threading
import time
from pathlib import Path

import pytest

from core import config as C
from core import naming, subfetch, subprep, tools, undo
from core.subfetch import Candidate, FetchItem, FetchReport, Offset, VideoInfo
from core.subprep import Health, RenameRow, SyncPair, SyncResult
from core.tmdb import ConnStatus
from fakehttp import FakeHTTP, FakeResponse, Sleeper
from gui import app as A
from gui import fetch_view as FV
from gui import mux_view as MV
from gui import settings_view as SV
from gui import subprep_view as PV
from gui import widgets as W

ROOT = Path(__file__).resolve().parent.parent


# ==========================================================================
#  widgets
# ==========================================================================


@pytest.mark.parametrize("n,out", [
    (5, "Show."), (11, "Show.S01E01"), (12, "Show....rtv]"), (13, "Show.S...rtv]"), (20, "Show.S01E...b[rartv]"),
    (33, "Show.S01E01.1080...264-NTb[rartv]"),  # (33-3)*0.55 = 16.5 -> 16 (lam tron ve so chan nhu [int] PowerShell)
    (40, "Show.S01E01.1080p.WE....H.264-NTb[rartv]"), (48, "Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-NTb[rartv]"),
])
def test_ellipsize_middle_matches_limit_text(n, out):
    assert W.ellipsize_middle("Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-NTb[rartv]", n) == out
    assert W.ellipsize_middle("", 10) == "" and W.ellipsize_middle(None, 10) == ""


def test_grid_model_cells_columns_rows_reset():
    auto = {("e1", "E|vi"): True, ("e1", "I|English"): False, ("e2", "I|English"): True}
    m = W.GridModel(["e1", "e2"], ["E|vi", "I|English"], auto)
    assert m.present("e1", "E|vi") and not m.present("e2", "E|vi")  # o xam: tap 2 khong co sub ngoai vi
    assert not m.toggle_cell("e2", "E|vi")  # o xam khong bam duoc
    assert m.toggle_cell("e1", "I|English") and m.differs("e1", "I|English") and m.changed_rows() == ["e1"]
    # cot: con o chua tick -> tick het; da tick het -> bo het
    m.reset()
    assert m.toggle_col("I|English") and m.get("e1", "I|English") and m.get("e2", "I|English")
    assert m.toggle_col("I|English") and not m.get("e1", "I|English") and not m.get("e2", "I|English")
    # dong
    m.reset()
    assert m.toggle_row("e1") and m.row_values("e1") == {"E|vi": True, "I|English": True}
    assert m.toggle_row("e1") and m.row_values("e1") == {"E|vi": False, "I|English": False}
    m.set_all(True)
    assert all(m.cells.values()) and m.changed_rows() == ["e1"]
    m.reset()
    assert m.cells == auto and not m.changed_rows()
    # lua chon dang co (bang chon da ap dung truoc do)
    m2 = W.GridModel(["e1"], ["E|vi"], {("e1", "E|vi"): True}, {("e1", "E|vi"): False, ("e9", "x"): True})
    assert m2.get("e1", "E|vi") is False and not m2.present("e9", "x")


class _Rec:
    def __init__(self):
        self.logs: list[tuple[str, str]] = []
        self.progs: list[tuple[int, int, str]] = []
        self.busy: list[tuple[bool, str]] = []

    def runner(self):
        return W.Runner(None, lambda lv, m: self.logs.append((lv, m)), lambda d, t, lb: self.progs.append((d, t, lb)),
                        lambda b, t: self.busy.append((b, t)), redact=lambda s: s.replace("demo-bi-mat", "•••"))

    def text(self):
        return "\n".join(m for _l, m in self.logs)


def test_runner_done_progress_redact_and_one_task_at_a_time():
    rec = _Rec()
    r = rec.runner()
    gate = threading.Event()
    got: list = []

    def work(job):
        job.info("key = demo-bi-mat")
        job.progress(1, 2, "a")
        job.progress(2, 2, "b")
        gate.wait(5)
        return 42

    assert r.start("Việc A", work, got.append)
    assert r.busy and not r.start("Việc B", work)  # chi mot viec moi luc
    assert "Đang chạy \"Việc A\"" in rec.text()
    gate.set()
    r.wait()
    assert got == [42] and not r.busy
    assert ("info", "key = •••") in rec.logs  # log luon che bi mat
    assert rec.progs[-1] == (2, 2, "b")
    assert rec.busy == [(True, "Việc A"), (False, "Việc A")]


def test_runner_error_and_callback_error_do_not_kill_loop():
    rec = _Rec()
    r = rec.runner()
    errs: list = []
    r.start("Hỏng", lambda job: 1 / 0, None, errs.append)
    r.wait()
    assert isinstance(errs[0], ZeroDivisionError) and "[LỖI] Hỏng: division by zero" in rec.text()
    r.start("Hiển thị hỏng", lambda job: 1, lambda _x: [][1])
    r.wait()
    assert "[LỖI giao diện]" in rec.text()
    ok: list = []
    assert r.start("Sau đó", lambda job: "vẫn chạy", ok.append)
    r.wait()
    assert ok == ["vẫn chạy"]


def test_runner_stop_after_current_and_queue_when_free():
    rec = _Rec()
    r = rec.runner()
    order: list = []
    started = threading.Event()

    def loop(job):
        started.set()
        n = 0
        while not job.should_stop():
            n += 1
            time.sleep(0.005)
        order.append("A dừng")
        return n

    r.start("A", loop, lambda n: order.append("A xong"))
    started.wait(5)
    assert r.start("Đếm file", lambda job: order.append("B chạy"), when_free=True)  # xep hang
    assert r.start("Đếm file", lambda job: order.append("B lần 2"), when_free=True)  # trung ten -> khong them
    r.stop_after_current()
    r.wait()
    assert order == ["A dừng", "A xong", "B chạy"]
    assert "Sẽ dừng sau file đang chạy" in rec.text()


def test_runner_cancel_now_kills_child_process():
    rec = _Rec()
    r = rec.runner()
    errs: list = []
    t0 = time.monotonic()

    def work(job):
        tools.run([sys.executable, "-c", "import time; time.sleep(30)"], job)
        return "không tới đây"

    r.start("Chạy lâu", work, None, errs.append)
    for _ in range(500):
        job = r.job
        if job is not None and job._procs:
            break
        time.sleep(0.01)
    r.start("Đếm file", lambda job: None, when_free=True)
    r.cancel_now()
    r.wait()
    assert time.monotonic() - t0 < 15
    assert errs and isinstance(errs[0], tools.Cancelled)
    assert "Đã dừng ngay: Chạy lâu" in rec.text()
    assert "Đếm file" not in [b[1] for b in rec.busy]  # Dung ngay -> bo ca viec dang xep hang


# ==========================================================================
#  Cài đặt
# ==========================================================================


def test_every_field_text_roundtrip():
    for f in C.FIELDS:
        k = SV.field_kind(f)
        if k in ("bool", "choice"):
            continue
        txt = SV.value_to_text(f, f.default)
        assert SV.text_to_value(f, txt) == f.default, f.key
    f = C.FIELD_BY_KEY["ExtraToolDirs"]
    assert SV.value_to_text(f, ["C:\\A B\\x", "D:\\y"]) == "C:\\A B\\x\nD:\\y"  # duong dan co dau cach: moi dong mot muc
    assert SV.text_to_value(f, "C:\\A B\\x\n\n D:\\y ") == ["C:\\A B\\x", "D:\\y"]
    assert SV.text_to_value(C.FIELD_BY_KEY["AlassExtraArgs"], "--split-penalty  7") == ["--split-penalty", "7"]
    assert SV.text_to_value(C.FIELD_BY_KEY["ExistingMinScore"], "0,4") == 0.4  # dau phay cung nhan
    assert SV.value_to_text(C.FIELD_BY_KEY["DonorMaxDurationDiff"], 1.0) == "1"


def test_field_errors_are_vietnamese_with_label():
    with pytest.raises(ValueError, match="Nghỉ giữa các lượt gọi"):
        SV.text_to_value(C.FIELD_BY_KEY["ApiPauseMs"], "nhanh")
    with pytest.raises(ValueError, match="không được để trống"):
        SV.text_to_value(C.FIELD_BY_KEY["MinVerifyVotes"], "  ")


def test_donor_offset_dict_text():
    f = C.FIELD_BY_KEY["DonorOffsetMs"]
    v = SV.text_to_value(f, "S01E03 = 1200\n'tap #643' = -500\nPhim.2020.mkv: 12,5\n\n")
    assert v == {"S01E03": 1200, "tap #643": -500, "Phim.2020.mkv": 12.5}
    assert SV.text_to_value(f, SV.value_to_text(f, v)) == v
    with pytest.raises(ValueError, match="dòng 2"):
        SV.parse_dict_text("S01E01 = 1\nkhông có số")


def test_languages_switches_and_extra():
    sw, extra = SV.langs_to_parts(["vi", "en", "cs", "pt", "ru"])
    assert sw["vi"] and sw["cs"] and not sw["ja"] and extra == "pt ru"
    assert SV.langs_from_parts({"vi": True, "ja": True, "en": True}, "pt, cz vi") == ["vi", "en", "ja", "pt", "cs"]


def test_check_connection_with_fake_apis():
    cfg = C.Config({})
    assert SV.check_connection("tmdb", {}, cfg).ok is None
    h = FakeHTTP().add("GET", r"/configuration$", FakeResponse(200, {"images": {}}))
    st = SV.check_connection("tmdb", {"TmdbApiKey": "demo-tmdb-key-0123456789abcdef01"}, cfg, http=h, sleep=Sleeper())
    assert st.ok and "sẵn sàng" in st.text
    h = FakeHTTP().add("GET", r"/configuration$", FakeResponse(401, {"status_message": "Invalid API key"}))
    st = SV.check_connection("tmdb", {"TmdbApiKey": "demo-tmdb-key-0123456789abcdef01"}, cfg, http=h, sleep=Sleeper())
    assert st.ok is False and "từ chối" in st.text
    h = FakeHTTP().add("GET", r"subsource\.net/api/v1/movies/search",
                       FakeResponse(200, {"data": []}, headers={"X-RateLimit-Remaining": "58", "X-RateLimit-Limit": "60"}))
    st = SV.check_connection("subsource", {"SubSourceApiKey": "demo-ss"}, cfg, http=h, sleep=Sleeper())
    assert st.ok and st.text == "SubSource: key hợp lệ, còn 58/60 lượt/phút"
    h = FakeHTTP().add("POST", r"thetvdb\.com/v4/login", FakeResponse(401, {}))
    st = SV.check_connection("tvdb", {"TvdbApiKey": "demo-tvdb"}, cfg, http=h, sleep=Sleeper())
    assert st.ok is False and "đăng nhập lỗi" in st.text
    assert SV.status_color(ConnStatus(True, "")) == W.LEVEL_COLORS["ok"]
    with pytest.raises(ValueError):
        SV.check_connection("khac", {}, cfg)


def test_import_rows_mask_secrets_and_filter(tmp_path):
    p = tmp_path / "MuxTool [cu].bat"
    p.write_text("<# : batch\n@echo off\n#>\n$DonorFolder = 'audio_x'\n$TmdbApiKey = 'demo-tmdb-key-0123456789abcdef01'\n"
                 "$MaxShiftSeconds = 99\n", encoding="utf-8", newline="")
    q = tmp_path / "MuxTool2.bat"
    q.write_text("<# : batch\n#>\n$DonorFolder = 'audio_y'\n", encoding="utf-8", newline="")
    imp = C.import_from_bat([p, q])
    rows = SV.import_rows(imp, C.Config({}), lambda k: "demo-cu-0123456789" if k == "TmdbApiKey" else "")
    sec = next(r for r in rows if r.key == "TmdbApiKey")
    assert sec.secret and sec.new == "demo…ef01" and sec.current == "demo…6789"
    assert "demo-tmdb-key" not in " ".join(r.new + r.current for r in rows)
    donor = [r for r in rows if r.key == "DonorFolder"]
    assert len(donor) == 2 and all(r.group == "DonorFolder" for r in donor)  # xung dot -> chon mot
    f = SV.filtered_import(imp, {"DonorFolder"}, set())
    assert "MaxShiftSeconds" not in f.values and not f.secrets and f.conflicts == imp.conflicts


# ==========================================================================
#  ① Tải phụ đề
# ==========================================================================


def _vi(tmp_path, **kw):
    v = VideoInfo(tmp_path / "Show.S01E01.mkv", "Show.S01E01", None, "Show", None, 23.976, ["show"])
    return FetchItem(v, "vi", tmp_path / "srt_can_sync" / "vi", **kw)


def test_fetch_item_status_texts(tmp_path):
    c = Candidate("SubSource", 1, "23.976", 0.8, "Show.S01.1080p.WEB-DL", "2 file, 50 tải (gói season)", {}, Offset(2.5, 4, 4))
    assert FV.item_status(_vi(tmp_path)) == ("chưa tìm", "")
    assert FV.item_status(_vi(tmp_path, candidates=[c, c], gathered=True)) == ("CẦN CHỌN — 2 ứng viên", "need")
    assert FV.item_status(_vi(tmp_path, gathered=True, status="không nguồn nào có")) == ("không nguồn nào có", "bad")
    assert FV.item_status(_vi(tmp_path, candidates=[c], chosen=c, choice_reason="bạn chọn"))[1] == "ok"
    assert FV.item_status(_vi(tmp_path, skip=True, candidates=[c])) == ("bỏ qua", "dim")
    done = _vi(tmp_path, chosen=c, result=tmp_path / "a.vi.srt", result_src="SubSource", timing_note="Kiểm chứng yếu")
    assert FV.item_status(done) == ("đã tải (SubSource): a.vi.srt", "need")
    assert FV.chosen_text(done) == "[SubSource] Show.S01.1080p.WEB-DL"
    row = FV.candidate_row(c, 0)
    assert row == {"n": 1, "src": "SubSource", "fps": "23.976 KHỚP", "score": "80%", "off": "+2.5s~",
                   "rel": "Show.S01.1080p.WEB-DL", "info": "2 file, 50 tải (gói season)"}
    assert FV.candidate_tag(Candidate("SubSource", 2, "?", 0.5, "x", "", None)) == ""
    assert FV.candidate_tag(Candidate("SubSource", 1, "đo: khớp", 0.5, "x", "", None, Offset(0.1, 30, -1))) == "good"
    assert FV.candidate_tag(Candidate("OpenSub", 3, "25", 0.5, "x", "", None)) == "bad"
    assert [k for k, _ in FV.SOURCES] == ["subsource", "opensubtitles"]  # TD-38: het nguon "kho noi bo"
    assert FV.lang_label("vi") == "vi — Vietnamese"
    rep = FetchReport(ok=3, used_existing=1, missing=1, os_remaining=48, os_allowed=100, stopped=True)
    assert FV.summary_text(rep).startswith("Tải được: 3  |  Dùng file có sẵn: 1")
    assert "OpenSubtitles còn 48/100" in FV.summary_text(rep) and "đã dừng" in FV.summary_text(rep)


def test_gather_all_then_choice_reused_for_rest_of_season(movie_dir):
    """Hai bản tiếng Việt → tập 1 "CẦN CHỌN"; bạn chọn gói season (ô "Dùng lại…" bật) → tải trước gói, đo lại tập 2
    rồi tự chọn luôn cho tập 2 (đo THẲNG file của tập 2 trong gói)."""
    import test_subfetch as TS

    lib = TS.Lib(movie_dir)
    http = FakeHTTP()
    TS.ss_routes(http, lib, extra_subs=[{"subtitleId": 5, "language": "vietnamese", "releaseInfo": ["Show.S01.720p.HDTV"],
                                         "framerate": "", "files": 2, "downloads": 3}])
    s, job = TS.session(lib, http, C.Config({"Languages": ["vi"]}), secrets={"SubSourceApiKey": "demo-ss"})
    s.scan()
    FV.gather_all(s, s.items, job)
    e1, e2 = TS.item(s, TS.EP1, "vi"), TS.item(s, TS.EP2, "vi")
    assert e1.needs_choice and e2.needs_choice
    pack = next(c for c in e1.candidates if c.payload.get("subtitleId") == 1)
    s.choose(e1, pack, True)
    assert FV.after_choice(s, e1, s.items, job) == 1
    assert e2.chosen.ident == pack.ident and e2.choice_reason == "dùng lại lựa chọn trước"
    assert e2.chosen.off.total == -1 and abs(e2.chosen.off.offset + 1.25) < 0.1
    assert len(http.calls_to(r"subtitles/1/download")) == 1
    s.close()


# ==========================================================================
#  ② Chuẩn bị phụ đề
# ==========================================================================


def test_prep_lang_options_and_rows(tmp_path):
    opts = PV.lang_options()
    assert opts[0] == PV.AUTO_LANG and opts[-1] == PV.OTHER_LANG and "vi — Vietnamese" in opts
    assert PV.lang_from_option(PV.AUTO_LANG) == "auto" and PV.lang_from_option("cs — Czech") == "cs"
    assert "pt — Portuguese" in PV.lang_options(["pt"])
    src = tmp_path / "srt_can_sync" / "cs" / "x.srt"
    r = RenameRow(src, "vi", "cs", new_path=tmp_path / "Show.S01E01.vi.srt", ep_label="S01E01",
                  warn="file nằm trong thư mục 'cs' nhưng bạn chọn 'vi'")
    vals, tag = PV.rename_values(r)
    assert tag == "bad" and vals["new"] == "Show.S01E01.vi.srt" and vals["why"].startswith("⚠ file nằm trong thư mục 'cs'")
    assert PV.rename_values(RenameRow(src, "vi", "vi", reason="đã đúng tên"))[1] == "dim"
    assert PV.rename_values(RenameRow(src, "vi", "vi", reason="nhập nhằng"))[1] == "need"
    assert PV.rename_values(RenameRow(src, "vi", "vi", status="đã đổi tên"))[1] == "good"


def test_prep_pair_values_health_and_csv(tmp_path):
    p = SyncPair(tmp_path / "a.vi.srt", "vi", "sub", out_path=tmp_path / "Phim.vi.srt")
    vals, tag = PV.pair_values(p)
    assert tag == "" and vals["health"] == "" and not PV.pair_needs_review(p)
    h = Health(1, ["còn lệch 1.3s so với phụ đề gốc"], ["offset"], 812, -2.0, -2.5, 1.3, 31)
    p.result = SyncResult(True, 0, h, cut=12, fixed=3)
    vals, tag = PV.pair_values(p)
    assert tag == "need" and vals["health"] == "CẦN XEM"
    assert (vals["first"], vals["last"], vals["spread"], vals["resid"], vals["votes"], vals["cut"]) == \
        ("-2.00", "-2.50", "-0.50", "+1.30", "31", "12")
    assert vals["flags"].startswith("bỏ 3 dòng dồn về 00:00:00; còn lệch 1.3s")
    assert PV.pair_needs_review(p)
    q = SyncPair(tmp_path / "b.vi.srt", "vi", "audio", reason="đã có kết quả")
    q2 = SyncPair(tmp_path / "c.vi.srt", "vi", "audio", out_path=tmp_path / "c.srt")
    q2.result = SyncResult(False, 1, rejected=True, notes=["alass dịch 300s > 130s"])
    assert PV.pair_values(q)[1] == "dim" and PV.pair_values(q2) [1] == "bad" and PV.pair_values(q2)[0]["health"] == "TỪ CHỐI"
    out = tmp_path / "[x] bao cao.csv"
    assert PV.export_csv([p, q, q2], out) == 3
    data = out.read_bytes()
    assert data.startswith(b"\xef\xbb\xbf") and b"\r\n" in data
    txt = data.decode("utf-8-sig")
    assert txt.splitlines()[0].startswith('"Phụ đề","Mốc","Loại mốc","Kết quả"') and "CẦN XEM" in txt and "TỪ CHỐI" in txt


# ==========================================================================
#  ③ Mux
# ==========================================================================


@pytest.fixture
def mlib(movie_dir, monkeypatch):
    import test_mux as TM

    return TM, TM.Lib(movie_dir, monkeypatch)


def test_mux_overview_audio_rows_and_details(mlib):
    TM, lib = mlib
    j = TM.J(TM.V(), TM.A(1, "eng", ch=6, bps=640000), TM.A(2, "eng", ch=2, bps=192000), TM.A(3, "eng", name="Commentary"),
             TM.A(4, "cze", ch=2), TM.S(5, "eng"), TM.S(6, "eng", sdh=True, name="SDH"), TM.S(7, "cze"))
    p = lib.plan(lib.add(TM.MOVIE, j))
    assert p.status == "need_choice"
    vals, tag = MV.overview_values(p, lib.s.out_dir)
    assert tag == "need" and vals["status"].startswith("Cần chọn: có 2 audio tiếng Anh")
    assert vals["audio"] == "3/4" and vals["new"] == f"{TM.MOVIE} - 1080p HEVC EAC35.1.mkv"
    rows = {iid: (v, chk, t) for iid, v, chk, t in MV.audio_rows(p)}
    assert rows["a3"][1] is None and rows["a3"][0]["note"] == "Commentary"  # binh luan: khong cho tick
    assert rows["a1"][1] is False and rows["a1"][0]["note"] == "giữ — Default" and rows["a1"][2] == "ok"
    assert MV.multi_en(p)
    det = "\n".join(MV.plan_details(p, lib.s.out_dir))
    assert "#3 English E-AC-3 5.1" in det and "XOÁ (Commentary)" in det and "Phụ đề trong MKV:" in det
    # ap dung lua chon -> san sang
    lib.s.set_audio_choice(p, [2], True)
    p2 = lib.plan(p.src)
    assert p2.status == "ok" and MV.overview_values(p2, lib.s.out_dir)[1] == ""


def test_mux_grid_data_and_choice(mlib):
    TM, lib = mlib
    a = lib.add("Show.S01E01.1080p", TM.J(TM.V(), TM.A(1), TM.S(2, "eng"), TM.S(3, "eng", sdh=True, name="SDH")))
    b = lib.add("Show.S01E02.1080p", TM.J(TM.V(), TM.A(1), TM.S(2, "eng")))
    (lib.root / "Show.S01E01.1080p.vi.srt").write_text("1\n00:00:01,000 --> 00:00:02,000\nx\n", encoding="utf-8", newline="")
    pa, pb = lib.plan(a), lib.plan(b)
    d = MV.grid_data([pa, pb])
    assert d["rows"] == ["m0", "m1"] and d["cols"][0] == "E|vi"
    sdh = next(c for c in d["cols"] if "SDH" in c)
    assert d["auto"][("m0", "E|vi")] and ("m1", "E|vi") not in d["auto"] and d["auto"][("m0", sdh)] is False
    assert d["headers"]["E|vi"].startswith("NGOÀI\nVietnamese") and d["labels"]["m1"] == pb.rel
    assert MV.grid_choice(pa, {"E|vi": False, "I|English": True, sdh: True}) == ([], [2, 3])
    lib.s.set_sub_choice(pa, [], [2, 3])
    d2 = MV.grid_data([lib.plan(a), pb], lib.s.sub_choices)
    assert d2["current"] == {("m0", "E|vi"): False, ("m0", "I|English"): True, ("m0", sdh): True}


def test_mux_parse_inputs_and_summary():
    assert MV.parse_ms("") is None and MV.parse_ms(" +1200 ") == 1200.0 and MV.parse_ms("-500ms") == -500.0
    assert MV.parse_ms("1,5") == 1.5
    with pytest.raises(ValueError):
        MV.parse_ms("1 giây")
    assert MV.parse_year("") is None and MV.parse_year("2019") == 2019
    with pytest.raises(ValueError):
        MV.parse_year("19")
    from core.mux import MuxReport

    assert MV.summary_text(MuxReport(ok=3, warning=1, skipped=2, pending=1, failed=0)) == \
        "Thành công: 3 (1 có cảnh báo)  |  Bỏ qua: 2  |  Cần chọn: 1  |  Lỗi: 0"


# ==========================================================================
#  Hoàn tác (màn hình chung)
# ==========================================================================


def test_undo_entries_all_steps_newest_first(movie_dir):
    cfg = C.Config({})
    f = movie_dir / "a.srt"
    f.write_text("x", encoding="utf-8", newline="")
    import datetime as dt

    for step, key, day in (("subfetch", "SubFetchLogDir", 1), ("mux", "MuxLogDir", 3), ("subprep", "SubPrepLogDir", 2)):
        lg = undo.RunLog(movie_dir, cfg[key], step)
        lg.add(undo.CREATE, "", f)
        lg.add(undo.RENAME, movie_dir / "b.srt", f)
        lg.save(dt.datetime(2026, 11, day, 9, 30, 5))
    ents = A.undo_entries(movie_dir, cfg)
    assert [e.step for e in ents] == ["mux", "subprep", "subfetch"]
    assert ents[0].label == "③ Mux" and ents[0].when == "03/11/2026 09:30:05"
    assert A.counts_text(ents[0].run.counts) == "1 tạo, 1 đổi tên" and A.counts_text({}) == "(trống)"
    plan = undo.plan_undo(ents[0].run.path)
    lines = A.plan_lines(plan, movie_dir)
    assert lines[0] == "Vào Thùng rác: a.srt" and lines[1] == "Đổi lại tên cũ: a.srt  →  b.srt"


# ==========================================================================
#  Lõi: set_job — một phiên sống qua nhiều lần bấm nút
# ==========================================================================


def test_set_job_rebinds_every_part(movie_dir, monkeypatch):
    j1, j2 = tools.Job(), tools.Job()
    fs = subfetch.FetchSession(movie_dir, C.Config({}), {"SubSourceApiKey": "demo-ss", "TmdbApiKey": "demo-t",
                                                         "OsApiKey": "demo-os"}, j1, FakeHTTP(), Sleeper(),
                               tool_paths={"mkvmerge": None, "mkvextract": None})
    fs.set_job(j2)
    assert all(x.job is j2 for x in (fs, fs.memory, fs.refs, fs.ss, fs.tmdb, fs.os))
    fs.close()
    ps = subprep.PrepSession(movie_dir, C.Config({}), j1, tool_paths={})
    ps.set_job(j2)
    assert ps.job is j2
    ps.close()
    from core import mux

    monkeypatch.setattr(tools, "mkvmerge_version", lambda p, job=None: tools.ToolVersion(True, 82, "v82"))
    ms = mux.MuxSession(movie_dir, C.Config({}), {"TmdbApiKey": "demo-t", "TvdbApiKey": "demo-v"}, j1, FakeHTTP(),
                        Sleeper(), tool_paths={"mkvmerge": "x"})
    ms.set_job(j2)
    assert all(x.job is j2 for x in (ms, ms.tmdb, ms.tvdb, ms.track_times))
    ms.close()


def test_gui_modules_import_without_display():
    """Mọi module giao diện import được mà không cần màn hình (tkinter chỉ dùng khi dựng cửa sổ)."""
    import importlib

    for m in ("gui.widgets", "gui.log_panel", "gui.settings_view", "gui.fetch_view", "gui.subprep_view", "gui.mux_view",
              "gui.app"):
        importlib.import_module(m)
    from gui import log_panel

    assert log_panel.format_log([("09:00:00", "error", "hỏng"), ("09:00:01", "info", "ok")]) == \
        "09:00:00  [LỖI]  hỏng\n09:00:01  ok\n"
    assert naming.lang_name("vi") == "Vietnamese"


def test_main_startup_error_written_to_file(config_dir, monkeypatch, capsys):
    """``pythonw`` không có console: lỗi khởi động được ghi ra ``loi_khoi_dong.txt`` (và báo bằng hộp thoại nếu có)."""
    def boom(**_k):
        raise RuntimeError("hỏng màn hình")

    monkeypatch.setattr(A, "App", boom)
    shown: list = []
    if W.messagebox is not None:
        monkeypatch.setattr(W.messagebox, "showerror", lambda t, m: shown.append(m))
    if W.ctk is None:
        monkeypatch.setattr(W, "ctk", object())
    assert A.main([]) == 1
    txt = (config_dir / "loi_khoi_dong.txt").read_text(encoding="utf-8")
    assert "RuntimeError: hỏng màn hình" in txt
    assert (shown and "hỏng màn hình" in shown[0]) or "hỏng màn hình" in capsys.readouterr().err


# ==========================================================================
#  Tên file dài + bố cục theo độ phân giải (720p / 1080p / 1440p)
# ==========================================================================

LONG = ("[Judas] Sousou no Frieren - S01E05 - The Journey's End and the Hero's Party Departs Again "
        "[1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs].mkv")


@pytest.mark.parametrize("w", [120, 80, 60, 40, 30, 20, 14, 9])
def test_fit_name_keeps_episode_and_extension(w):
    out = W.fit_text(LONG, w, len, "name")
    assert len(out) <= w and "…" in out
    assert "S01E05" in out  # KHONG BAO GIO mat so tap
    if w >= 30:
        assert out.startswith("[Judas] S") and out.endswith(".mkv")


def test_fit_text_other_cases():
    assert W.fit_text("ngắn", 10) == "ngắn" and W.fit_text(None, 10) == "" and W.fit_text("", 0) == ""
    assert W.fit_text("Cần chọn: có 2 audio tiếng Anh", 16) == "Cần chọn: có 2…"
    assert W.fit_text("abc", 0) == ""  # khong du cho ca dau "…"
    movie = "Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.DV.HDR.H.265-FLUX.mkv"
    out = W.fit_text(movie, 40, len, "name")  # khong co so tap: bo phan giua, giu dau + nhom release
    assert len(out) == 40 and out.startswith("Dune.Part.Two.2024") and out.endswith("-FLUX.mkv")
    anime = W.fit_text("[Judas] One Piece - 1085 [1080p][HEVC x265 10bit][Multi-Subs].mkv", 30, len, "name")
    assert "- 1085" in anime and len(anime) <= 30
    # ten series rat dai truoc so tap: rut gon ten, van giu so tap
    out = W.fit_text("A" * 80 + ".S02E10.1080p.mkv", 24, len, "name")
    assert "S02E10" in out and len(out) <= 24
    # do bang pixel (moi ky tu rong khac nhau) cung dung
    out = W.fit_text(LONG, 300, lambda s: sum(9 if c.isupper() else 7 for c in s), "name")
    assert "S01E05" in out
    assert W.shorten_name(LONG, 50) == W.fit_text(LONG, 50, len, "name") and len(W.shorten_name(LONG, 50)) <= 50


@pytest.mark.parametrize("sw,sh,zoomed,density,fits", [
    (1280, 720, True, "compact", (1264, 648)),   # 720p: phong to het man hinh, bo cuc gon
    (1366, 768, True, "compact", (1350, 696)),   # laptop 768p
    (1280, 720, True, "compact", (1264, 648)),   # 1080p dat phong to 150% (= 1280x720 logic)
    (1536, 864, False, "compact", None),          # 1080p dat phong to 125%
    (1920, 1080, False, "normal", None),          # 1080p 100%
    (2560, 1440, False, "large", None),           # 1440p 100%
    (2048, 1152, False, "normal", None),          # 1440p dat phong to 125%
    (3840, 2160, False, "large", None),           # 4K
])
def test_plan_window_fits_every_screen(sw, sh, zoomed, density, fits):
    p = W.plan_window(sw, sh)
    assert p.zoomed is zoomed and p.density == density
    assert p.width <= sw - 16 and p.height <= sh - 72  # khong bao gio to hon man hinh
    assert p.min_width <= p.width and p.min_height <= p.height
    if fits:
        assert (p.width, p.height) == fits
    if sw >= 1900 and not zoomed:
        assert p.width >= 1600  # man hinh lon: dung phan lon man hinh (truoc day co dinh 1320x900)


def test_plan_window_saved_size_only_if_it_still_fits():
    assert (W.plan_window(1920, 1080, "1500x900").width, W.plan_window(1920, 1080, "1500x900").height) == (1500, 900)
    assert W.plan_window(1920, 1080, "1500x900+12+30").width == 1500
    big = W.plan_window(1920, 1080, "2400x1400")  # luu tu man hinh 1440p, nay mo tren 1080p -> bo qua
    assert big.width <= 1904 and big.height <= 1008
    assert W.plan_window(1920, 1080, "rác").width == W.plan_window(1920, 1080).width
    assert W.plan_window(1920, 1080, zoomed=True).zoomed


def test_ui_factor_choices():
    assert W.ui_factor("auto", 1920, 1080) == 1.0 and W.ui_factor("auto", 1280, 720) == 1.0
    assert W.ui_factor("auto", 2560, 1440) == 1.1 and W.ui_factor("auto", 3840, 2160) == 1.1
    assert W.ui_factor("125", 1920, 1080) == 1.25 and W.ui_factor("90%", 1280, 720) == 0.9
    assert W.ui_factor("500", 0, 0) == 2.0 and W.ui_factor("lạ", 0, 0) == 1.0
    f = C.FIELD_BY_KEY["UiScale"]
    assert f.default == "auto" and f.choices == ("auto", "90", "100", "110", "125", "150")


def test_tool_status_texts_and_startup_summary():
    from core import tools as T

    ok = T.ToolInfo("mkvmerge", "C:\\MKVToolNix\\mkvmerge.exe", True, "v82.0")
    assert SV.tool_status_text(ok, True) == "✓ v82.0 — tự tìm thấy: C:\\MKVToolNix\\mkvmerge.exe"
    assert SV.tool_status_text(ok, False) == "✓ v82.0 — C:\\MKVToolNix\\mkvmerge.exe"
    miss = T.ToolInfo("alass", None, False, problem="không tìm thấy")
    assert "BẮT BUỘC" in SV.tool_status_text(miss, True) and "đồng bộ phụ đề" in SV.tool_status_text(miss, True)
    assert SV.tool_status_color(miss) == W.LEVEL_COLORS["error"]
    weak = T.ToolInfo("ffprobe", "x", False, problem="không có file này")
    assert SV.tool_status_text(weak, False) == "✗ không có file này — x" and SV.tool_status_color(weak) == W.LEVEL_COLORS["warn"]
    res = {n: (T.ToolInfo(n, f"/{n}", True, "1"), True) for n in T.TOOLS}
    assert SV.tool_summary(res) == [("ok", "Công cụ: mkvmerge ✓ 1 · mkvextract ✓ 1 · ffmpeg ✓ 1 · ffprobe ✓ 1 · alass ✓ 1")]
    res["alass"] = (miss, True)
    lines = SV.tool_summary(res)
    assert lines[0][0] == "warn" and "alass ✗" in lines[0][1]
    assert lines[1][0] == "error" and lines[1][1].startswith("[!] alass")
    assert "không làm được: đồng bộ phụ đề (bước ②)" in lines[1][1]
    # 5 o cong cu nam DAU nhom "Chung"
    chung = [f.key for f in C.FIELDS if f.group == "chung"]
    assert chung[:5] == ["MkvmergePath", "MkvextractPath", "AlassPath", "FfprobePath", "FfmpegPath"]


# ==========================================================================
#  sau duyet GD6: thanh cac buoc, keo khung nhat ky, don file nguon sau mux
# ==========================================================================


def test_step_states_next_is_first_undone_and_enabled():
    # chua chon thu muc phim: buoc 1 khoa -> KHONG buoc nao sang (khong nhay qua buoc dang khoa)
    assert W.step_states([False, False, False, False], [False, False, False, True]) == ["off", "off", "off", "todo"]
    assert W.step_states([False, False, False, False], [True, False, False, True]) == ["next", "off", "off", "todo"]
    assert W.step_states([True, False, False, False], [True, True, True, True]) == ["done", "next", "todo", "todo"]
    assert W.step_states([True, True, True, False], [True, True, True, True]) == ["done", "done", "done", "next"]
    # buoc da xong van bam lai duoc (vd quet lai) nhung hien ✓
    assert W.step_label(0, "Quét thư mục", "done") == "✓ 1. Quét thư mục"
    assert W.step_label(1, "Tìm phụ đề", "next") == "▶ 2. Tìm phụ đề"
    assert W.step_label(3, "Sang ② →", "todo", numbered=False) == "Sang ② →"


def test_step_bar_fit_size_shrinks_only_when_needed():
    class Btn:
        def __init__(self, t):
            self.t = t

        def cget(self, _k):
            return self.t

    bar = W.StepBar.__new__(W.StepBar)
    bar._base = 15
    bar.buttons = {"a": Btn("▶ 1. Trích phụ đề gốc"), "b": Btn("2. Đổi tên")}
    measure = lambda size, text: len(text) * size * 0.6  # noqa: E731
    assert bar.fit_size({"a": 400, "b": 400}, measure) == 15  # du cho: giu co goc
    assert bar.fit_size({"a": 170, "b": 170}, measure) == 12  # 720p: 21 ky tu * 12 * 0,6 + 18 = 169 <= 170
    assert bar.fit_size({"a": 40, "b": 40}, measure) == 10  # khong nho hon 10


def test_log_drag_height():
    from gui import log_panel as LP

    assert LP.drag_height(120, -80, 1.0, 600) == 200  # keo LEN 80 px -> cao them 80
    assert LP.drag_height(120, -80, 1.25, 600) == 184  # Windows 125%: 80 px = 64 don vi logic
    assert LP.drag_height(120, 500, 1.0, 600) == LP.MIN_HEIGHT  # khong nho hon ~2 dong
    assert LP.drag_height(120, -2000, 1.0, 600) == 600  # khong che het cac the


def test_trash_dialog_rows(tmp_path):
    from core.mux import UsedFile

    assert [MV.size_text(n) for n in (0, 999, 1536, 734003200, 5 * (1 << 30) + (1 << 29))] == [
        "0 B", "999 B", "1,5 KB", "700 MB", "5,5 GB"]
    root = tmp_path / "[YTS.MX] Phim (2020)"
    f = root / "Phim.2020.vi.srt"
    f.parent.mkdir()
    f.write_bytes(b"x" * 2048)
    v = MV.trash_values(UsedFile(f, "sub", ["Phim (2020).mkv"]), str(root))
    assert v == {"kind": "Phụ đề ngoài", "file": "Phim.2020.vi.srt", "size": "2,0 KB", "used": "Phim (2020).mkv"}
    assert MV.trash_values(UsedFile(root / "mat.mkv", "video"), None)["size"] == "?"


def test_new_config_key_trash_after_mux():
    assert C.Config({})["AskTrashSourcesAfterMux"] is True
    assert C.FIELD_BY_KEY["AskTrashSourcesAfterMux"].group == "mux"


# ==========================================================================
#  1.1.0: an buoc + danh so lai, bang audio moi file, phim da co phu de
# ==========================================================================


def test_visible_step_states_skips_hidden_steps():
    keys = ["extract", "preview", "rename", "next"]
    # buoc an khong bao gio la "buoc ke tiep" va khong chan buoc sau no
    assert W.visible_step_states(keys, {"extract"}, [False, False, False, False], [True, True, False, True]) == [
        "hidden", "next", "off", "todo"]
    assert W.visible_step_states(keys, (), [False, False, False, False], [True, True, False, True]) == [
        "next", "todo", "off", "todo"]
    assert W.visible_step_states(keys, {"extract"}, [False, True, False, False], [False, True, True, True]) == [
        "hidden", "done", "next", "todo"]


def _va(name, ep, *streams):
    from core import probe

    return subprep.VideoAudio(Path(name), ep, [probe.AudioStream(i, *s) for i, s in enumerate(streams)])


def test_pick_audio_stream_per_file():
    from core import probe

    two = [probe.AudioStream(0, "eng", "eac3", 6, "", False), probe.AudioStream(1, "cze", "aac", 2, "", True)]
    AC = subprep.AudioChoice
    assert subprep.pick_audio_stream(two, AC("auto"))[::2] == (1, False)  # track Default
    assert subprep.pick_audio_stream(two[:1], AC("auto"))[::2] == (0, False)  # khong co Default -> track dau
    assert subprep.pick_audio_stream(two, AC("index", 0))[::2] == (0, False)
    i, desc, miss = subprep.pick_audio_stream(two, AC("index", 5))
    assert (i, miss) == (1, True) and desc.startswith("không có #5 → ")
    assert subprep.pick_audio_stream(two, AC("lang", "en"))[::2] == (0, False)
    i, desc, miss = subprep.pick_audio_stream(two, AC("lang", "vi"))
    assert (i, miss) == (1, True) and desc.startswith("không có 'vi' → ")
    assert subprep.pick_audio_stream([], AC("auto")) == (0, "không đọc được danh sách audio → thử track đầu", False)


def test_audio_rows_and_summary_for_every_file():
    en, cs = ("eng", "eac3", 6, "Surround", True), ("cze", "aac", 2, "", False)
    files = [_va("Show.S01E01.mkv", "S01E01", en, cs), _va("Show.S01E02.mkv", "S01E02", en, cs),
             _va("Show.S01E03.mkv", "S01E03", cs), _va("Show.S01E04.mkv", "S01E04")]
    rows = PV.audio_rows(files, subprep.AudioChoice("lang", "cs"))
    assert [r[0] for r in rows] == ["a0_0", "a0_1", "a1_0", "a1_1", "a2_0", "a3_x"]
    assert PV.audio_iid_index("a12_3") == 3
    assert rows[0][1] == {"ep": "S01E01", "file": "Show.S01E01.mkv", "idx": 0, "lang": "eng", "codec": "eac3", "ch": 6,
                          "title": "Surround", "def": "✓", "ref": ""}
    assert rows[1][1]["file"] == "" and rows[1][1]["ref"] == PV.REF_MARK
    assert [r[2] for r in rows] == ["", "", "", "", "need", "bad"]  # tap 3 khac bo cuc; tap 4 khong doc duoc
    assert rows[4][1]["ref"] == PV.REF_MARK
    # tap 3 khong co so thu tu 1 -> dung Default, ghi ro
    by_index = PV.audio_rows(files, subprep.AudioChoice("index", 1))
    assert by_index[4][1]["ref"].startswith(PV.REF_MARK + " (Default — không có")
    assert all(r[1]["ref"] == "" for r in PV.audio_rows(files, None))
    txt, level = PV.audio_summary(files)
    assert "4 file phim" in txt and "1 file có bố cục audio KHÁC" in txt and "1 file không đọc được" in txt and level == "warn"
    assert PV.audio_summary(files[:2]) == ("Audio track của 2 file phim — mọi file cùng bố cục audio.", "info")
    assert PV.audio_summary([])[1] == "warn" and PV.audio_summary(files[3:])[1] == "error"
    assert PV.audio_summary(files[:1])[0].endswith(": Show.S01E01.mkv")


def test_item_status_video_already_has_sub():
    v = VideoInfo(Path("Phim.mkv"), "Phim", None, "Phim", 2020, 23.976, [])
    it = FetchItem(v, "en", Path("srt_can_sync/en"), already=Path("Phim.mkv"), already_note="trong MKV: track 2 SRT")
    assert FV.item_status(it) == ("phim đã có (trong MKV: track 2 SRT) — không tìm", "dim")
    it2 = FetchItem(v, "vi", Path("srt_can_sync/vi"), already=Path("Phim.vi.srt"), already_note="cạnh phim")
    assert FV.item_status(it2) == ("phim đã có (cạnh phim): Phim.vi.srt — không tìm", "dim")
    it3 = FetchItem(v, "cs", Path("srt_can_sync/cs"), already=Path("srt_can_sync/cs/Phim.cs.srt"))
    assert FV.item_status(it3) == ("đã có: Phim.cs.srt", "dim")


# ==========================================================================
#  1.2.0: thanh tien do xanh la + phan tram
# ==========================================================================


def test_progress_percent_and_text():
    assert W.progress_percent(0, 8) == 0 and W.progress_percent(3, 8) == 37 and W.progress_percent(8, 8) == 100
    assert W.progress_percent(199, 200) == 99  # chua xong het thi chua ghi 100%
    assert W.progress_percent(5, 0) == 0 and W.progress_percent(9, 8) == 100 and W.progress_percent(-1, 8) == 0
    assert W.progress_text(3, 8) == "37% · 3/8" and W.progress_text(0, 0) == "0%" and W.progress_text(8, 8) == "100% · 8/8"
    assert all(c.startswith("#") for c in W.PROGRESS_GREEN)
