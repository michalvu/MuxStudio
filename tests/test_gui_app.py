"""Giao diện chạy THẬT (Tk + CustomTkinter) — bấm nút bằng code, chờ luồng phụ, kiểm tra kết quả trên đĩa.

* ③ Mux: MKV thật (ffmpeg + mkvmerge), 2 audio tiếng Anh → "Cần chọn" → tick xoá + "Áp dụng cho mọi file còn lại";
  bảng chọn phụ đề theo tập; "Xem lệnh"; Bắt đầu mux → Output; màn hình Hoàn tác.
* ① Tải phụ đề: OpenSubtitles giả (không gọi mạng thật) → quét → tìm → chọn trong bảng ứng viên → tải.
* ② Chuẩn bị phụ đề: xem trước đổi tên → đổi tên → lập cặp → đồng bộ (alass GIẢ) → sức khoẻ, xuất CSV.
* Cài đặt: mọi ô đọc/ghi đúng kiểu (đủ 5 nhóm), lưu bí mật vào keyring (không vào config.json), báo lỗi ô sai kiểu,
  nhập từ .bat.

Không có tkinter / màn hình (Linux không có DISPLAY) → tự bỏ qua. Trên Windows chạy bình thường (cửa sổ được ẩn).
"""
from __future__ import annotations

import json
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

import pytest

from core import config as C
from core import secrets as S
from core import tools
from synth import make_ref_times, srt_text, transform_times

try:
    import tkinter

    import customtkinter  # noqa: F401

    _r = tkinter.Tk()
    _r.destroy()
    HAS_TK = True
except Exception:  # noqa: BLE001
    HAS_TK = False

pytestmark = pytest.mark.skipif(not HAS_TK, reason="không có tkinter / customtkinter / màn hình")

ROOT = Path(__file__).resolve().parent.parent
FAKE = Path(__file__).resolve().parent / "fake_alass.py"


def _find(name):
    return shutil.which(name) or tools.find_tool(name, C.Config({}))


T = {n: _find(n) for n in ("mkvmerge", "mkvextract", "ffmpeg", "ffprobe")}
need_media = pytest.mark.skipif(not all(T.values()), reason="thiếu mkvmerge/mkvextract/ffmpeg/ffprobe")


# ---------------------------------------------------------------- tien ich
@pytest.fixture
def ui(monkeypatch):
    """Tạo App (ẩn cửa sổ); hộp thoại hỏi Có/Không luôn trả Có; hộp thoại thông báo không chặn."""
    from gui import app as A
    from gui import widgets as W

    shown: dict[str, list] = {"info": [], "warn": [], "text": []}
    monkeypatch.setattr(W, "ask_yes_no", lambda *a, **k: True)
    monkeypatch.setattr(W, "confirm_list", lambda *a, **k: True)
    monkeypatch.setattr(W, "info", lambda _p, t, m: shown["info"].append((t, m)))
    monkeypatch.setattr(W, "warn", lambda _p, t, m: shown["warn"].append((t, m)))
    monkeypatch.setattr(W, "show_text", lambda _p, t, text, note="": shown["text"].append(text))
    from gui import mux_view as MV

    class FakeTrashDialog:
        """Hộp thoại "Dọn file nguồn đã dùng" giả (không chặn): ghi lại danh sách, trả lời theo ``answer``."""

        answer: Any = None  # None = "Khong xoa"; ham(cands) -> danh sach file tick

        def __init__(self, _view, cands):
            shown["trash"].append(list(cands))
            self.cands = cands

        def run(self):
            a = FakeTrashDialog.answer
            return a(self.cands) if callable(a) else a

    shown["trash"] = []
    shown["TrashDialog"] = FakeTrashDialog
    monkeypatch.setattr(MV, "TrashDialog", FakeTrashDialog)
    apps = []

    def make(folder, cfg=None):
        a = A.App(cfg=C.Config({"DeleteToRecycleBin": False, **(cfg or {})}), store=S.SecretStore(),
                  folder=str(folder), appearance="light")
        a.root.withdraw()
        a.shown = shown
        apps.append(a)
        pump(a)
        return a

    yield make
    for a in apps:
        a.runner.wait(30)
        a.destroy()


def pump(a, timeout=60):
    """Chờ mọi việc ở luồng phụ xong + xử lý sự kiện Tk."""
    a.runner.wait(timeout)
    a.root.update()
    a.runner.wait(timeout)
    a.root.update_idletasks()


def log_text(a):
    return "\n".join(m for _t, _l, m in a.log_panel.lines)


def tiny_mkv(path, dur=2):
    subprocess.run([T["ffmpeg"], "-v", "error", "-y", "-f", "lavfi", "-i", f"color=c=black:s=64x36:r=24000/1001:d={dur}",
                    "-c:v", "mpeg4", str(path)], check=True)


def mux_mkv(path, subs, dur=2):
    """Video + audio: tiếng Anh 2.0 (#1), tiếng Anh 5.1 (#2), tiếng Séc (#3) + phụ đề trong ``subs``."""
    base = path.with_name(path.stem + ".base.mkv")
    subprocess.run([T["ffmpeg"], "-v", "error", "-y", "-f", "lavfi", "-i", f"color=c=black:s=64x36:r=24000/1001:d={dur}",
                    "-f", "lavfi", "-i", f"sine=frequency=440:duration={dur}",
                    "-f", "lavfi", "-i", f"sine=frequency=660:duration={dur}",
                    "-f", "lavfi", "-i", f"sine=frequency=880:duration={dur}",
                    "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-c:v", "mpeg4", "-c:a", "aac",
                    "-ac:a:0", "2", "-ac:a:1", "6", "-ac:a:2", "2",
                    "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=eng",
                    "-metadata:s:a:2", "language=cze", str(base)], check=True)
    cmd = [T["mkvmerge"], "-q", "-o", str(path), str(base)]
    for f, lang, name in subs:
        cmd += ["--language", f"0:{lang}"] + (["--track-name", f"0:{name}"] if name else []) + [str(f)]
    r = subprocess.run(cmd, capture_output=True)
    assert r.returncode in (0, 1), r.stdout
    base.unlink()


def mkv_tracks(path):
    r = subprocess.run([T["mkvmerge"], "-J", str(path)], capture_output=True, encoding="utf-8")
    return json.loads(r.stdout)["tracks"]


# ---------------------------------------------------------------- ③ Mux
@need_media
def test_mux_tab_end_to_end(movie_dir, tmp_path, ui):
    en = tmp_path / "en.srt"
    en.write_text(srt_text(make_ref_times(40, seed=3)), encoding="utf-8", newline="")
    eps = []
    for n in (1, 2):
        p = movie_dir / f"Show.S01E0{n}.1080p.WEB.mkv"
        mux_mkv(p, [(en, "eng", ""), (en, "eng", "English SDH")])
        eps.append(p)
    (movie_dir / "Show.S01E01.1080p.WEB.vi.srt").write_text(srt_text(make_ref_times(40, seed=4)), encoding="utf-8", newline="")
    a = ui(movie_dir)
    v = a.mux
    a.goto("③ Mux")
    pump(a)
    # chua chon database -> nut khoa
    assert v.db.get() == "" and str(v.plan_btn.cget("state")) == "disabled"
    v.db.set("none")
    v.db_changed()
    assert str(v.plan_btn.cget("state")) == "normal"
    v.plan_all()
    pump(a)
    assert [p.status for p in v.plans] == ["need_choice", "need_choice"]  # 2 audio tieng Anh (Q3)
    assert "cần chọn" in v.hint.cget("text")
    rows = [v.table.tv.item(i, "values") for i in v.table.ids()]
    assert all("Cần chọn" in r[-1] for r in rows)

    # bang audio cua file 1: tick XOA tieng Anh 2.0 (#1), ap dung cho moi file con lai
    v._on_select(["p0"])
    assert set(v.atable.checked) == {"a1", "a2", "a3"} and not any(v.atable.checked.values())
    v.atable.update("a1", checked=True)
    v.apply_rest.set(True)
    v.apply_audio()
    pump(a)
    assert [p.status for p in v.plans] == ["ok", "ok"]
    assert [x.keep_audio for x in v.plans] == [[2, 3], [2, 3]]
    assert v.plans[1].rule_applied.startswith("chỉ giữ English (5.1)")

    # bang chon phu de theo tap: sub ngoai vi chi co o tap 1 (o xam o tap 2); bat English SDH cho tap 1
    m = v.grid.model
    assert m.rows == ["m0", "m1"] and m.cols[0] == "E|vi"
    assert m.present("m0", "E|vi") and not m.present("m1", "E|vi")
    sdh = next(c for c in m.cols if "SDH" in c)
    assert m.get("m0", sdh) is False  # tu dong: bo English SDH vi da co English thuong
    m.toggle_cell("m0", sdh)
    v._grid_changed()
    assert m.differs("m0", sdh) and m.changed_rows() == ["m0"]
    v.apply_grid()
    pump(a)
    assert [s.id for s in v.plans[0].keep_subs] == [4, 5]
    assert [s.id for s in v.plans[1].keep_subs] == [4]
    assert v.grid.model.differs("m0", sdh)  # bang van nho lua chon sau khi lap lai ke hoach

    # Xem lenh: khong ghi file nao
    v._on_select(["p0"])
    v.show_commands()
    pump(a)
    cmd = a.shown["text"][-1]
    assert "--audio-tracks \"2,3\"" in cmd and "--track-order" in cmd and not (movie_dir / "Output").exists()

    # Bat dau mux
    v.start_mux()
    pump(a, 120)
    outs = [movie_dir / "Output" / f"{p.stem}.mkv" for p in eps]
    assert all(o.is_file() for o in outs)
    assert all(p.result is not None and p.result.ok for p in v.plans)
    assert not any(p.selected for p in v.plans)  # da mux xong -> bo tick (khong mux lai)
    assert "Thành công: 2" in v.summary.cget("text")
    # Mux xong: tu de xuat dua file nguon da dung vao Thung rac (yeu cau sau duyet GD6)
    for _ in range(40):
        if a.shown["trash"]:
            break
        a.root.update()
        time.sleep(0.05)
    srt_vi = movie_dir / "Show.S01E01.1080p.WEB.vi.srt"
    assert [(u.path, u.kind) for u in a.shown["trash"][0]] == [(eps[0], "video"), (eps[1], "video"), (srt_vi, "sub")]
    assert v.steps.states["trash"] == "done" and v.steps.states["out"] == "next"  # bam "Khong xoa"
    a.shown["TrashDialog"].answer = lambda cands: [u for u in cands if u.path != eps[1]]
    v.offer_trash()  # bam lai nut "3. Don file nguon da dung", tick 2/3 file
    pump(a)
    assert not eps[0].exists() and not srt_vi.exists() and eps[1].exists() and all(o.is_file() for o in outs)
    assert "DỌN FILE NGUỒN: 2 file đã XOÁ HẲN" in log_text(a)  # fixture: DeleteToRecycleBin tat
    assert [u.path for u in v.trash_cands] == [eps[1]]
    t1 = mkv_tracks(outs[0])
    names = [t["properties"].get("track_name") for t in t1 if t["type"] == "audio"]
    assert names == ["English AAC 5.1", "Czech AAC 2.0"]
    assert sum(1 for t in t1 if t["type"] == "subtitles") == 3  # vi ngoai + English + English SDH

    # 1.1.0: nut "↺ Lam lai tu dau" -> moi bang / tong ket / tien do / lua chon database ve nhu chua lam gi
    assert v.plans and v.summary.cget("text") and v.steps.states["plan"] == "done" and v.db.get()
    v.reset_all()
    a.root.update()
    assert v.plans == [] and v.session is None and v.db.get() == "" and v.summary.cget("text") == ""
    assert not v.table.ids() and not v.atable.ids() and not v.dtable.ids() and v.trash_cands == []
    assert v.steps.states == {"plan": "off", "run": "off", "trash": "off", "out": "todo"}  # chua chon database
    assert v.steps.buttons["plan"].cget("text") == "1. Quét & lập kế hoạch"  # het ✓
    assert all(o.is_file() for o in outs) and eps[1].exists()  # khong xoa file nao
    assert "③ Đã làm lại từ đầu" in log_text(a)

    # Hoan tac (man hinh chung)
    from gui import app as A

    ents = A.undo_entries(movie_dir, a.cfg)
    assert [e.step for e in ents] == ["mux"] and A.counts_text(ents[0].run.counts) == "1 thư mục mới, 2 tạo, 2 vào Thùng rác"
    d = A.UndoDialog(a, str(movie_dir))
    d.t.update("u0", checked=True)
    d.do_undo()
    pump(a)
    assert not any(o.exists() for o in outs)
    assert "HOÀN TÁC: " in log_text(a)
    d.d.close()


@need_media
def test_mux_name_need_choice_dialog_actions(movie_dir, ui, monkeypatch):
    """Dòng "Cần chọn" tên (TMDB không khớp chính xác): Tìm lại với tên… → chọn kết quả; bấm Bắt đầu khi còn dòng
    cần chọn → hỏi giữ tên gốc."""
    from fakehttp import FakeHTTP, FakeResponse
    from gui import mux_view as MV
    from gui import widgets as W

    tiny_mkv(movie_dir / "Dune.Part.2.2024.1080p.mkv")
    dune = {"id": 693134, "title": "Dune: Part Two", "original_title": "Dune: Part Two", "release_date": "2024-02-27"}
    dune21 = {"id": 438631, "title": "Dune", "original_title": "Dune", "release_date": "2021-09-15"}
    h = FakeHTTP()
    h.add("GET", r"/configuration$", FakeResponse(200, {"images": {}}))
    h.add("GET", r"/search/movie$", lambda c: FakeResponse(200, {"results": [dune, dune21] if "Dune" in c.params["query"]
                                                                  else []}))
    import core.mux as M

    real = M.MuxSession.__init__

    def init(self, td, cfg, secrets, job=None, http=None, sleep=None, tool_paths=None):
        real(self, td, cfg, secrets, job, h, lambda s: None, tool_paths)

    monkeypatch.setattr(M.MuxSession, "__init__", init)
    a = ui(movie_dir, {"AddCodecTag": False})
    a.secret_store.set("TmdbApiKey", "demo-tmdb-key-0123456789abcdef01")
    v = a.mux
    v.db.set("tmdb")
    v.plan_all()
    pump(a)
    p = v.plans[0]
    assert p.status == "need_choice" and p.name.status == "need_choice" and len(p.name.candidates) == 2
    # Tim lai voi ten "Dune" (2021) -> van "Can chon" voi ket qua moi
    acts = iter([("search", "Dune", 2021), None])
    monkeypatch.setattr(MV.NameDialog, "run", lambda self: next(acts))
    v.edit_name(p)
    pump(a)
    assert v.plans[0].name.note.startswith("TMDB: kết quả tìm 'Dune' (2021)")
    assert [x.title for x in v.plans[0].name.candidates] == ["Dune: Part Two", "Dune"]
    # chon ket qua thu hai
    monkeypatch.setattr(MV.NameDialog, "run", lambda self: ("choose", 438631))
    v.edit_name(v.plans[0])
    pump(a)
    assert v.plans[0].status == "ok" and v.plans[0].out_base == "Dune (2021) [tmdbid-438631]"
    # sua tay
    monkeypatch.setattr(MV.NameDialog, "run", lambda self: ("set", "Tên: Tự Đặt?"))
    v.edit_name(v.plans[0])
    pump(a)
    assert v.plans[0].out_base == "Tên - Tự Đặt"
    # giu ten goc
    monkeypatch.setattr(MV.NameDialog, "run", lambda self: ("keep",))
    v.edit_name(v.plans[0])
    pump(a)
    assert v.plans[0].out_base == "Dune.Part.2.2024.1080p"
    # Bat dau khi con dong "Can chon" ten: tra loi "Khong" -> khong mux gi
    v.reset()
    v.plan_all()
    pump(a)
    assert v.plans[0].name.status == "need_choice"
    monkeypatch.setattr(W.messagebox, "askyesnocancel", lambda *x, **k: False)
    v.start_mux()
    pump(a)
    assert not (movie_dir / "Output").exists()
    # "Co" -> giu ten goc roi mux
    monkeypatch.setattr(W.messagebox, "askyesnocancel", lambda *x, **k: True)
    v.start_mux()
    pump(a, 120)
    assert (movie_dir / "Output" / "Dune.Part.2.2024.1080p.mkv").is_file()


# ---------------------------------------------------------------- ① Tai phu de
@need_media
def test_fetch_tab_choose_and_download(movie_dir, ui, monkeypatch):
    from core import subfetch
    from fakehttp import FakeHTTP, FakeResponse, Sleeper
    from gui import fetch_view as FV

    stem = "Phim.Thu.2020.1080p.WEB"
    tiny_mkv(movie_dir / f"{stem}.mkv")
    ref = make_ref_times(400, seed=11)
    goc = movie_dir / "srt_goc"
    goc.mkdir()
    (goc / f"{stem}.srt").write_text(srt_text(ref), encoding="utf-8", newline="")
    # TD-38: thu muc kho cu con tren dia nhung KHONG con la nguon
    pool = movie_dir / "srt_pool" / "vi"
    pool.mkdir(parents=True)
    (pool / "Phim.Thu.2020.BluRay.vi.srt").write_text(srt_text(transform_times(ref, 1.0, 1.5, seed=5)), encoding="utf-8", newline="")
    # OpenSubtitles gia: 2 ban tieng Viet
    http = FakeHTTP()
    http.add("POST", r"opensubtitles\.com/api/v1/login", FakeResponse(200, {
        "token": "tok", "base_url": "api.opensubtitles.com", "user": {"allowed_downloads": 100, "vip": False}}))
    http.add("GET", r"opensubtitles\.com/api/v1/infos/user",
             FakeResponse(200, {"data": {"remaining_downloads": 100, "allowed_downloads": 100}}))
    found = [{"id": "a", "attributes": {"release": "Phim.Thu.2020.BluRay", "fps": 23.976, "download_count": 9,
                                        "files": [{"file_id": 501}]}},
             {"id": "b", "attributes": {"release": "Phim.Thu.2020.WEB-DL", "fps": 23.976, "download_count": 5,
                                        "files": [{"file_id": 502}]}}]
    http.add("GET", r"opensubtitles\.com/api/v1/subtitles",
             lambda call: FakeResponse(200, {"data": found if call.params.get("languages") == "vi" else []}))
    http.add("POST", r"opensubtitles\.com/api/v1/download", lambda call: FakeResponse(
        200, {"link": f"https://dl.opensubtitles.com/{call.json['file_id']}", "remaining": 99, "reset_time": "5 giờ"}))
    http.add("GET", r"dl\.opensubtitles\.com/50[12]",
             FakeResponse(200, content=srt_text(transform_times(ref, 1.0, -0.8, seed=6)).encode("utf-8")))
    monkeypatch.setattr(FV, "FetchSession", lambda folder, cfg, _secrets, job, languages=None, sources=None:
                        subfetch.FetchSession(folder, cfg, {"OsApiKey": "demo-os", "OsUsername": "u", "OsPassword": "p"},
                                              job, http, Sleeper(), languages=languages, sources=sources))
    a = ui(movie_dir)
    v = a.fetch
    a.goto("① Tải phụ đề")
    pump(a)
    assert set(v.src_vars) == set(v.src_status) == {"subsource", "titulky", "opensubtitles"}  # het o "Kho noi bo"
    assert v.src_status["titulky"].cget("text") == "kiểu khách (chưa điền tài khoản)"  # khong goi mang khi chua co tai khoan
    for c, var in v.lang_vars.items():
        var.set(c == "vi")
    v.src_vars["subsource"].set(False)
    v.scan()
    pump(a)
    assert len(v.items) == 1 and v.items[0].lang == "vi"
    assert v.tree.full("v0")["ref"].startswith("dùng lại srt_goc")
    v.gather()
    pump(a)
    it = v.items[0]
    assert it.needs_choice and len(it.candidates) == 2  # 2 ban OpenSubtitles -> ban chon
    assert v.cur_item is it  # tu nhay toi dong "can chon" dau tien
    assert "CẦN CHỌN" in v.tree.tv.item("i0", "values")[3]
    rows = [v.cands.tv.item(i, "values") for i in v.cands.ids()]
    assert {r[1] for r in rows} == {"OpenSub"}
    v.choose("c1")
    pump(a)
    assert it.chosen is it.candidates[1] and not it.needs_choice
    v.execute()
    pump(a)
    dest = movie_dir / "srt_can_sync" / "vi" / f"{stem}.vi.srt"
    assert dest.is_file() and it.result == dest
    assert "Tải được: 1" in v.summary.cget("text")
    assert "đã tải (OpenSub)" in v.tree.tv.item("i0", "values")[3]
    # 1.1.0: nut "↺ Lam lai tu dau"
    assert v.steps.states["run"] == "done"
    v.reset_all()
    assert v.items == [] and v.session is None and not v.tree.ids() and not v.cands.ids()
    assert v.summary.cget("text") == "" and dest.is_file()
    assert v.steps.states == {"scan": "next", "gather": "off", "run": "off", "next": "todo"}


# ---------------------------------------------------------------- ② Chuan bi phu de
def test_subprep_tab_rename_sync_export(movie_dir, ui, tmp_path, monkeypatch):
    conf = tmp_path / "fake_alass.json"
    conf.write_text(json.dumps({"offset": 0}), encoding="utf-8", newline="")
    monkeypatch.setenv("FAKE_ALASS", str(conf))
    real = tools.find_all_tools
    monkeypatch.setattr(tools, "find_all_tools",
                        lambda cfg, td=None: {**real(cfg, td), "mkvmerge": None, "alass": [sys.executable, str(FAKE)]})
    ref = make_ref_times(300, seed=21)
    for n in (1, 2):
        (movie_dir / f"Show.S01E0{n}.1080p.mkv").write_bytes(b"\x1a\x45\xdf\xa3")
    goc = movie_dir / "srt_goc"
    goc.mkdir()
    (goc / "Show.S01E01.1080p.srt").write_text(srt_text(ref), encoding="utf-8", newline="")
    (goc / "Show.S01E02.1080p.srt").write_text(srt_text(make_ref_times(300, seed=22)), encoding="utf-8", newline="")
    vi = movie_dir / "srt_can_sync" / "vi"
    vi.mkdir(parents=True)
    src = vi / "Show - 01x01 - WEB.srt"
    src.write_text(srt_text(transform_times(ref, 1.0, 2.0, seed=7)), encoding="utf-8", newline="")
    a = ui(movie_dir, {"AskCleanup": False})
    v = a.prep
    a.goto("② Chuẩn bị phụ đề")
    pump(a)
    assert v.counts.cget("text").startswith("Phim: 2") and "srt_goc': 2" in v.counts.cget("text")
    # moc = audio: hien bang audio cua file mau; chua chon cach lay audio -> khong lap cap
    v.mode.set("audio")
    v._mode_changed()
    pump(a)
    assert v.audio_box.winfo_manager() == "pack" and v.audio_choice() is None
    # 1.1.0: moc = audio -> an han buoc "Trich phu de goc", cac buoc con lai danh so lai tu 1
    assert v.steps.states["extract"] == "hidden" and v.steps.buttons["extract"].winfo_manager() == ""
    assert v.steps.buttons["preview"].cget("text") == "▶ 1. Quét phụ đề"
    assert v.steps.buttons["rename"].cget("text") == "2. Đổi tên" and v.steps.buttons["cleanup"].cget("text") == "5. Dọn dẹp"
    assert v.steps.arrows["preview"].winfo_manager() == "" and v.steps.arrows["rename"].winfo_manager() == "grid"
    assert v.extract_hint.winfo_manager() == ""
    # bang audio: MOI file phim (2 file gia khong doc duoc audio -> 2 dong do)
    assert v.atable.ids() == ["a0_x", "a1_x"] and "KHÔNG đọc được" in v.audio_title.cget("text")
    assert v.atable.full("a1_x")["file"] == "Show.S01E02.1080p.mkv" and v.atable.full("a1_x")["ep"] == "S01E02"
    v.plan_sync()
    assert "Chọn audio làm mốc" in a.shown["warn"][-1][0] and not v.pairs
    v.amode.set("lang")
    assert v.audio_choice() == __import__("core.subprep", fromlist=["x"]).AudioChoice("lang", "en")
    v.mode.set("sub")
    v._mode_changed()
    assert v.audio_box.winfo_manager() == ""
    assert v.steps.states["extract"] == "done" and v.steps.buttons["extract"].winfo_manager() == "grid"
    assert v.steps.buttons["extract"].cget("text") == "✓ 1. Trích phụ đề gốc"
    assert v.steps.buttons["preview"].cget("text") == "▶ 2. Quét phụ đề" and v.steps.arrows["preview"].winfo_manager() == "grid"
    v.plan_rename()
    pump(a)
    assert len(v.renames) == 1 and v.renames[0].ok
    vals = v.rtable.tv.item("r0", "values")
    assert vals[0] == "☑" and vals[3] == "Show.S01E01.1080p.vi.srt"
    v.do_rename()
    pump(a)
    new = vi / "Show.S01E01.1080p.vi.srt"
    assert new.is_file() and not src.exists()
    from gui import subprep_view as PV

    assert v.inner.get() == PV.INNER_SYNC
    v.plan_sync()
    pump(a)
    assert len(v.pairs) == 1 and v.pairs[0].ok
    assert "dự báo: lệch +2s" in v.stable.tv.item("p0", "values")[5]
    v.show_commands()
    assert "fake_alass.py" in a.shown["text"][-1]
    v.do_sync()
    pump(a)
    out = movie_dir / "Show.S01E01.1080p.vi.srt"
    assert out.is_file() and new.is_file()  # file nguon trong srt_can_sync KHONG bi sua / xoa
    vals = v.stable.tv.item("p0", "values")
    assert vals[0] == "" and vals[6] in ("TỐT", "CẦN XEM", "CÓ VẤN ĐỀ")
    from gui import subprep_view as SV

    csvp = tmp_path / "bao cao [1].csv"
    assert SV.export_csv(v.pairs, csvp) == 1
    assert csvp.read_bytes().startswith(b"\xef\xbb\xbf\"Ph\xe1\xbb\xa5 \xc4\x91\xe1\xbb\x81\"")
    v.only_bad.set(True)
    v.fill_pairs()
    assert len(v.stable.ids()) == (1 if SV.pair_needs_review(v.pairs[0]) else 0)
    # mot nhat ky cho ca luot (doi ten + dong bo)
    from gui import app as A

    ents = A.undo_entries(movie_dir, a.cfg)
    assert len(ents) == 1 and ents[0].run.counts.get("Rename") == 1 and ents[0].run.counts.get("Create") == 1
    # 1.1.0: dong bo xong ma CHUA qua hop thoai don dep -> buoc "Don dep" khong tu ✓
    assert v.steps.states["sync"] == "done" and v.steps.states["cleanup"] == "next"
    # nut "↺ Lam lai tu dau": bang + thanh cac buoc ve mac dinh, phien moi (nhat ky moi), khong xoa file
    old = v.session
    v._cleaned = True
    v.update_buttons()
    assert v.steps.states["cleanup"] == "done"
    v.reset_all()
    pump(a)
    assert v.renames == [] and v.pairs == [] and not v.rtable.ids() and not v.stable.ids()
    assert v.session is not None and v.session is not old and not v.only_bad.get() and v.inner.get() == PV.INNER_RENAME
    assert v.rename_info.cget("text") == "" and v.sync_info.cget("text") == ""
    assert v.steps.states == {"extract": "done", "preview": "next", "rename": "off", "pairs": "todo", "sync": "off",
                              "cleanup": "off", "next": "todo"}
    assert v.steps.buttons["cleanup"].cget("text") == "6. Dọn dẹp" and v.mode.get() == "sub"
    assert out.is_file() and new.is_file() and "② Đã làm lại từ đầu" in log_text(a)


def test_subprep_audio_table_lists_every_file(movie_dir, ui, monkeypatch):
    """TĐ-35: mốc = audio → bảng audio của MỌI file phim, đánh dấu track làm mốc của từng file."""
    from core import probe, subprep
    from gui import subprep_view as PV

    def st(i, lang, codec="eac3", ch=6, title="", default=False):
        return probe.AudioStream(i, lang, codec, ch, title, default)

    files = [subprep.VideoAudio(movie_dir / "Show.S01E01.mkv", "S01E01", [st(0, "eng", default=True), st(1, "cze", "aac", 2)]),
             subprep.VideoAudio(movie_dir / "Show.S01E02.mkv", "S01E02", [st(0, "eng", default=True), st(1, "cze", "aac", 2)]),
             subprep.VideoAudio(movie_dir / "Show.S01E03.mkv", "S01E03", [st(0, "cze", "aac", 2, default=True)])]
    monkeypatch.setattr(subprep.PrepSession, "all_audio_streams", lambda self: files)
    a = ui(movie_dir)
    v = a.prep
    a.goto("② Chuẩn bị phụ đề")
    v.mode.set("audio")
    v._mode_changed()
    pump(a)
    assert v.atable.ids() == ["a0_0", "a0_1", "a1_0", "a1_1", "a2_0"]
    assert "3 file phim" in v.audio_title.cget("text") and "1 file có bố cục audio KHÁC" in v.audio_title.cget("text")
    assert v.atable.full("a1_0")["ep"] == "S01E02" and v.atable.full("a1_1")["ep"] == ""  # ten tap chi ghi dong dau
    assert v.atable.tv.item("a2_0", "tags") == ("need",) and not v.atable.tv.item("a0_0", "tags")
    assert not any(v.atable.full(i)["ref"] for i in v.atable.ids())  # chua chon cach lay audio -> chua danh dau
    v.amode.set("auto")
    v.mark_audio()
    assert [i for i in v.atable.ids() if v.atable.full(i)["ref"]] == ["a0_0", "a1_0", "a2_0"]
    v.alang.delete(0, "end")
    v.alang.insert(0, "cs")
    v.amode.set("lang")
    v.mark_audio()
    assert [i for i in v.atable.ids() if v.atable.full(i)["ref"]] == ["a0_1", "a1_1", "a2_0"]
    v.atable.select("a1_1")  # bam mot dong -> "theo so thu tu" = so 1 cho MOI file
    a.root.update()
    assert v.amode.get() == "index" and v.audio_choice() == subprep.AudioChoice("index", 1)
    assert v.atable.full("a0_1")["ref"] == PV.REF_MARK and v.atable.full("a2_0")["ref"].startswith(PV.REF_MARK + " (Default — không có")


# ---------------------------------------------------------------- Cai dat
def test_settings_every_field_roundtrip_and_save(movie_dir, ui, memory_keyring):
    from gui import app as A

    a = ui(movie_dir)
    v = a.settings
    a.goto(A.TAB_SETTINGS)
    for label in C.GROUPS.values():
        v.group_var.set(label)
        v.show_group()
    a.root.update()
    assert set(v.rows) == {f.key for f in C.FIELDS}
    vals, errors = v.collect()
    assert errors == []
    assert {k: vals[k] for k in vals} == {f.key: a.cfg[f.key] for f in C.FIELDS}
    assert not v.is_dirty()
    # sua vai o + mot bi mat
    v.rows["MinVerifyVotes"].entry.delete(0, "end")
    v.rows["MinVerifyVotes"].entry.insert(0, "25")
    v.rows["DonorOffsetMs"].text.insert("1.0", "S01E03 = 1200\ntap #643 = -500\n")
    v.rows["Languages"].vars["ja"].set(True)
    v.rows["CodecTagParts"].vars["hdr"].set(False)
    v.secret_rows["TmdbApiKey"].entry.insert(0, "demo-tmdb-key-0123456789abcdef01")
    assert v.is_dirty()
    assert v.save()
    assert a.cfg["MinVerifyVotes"] == 25 and a.cfg["DonorOffsetMs"] == {"S01E03": 1200, "tap #643": -500}
    assert a.cfg["Languages"] == ["vi", "en", "cs", "ja"] and a.cfg["CodecTagParts"] == ["res", "vcodec", "acodec"]
    saved = C.config_path().read_text(encoding="utf-8")
    assert '"MinVerifyVotes": 25' in saved and "demo-tmdb-key" not in saved
    assert memory_keyring.get_password("MuxStudio", "TmdbApiKey") == "demo-tmdb-key-0123456789abcdef01"
    a.log("info", "key = demo-tmdb-key-0123456789abcdef01")
    assert "demo-tmdb-key-0123456789abcdef01" not in log_text(a)  # log luon che bi mat
    # o sai kieu -> khong luu, bao loi
    v.rows["ApiPauseMs"].entry.delete(0, "end")
    v.rows["ApiPauseMs"].entry.insert(0, "nhanh")
    assert not v.save()
    assert "Nghỉ giữa các lượt gọi" in a.shown["warn"][-1][1]
    v.reload()
    assert v.rows["ApiPauseMs"].entry.get() == "1100"
    # the 1 doi ngon ngu theo cai dat
    assert a.fetch.lang_vars["ja"].get()


def test_settings_import_from_bat(movie_dir, ui, memory_keyring):
    from gui import settings_view as SV

    a = ui(movie_dir)
    paths = sorted(str(p) for p in (ROOT / "reference").iterdir() if p.suffix == ".bat")
    imp = C.import_from_bat(paths)
    rows = SV.import_rows(imp, a.cfg, lambda k: "")
    assert rows and not any(r.secret and "•" not in r.new and r.new for r in rows)
    d = SV.ImportDialog(a.settings, imp, rows)
    # bo tick mot khoa
    i = next(i for i, r in enumerate(rows) if r.key == "MaxShiftSeconds")
    d.t.update(f"r{i}", checked=False)
    a.cfg["MaxShiftSeconds"] = 99
    # khoa xung dot: chon gia tri thu hai (tick o thu nhat roi o thu hai -> chi o thu hai con tick)
    groups = list(d.groups.values())
    if groups:
        g = groups[0]
        d.t.update(g[0], checked=True)
        d._toggle(g[0], True)
        d.t.update(g[1], checked=True)
        d._toggle(g[1], True)
        assert not d.t.checked[g[0]] and d.t.checked[g[1]]
    d.apply()
    assert a.cfg["MaxShiftSeconds"] == 99  # khong tick -> khong nhap
    assert a.cfg["DonorFolder"] == "audio_nguon" and a.cfg["Languages"]
    assert '"DonorFolder"' in C.config_path().read_text(encoding="utf-8")


# ---------------------------------------------------------------- ten dai + co cua so
LONG_STEM = ("[Judas] Sousou no Frieren - S01E0{n} - The Journey's End and the Hero's Party Departs Again "
             "[1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs]")


def test_long_names_keep_episode_in_tables_and_grid(movie_dir, ui):
    from gui import mux_view as MV
    from gui import widgets as W

    a = ui(movie_dir)
    t = W.Table(a.root, [W.Col("src", "Tên gốc", 220, stretch=True, fit="name"), W.Col("st", "Trạng thái", 120)])
    names = [LONG_STEM.format(n=n) + ".mkv" for n in (1, 2, 3)]
    for i, nm in enumerate(names):
        t.add(f"r{i}", {"src": nm, "st": "Cần chọn: có 2 audio tiếng Anh (#1, #2) — tick track muốn xoá"})
    shown = [t.tv.item(f"r{i}", "values")[0] for i in range(3)]
    assert [f"S01E0{n}" in s for n, s in zip((1, 2, 3), shown)] == [True] * 3  # 3 dong phan biet duoc
    assert all(s.endswith(".mkv") and "…" in s for s in shown)
    assert t.full("r0")["src"] == names[0] and t.cut_text("r0", "src") == names[0]  # re chuot: ten day du
    st = t.tv.item("r0", "values")[1]
    assert st.startswith("Cần chọn") and st.endswith("…") and t.cut_text("r0", "st").endswith("muốn xoá")
    # cot rong ra (keo cot / cua so to) -> hien nhieu hon
    t.tv.column("src", width=2000)
    t.refit()
    assert t.tv.item("r0", "values")[0] == names[0] and t.cut_text("r0", "src") is None
    t.set_cell("r1", "st", "sẵn sàng")
    assert t.tv.item("r1", "values")[1] == "sẵn sàng" and t.full("r1")["src"] == names[1]
    # cot cay (the 1): ten file giu so tap
    tree = W.Table(a.root, [W.Col("x", "X", 50)], tree="Phim", tree_width=230)
    tree.add("v0", {"x": ""}, text=names[1])
    tree.add("c0", {"x": ""}, parent="v0", text="   cs — Czech")
    assert "S01E02" in tree.tv.item("v0", "text") and tree.full("v0")["#0"] == names[1]
    # bang chon phu de theo tap: nhan dong giu so tap
    g = W.CheckGrid(a.root)
    rows = ["m0", "m1", "m2"]
    g.set_model(W.GridModel(rows, ["E|vi"], {(r, "E|vi"): True for r in rows}), {"E|vi": "vi"},
                {r: nm for r, nm in zip(rows, names)})
    assert [f"S01E0{n}" in g.label_text(r) for n, r in zip((1, 2, 3), rows)] == [True] * 3
    assert MV.overview_values  # dung chung Table


def test_title_icon_and_version_first_in_log(movie_dir, ui):
    """GĐ6: tiêu đề "MuxStudio <phiên bản>", icon MuxStudio (cửa sổ + hộp thoại), hai dòng ĐẦU khung log = bản đang chạy."""
    from core import __version__
    from gui import app as A
    from gui import widgets as W

    a = ui(movie_dir)
    assert a.root.title() == A.APP_TITLE == f"MuxStudio {__version__}"
    assert a.icon_ok
    first = a.log_panel.lines[:2]
    assert first[0][1:] == ("head", f"{A.APP_TITLE} — chọn thư mục phim ở trên, rồi làm lần lượt ① → ② → ③.")
    assert first[1][1] == "dim" and first[1][2].startswith(f"{A.APP_TITLE} (chạy từ mã nguồn) | Python ")
    d = W.Dialog(a.root, "Thử", 400, 300)
    if sys.platform.startswith("win"):
        assert d.win._iconbitmap_method_called  # CTk khong de icon cua no len nua
    else:
        assert getattr(d.win, "_muxstudio_icon", None) is not None
    d.close()


def test_window_fits_screen_and_remembers_logical_size(movie_dir, ui):
    from gui import widgets as W

    a = ui(movie_dir)
    sw, sh = W.screen_logical(a.root)
    p = W.plan_window(sw, sh)
    a.root.update()
    assert a.window == p and a.density == p.density
    w, h = (int(x) for x in a.root.geometry().split("+")[0].split("x"))
    assert w <= sw and h <= sh
    a.root.deiconify()  # cua so an: Tk khong bao kich thuoc that
    a.root.update()
    if p.zoomed:  # man hinh nho (< 1400 x 860, vd 1280x1024 cua Xvfb, laptop 1366x768): cua so tu phong to het man hinh
        assert a._is_zoomed()
        a._remember_window()
        assert a.cfg["UiZoomed"] is True
        # nguoi dung bam "thu nho" roi keo cua so: dat lai trang thai thuong TRUOC khi doi co
        if sys.platform.startswith("win"):
            a.root.state("normal")
        else:
            a.root.attributes("-zoomed", False)
        a.root.update()
        if a._is_zoomed():  # Xvfb khong co trinh quan ly cua so: Tk khong bo phong to duoc -> gia lap dung cho nay
            a._is_zoomed = lambda: False
    a.root.geometry("1100x700")
    a.root.update()
    a._remember_window()
    a.root.withdraw()
    assert a.cfg["UiGeometry"] == "1100x700" and a.cfg["UiZoomed"] is False
    # hop thoai to hon man hinh -> tu thu nho cho vua; nut van hien
    d = W.Dialog(a.root, "Thử", 5000, 4000)
    assert d.wrap == int(sw - 40) - 48  # rong = man hinh - 40 (khong phai 5000)
    d.close()
    # o "Co chu va giao dien" co trong Cai dat
    v = a.settings
    assert "UiScale" in v.rows and v.rows["UiScale"].get_value() == "auto"


def test_settings_tools_block_shows_used_files_and_wrong_pick(movie_dir, ui, tmp_path):
    import shutil

    from gui import app as A

    a = ui(movie_dir, {"FfprobePath": _find("ffmpeg") or ""})
    pump(a)
    pump(a)
    v = a.settings
    st = {k: v.rows[k].status.cget("text") for k in ("MkvmergePath", "MkvextractPath", "AlassPath", "FfprobePath", "FfmpegPath")}
    assert all(t and t != "đang kiểm tra…" for t in st.values())
    assert "Công cụ: mkvmerge" in log_text(a)  # bao trong log khi mo chuong trinh
    if _find("mkvmerge"):
        assert st["MkvmergePath"].startswith("✓ v") and "tự tìm thấy" in st["MkvmergePath"]
    if _find("ffmpeg"):
        assert st["FfprobePath"].startswith("✗ không phải ffprobe")  # chon nham ffmpeg vao o ffprobe
    # chon lai dung file -> kiem tra lai ngay
    fr = v.rows["AlassPath"]
    fr.entry.delete(0, "end")
    fr.entry.insert(0, str(tmp_path / "không có alass.exe"))
    v.show_tool_status(fr)
    pump(a)
    assert fr.status.cget("text").startswith("✗ không có file này")
    a.goto(A.TAB_SETTINGS)


def test_log_drag_resize_remembered_and_step_bars(movie_dir, ui):
    """Sau duyệt GĐ6: kéo tay kéo khung Nhật ký (lên = cao hơn, nhớ ``UiLogHeight``); thanh các bước ở 3 thẻ."""
    from gui import widgets as W

    a = ui(movie_dir)
    lp = a.log_panel
    h0 = lp.height

    class Ev:
        def __init__(self, y):
            self.y_root = y

    lp._drag_start(Ev(500))
    lp._drag(Ev(440))  # keo LEN 60 px
    lp._drag_end(Ev(440))
    sc = float(W.ctk.ScalingTracker.get_widget_scaling(lp.box))
    assert lp.height == round(h0 + 60 / sc) and a.cfg["UiLogHeight"] == lp.height
    lp.toggle()  # thu gon roi keo -> tu mo lai
    lp._drag_start(Ev(500))
    lp._drag(Ev(520))
    lp._drag_end(Ev(520))
    assert lp.expanded and a.cfg["UiLogHeight"] == lp.height < round(h0 + 60 / sc)
    b = ui(movie_dir, {"UiLogHeight": 150})  # mo lai: dung chieu cao da keo
    assert b.log_panel.height == min(150, b._log_max())
    # thanh cac buoc: da chon thu muc -> buoc 1 cua the ① sang; the ③ chua chon database -> khong buoc nao sang
    assert a.fetch.steps.states == {"scan": "next", "gather": "off", "run": "off", "next": "todo"}
    assert a.mux.steps.states["plan"] == "off" and "next" not in a.mux.steps.states.values()
    a.mux.db.set("none")
    a.mux.update_buttons()
    assert a.mux.steps.states["plan"] == "next"
    assert a.prep.steps.states["extract"] == "next"  # srt_goc trong, moc = phu de goc


def test_sub_grid_hover_frame_and_boxes(movie_dir, ui):
    from gui import widgets as W

    a = ui(movie_dir)
    g = W.CheckGrid(a.root)
    g.frame.pack()
    m = W.GridModel(["r1", "r2"], ["E|vi", "I|2"], {("r1", "E|vi"): True, ("r1", "I|2"): False, ("r2", "E|vi"): True})
    g.set_model(m, {"E|vi": "NGOÀI\nVietnamese", "I|2": "TRONG MKV\nEnglish"}, {"r1": "Tập 1", "r2": "Tập 2"})
    a.root.update()
    g.redraw()
    lines = [i for i in g.cv.find_all() if g.cv.type(i) == "line"]
    assert len(lines) == 2  # 2 o da tick = 2 dau ✓ ve bang net (o chua tick: chi co hop)
    g._set_hover("r2")
    (hv,) = g.cv.find_withtag("hover")
    assert g.cv.coords(hv)[1] == g.head_h + g.row_h + 1
    m.toggle_cell("r1", "I|2")
    g.redraw()  # ve lai van giu khung re chuot
    assert g.cv.find_withtag("hover") and len([i for i in g.cv.find_all() if g.cv.type(i) == "line"]) == 3
    g._set_hover(None)
    assert not g.cv.find_withtag("hover")


# ---------------------------------------------------------------- 1.2.0: English, Sang/Toi, Gioi thieu, tien do
def test_english_interface_translates_display_but_keeps_logic(movie_dir, ui):
    from core import i18n
    from gui import app as A
    from gui import subprep_view as PV

    for n in (1, 2):
        (movie_dir / f"Show.S01E0{n}.1080p.mkv").write_bytes(b"\x1a\x45\xdf\xa3")
    a = ui(movie_dir, {"UiLanguage": "en"})
    assert a.language == "en" and i18n.language() == "en"
    # ten the HIEN THI bang tieng Anh, nhung get()/goto van dung ten tieng Viet (ma so sanh nhu cu)
    assert a.tabs._segmented_button.cget("values") == ["① Fetch subtitles", "② Prepare subtitles", "③ Mux", "Settings", "About"]
    a.goto(A.TAB_PREP)
    pump(a)
    assert a.tabs.get() == A.TAB_PREP == "② Chuẩn bị phụ đề"
    v = a.prep
    assert v.counts.cget("text").startswith("Videos: 2   |   Subtitles in 'srt_can_sync': 0")
    assert v.reset_btn.cget("text") == "↺ Start over" and v.recount_btn.cget("text") == "Refresh"
    assert v.steps.buttons["extract"].cget("text") == "▶ 1. Extract original subs"
    assert v.steps.buttons["preview"].cget("text") == "2. Scan subtitles"
    assert v.rtable.tv.heading("old", "text") == "Old name" and v.rtable.tv.heading(v.rtable.CHECK, "text") == "Rename"
    # o chon ngon ngu: hien tieng Anh, gia tri van la lua chon tieng Viet -> "auto"
    assert v.lang.var.get().startswith("Auto-detect") and v.lang.get() == PV.AUTO_LANG and v._lang() == "auto"
    assert v.inner.get() == PV.INNER_RENAME  # the con
    v.inner.set(PV.INNER_SYNC)
    assert v.inner.get() == PV.INNER_SYNC
    # moc = audio: buoc trich an, danh so lai, bang audio co tieu de tieng Anh
    v.mode.set("audio")
    v._mode_changed()
    pump(a)
    assert v.steps.buttons["preview"].cget("text") == "▶ 1. Scan subtitles"
    assert "could NOT read any file" in v.audio_title.cget("text")
    assert v.atable.tv.item("a0_x", "values")[6].startswith("(cannot read the audio")
    assert v.atable.full("a0_x")["title"] == "(không đọc được audio track)"  # du lieu goc van tieng Viet
    # nhat ky: dong hien thi (va "Luu log") bang tieng Anh
    text = log_text(a)
    assert "choose the video folder above" in text and "Video folder: " in text
    assert a.status.cget("text").startswith("Done: ")
    # Cai dat: ten nhom dich, nhung gia tri nhom van la ten goc
    s = a.settings
    assert s.seg.cget("values")[0] == "General — folders & tools" and s.group_var.get() == C.GROUPS["chung"]
    a.goto(A.TAB_ABOUT)
    assert a.about.author.cget("text") == "Author: Vũ Hoàng Anh"
    assert a.about.table.tv.heading("lic", "text") == "Licence"


def test_light_dark_switch_about_tab_and_green_percent_progress(movie_dir, ui):
    from gui import app as A
    from gui import widgets as W

    a = ui(movie_dir)
    assert a.glass_theme and W.ctk.ThemeManager.theme["CTkFrame"]["corner_radius"] == 14  # bo mau "kinh"
    assert A.TABS[-1] == A.TAB_ABOUT == "Giới thiệu" and a.tabs.tab(A.TAB_ABOUT) is not None
    assert a.about.title.cget("text") == f"MuxStudio {A.VERSION}" and a.about.author.cget("text") == "Tác giả: Vũ Hoàng Anh"
    assert len(a.about.table.ids()) == 3 + len(__import__("gui.about_view", fromlist=["x"]).CREDITS)
    a.about.copy()
    assert a.root.clipboard_get().startswith(f"MuxStudio {A.VERSION}\nTác giả: Vũ Hoàng Anh")
    # thanh tien do: xanh la + phan tram
    assert tuple(a.prog.cget("progress_color")) == W.PROGRESS_GREEN
    a._progress(3, 8, "Show.S01E03.mkv")
    assert a.prog_pct.cget("text") == "37% · 3/8" and abs(a.prog.get() - 0.375) < 1e-6
    a._progress(8, 8, "")
    assert a.prog_pct.cget("text") == "100% · 8/8"
    # Sang / Toi doi ngay: bang, khung log duoc to lai
    a.set_appearance("dark")
    a.root.update()
    assert W.is_dark()
    field_dark = W.ttk.Style(a.root).lookup("Treeview", "fieldbackground")
    need_dark = str(a.mux.table.tv.tag_configure("need", "background"))
    a.set_appearance("light")
    a.root.update()
    assert not W.is_dark()
    assert W.ttk.Style(a.root).lookup("Treeview", "fieldbackground") != field_dark
    assert str(a.mux.table.tv.tag_configure("need", "background")) != need_dark == W.ROW_BG["need"][1]
    assert W.appearance_mode("DARK") == "Dark" and W.appearance_mode("x") == "System" and W.appearance_mode(None) == "System"
    # nen mo Windows 11: may nay khong phai Windows 11 -> bo qua, khong loi
    assert a.backdrop is False and not W.backdrop_supported("mica", 19045) and W.backdrop_supported("mica", 22631)
    assert not W.backdrop_supported("none", 26100) and W.backdrop_supported("Acrylic", 26100)


# ---------------------------------------------------------------- 1.3.0: Titulky.com + hop thoai CAPTCHA
def test_titulky_source_in_fetch_tab_asks_captcha_through_the_gui_thread(movie_dir, ui, monkeypatch):
    """Thẻ ①: nguồn Titulky (tiếng Séc) — tìm, chọn, tải; gặp CAPTCHA thì LUỒNG PHỤ nhờ luồng giao diện mở hộp thoại."""
    import io
    import threading

    from core import subfetch
    from fakehttp import FakeHTTP, FakeResponse, Sleeper, zip_bytes
    from gui import fetch_view as FV
    from gui import widgets as W

    pil = pytest.importorskip("PIL.Image")
    buf = io.BytesIO()
    pil.new("RGB", (400, 100), (240, 240, 240)).save(buf, format="JPEG")
    jpeg = buf.getvalue()
    data = Path(__file__).resolve().parent / "data" / "titulky"
    stem = "Breaking.Bad.S01E05.720p.BRRip.x264.AC3"
    tiny_mkv(movie_dir / f"{stem}.mkv")
    ref = make_ref_times(400, seed=31)
    (movie_dir / "srt_goc").mkdir()
    (movie_dir / "srt_goc" / f"{stem}.srt").write_text(srt_text(ref), encoding="utf-8", newline="")
    sub = srt_text(transform_times(ref, 1.0, 1.5, seed=4)).encode("utf-8")
    http = FakeHTTP()
    http.add("GET", r"idown\.php\?id=tok", FakeResponse(200, content=zip_bytes({"Breaking.Bad.S01E05.srt": sub})))
    http.add("GET", r"captcha/captcha\.php", FakeResponse(200, content=jpeg))
    http.add("POST", r"titulky\.com/idown\.php$", FakeResponse(
        200, text='<html><body><a id="downlink" href="idown.php?id=tok">x</a><script>CountDown(2)</script></body></html>'))
    http.add("GET", r"titulky\.com/idown\.php$", FakeResponse(200, text=(data / "idown_captcha.html").read_text(encoding="utf-8")))
    http.add("GET", r"titulky\.com/nahled\.php", FakeResponse(200, text="<html></html>"))
    http.add("GET", r"titulky\.com/Breaking-Bad-S01E05-\d+\.htm",
             FakeResponse(200, text=(data / "detail_breaking_bad_s01e05.html").read_text(encoding="utf-8")))
    http.add("GET", r"titulky\.com/$", FakeResponse(200, text=(data / "search_breaking_bad_s01e05.html").read_text(encoding="utf-8")))
    monkeypatch.setattr(FV, "FetchSession", lambda folder, cfg, secrets, job, languages=None, sources=None:
                        subfetch.FetchSession(folder, cfg, {}, job, http, Sleeper(), languages=languages, sources=sources))
    shown: list = []
    main = threading.get_ident()

    class FakeCaptcha:  # hop thoai that se chan test; van dung anh that de kiem tra duong di
        def __init__(self, parent, image, note="", site="Titulky.com"):
            shown.append((threading.get_ident(), image, note, W.image_png(image)[:4]))

        def run(self):
            return "k9x2"

    monkeypatch.setattr(W, "CaptchaDialog", FakeCaptcha)
    a = ui(movie_dir)
    v = a.fetch
    a.goto("① Tải phụ đề")
    pump(a)
    for c, var in v.lang_vars.items():
        var.set(c == "cs")
    v.src_vars["subsource"].set(False)
    v.src_vars["opensubtitles"].set(False)
    v.scan()
    pump(a)
    assert v.session.captcha == a.ask_captcha and v.session.tt is not None
    v.gather()
    pump(a)
    it = v.items[0]
    assert it.lang == "cs" and len(it.candidates) == 4 and it.needs_choice
    rows = [v.cands.tv.item(i, "values") for i in v.cands.ids()]
    assert {r[1] for r in rows} == {"Titulky"} and rows[0][2] == "23.976 KHỚP"
    v.choose("c0")
    pump(a)
    v.execute()
    pump(a)
    dest = movie_dir / "srt_can_sync" / "cs" / f"{stem}.cs.srt"
    assert dest.read_bytes() == sub and "đã tải (Titulky)" in v.tree.tv.item("i0", "values")[3]
    assert len(shown) == 1 and shown[0][0] == main  # hop thoai mo o LUONG GIAO DIEN du loi goi tu luong phu
    assert shown[0][1] == jpeg and shown[0][3] == b"\x89PNG" and "Breaking.Bad.S01E05" in shown[0][2]
    assert http.calls_to(r"idown\.php$", "POST")[0].data["downkod"] == "k9x2"


def test_captcha_dialog_shows_jpeg_and_returns_typed_code(movie_dir, ui):
    import io

    from gui import widgets as W

    pil = pytest.importorskip("PIL.Image")
    buf = io.BytesIO()
    pil.new("RGB", (400, 100), (250, 250, 250)).save(buf, format="JPEG")
    a = ui(movie_dir)
    d = W.CaptchaDialog(a.root, buf.getvalue(), "Breaking.Bad.S01E05.720p")
    a.root.update()
    assert d.photo is not None and (d.photo.width(), d.photo.height()) == (400, 100) and d.picture is not None
    assert d.d.win.title() == "Mã CAPTCHA của Titulky.com"
    d.submit()  # o trong: khong dong
    assert d.d.win.winfo_exists()
    d.entry.insert(0, "  aB3x ")
    d.submit()
    assert d.d.result == "aB3x" and not d.d.win.winfo_exists()
    # anh khong doc duoc -> khong co hinh, co nut mo bang trinh xem anh; "Bo qua" -> None
    d2 = W.CaptchaDialog(a.root, b"khong phai anh", "")
    a.root.update()
    assert d2.photo is None and d2.picture is None
    d2.d.close(None)
    assert d2.d.result is None
