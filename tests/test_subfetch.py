"""Bước ① đầu–cuối với API giả (§6.11, §8)."""
from __future__ import annotations

import os
import re

import pytest

from core import config as C
from core import srt, subfetch, tools, undo
from fakehttp import FakeHTTP, FakeResponse, Sleeper, zip_bytes
from synth import make_ref_times, srt_text, transform_times

FPS_JSON = {"tracks": [{"id": 0, "type": "video", "properties": {"default_duration": 41708333}}]}
EP1 = "Show.S01E01.1080p.WEB-DL"
EP2 = "Show.S01E02.1080p.WEB-DL"
DUNE = "Dune.Part.Two.2024.2160p"


def ass_text(times):
    lines = ["[Script Info]", "ScriptType: v4.00+", "", "[Events]",
             "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text"]
    for t in times:
        h, rem = divmod(t, 3600)
        m, s = divmod(rem, 60)
        lines.append(f"Dialogue: 0,{int(h)}:{int(m):02d}:{s:05.2f},{int(h)}:{int(m):02d}:{s + 1:05.2f},Default,,0,0,0,,x")
    return "\n".join(lines) + "\n"


class Lib:
    """Thư viện phim giả: 2 tập phim bộ + 1 phim lẻ, phụ đề gốc trong srt_goc."""

    def __init__(self, root):
        self.root = root
        self.ref = {EP1: make_ref_times(600, seed=1), EP2: make_ref_times(600, seed=2), DUNE: make_ref_times(800, seed=3)}
        for stem in self.ref:
            (root / f"{stem}.mkv").write_bytes(b"\x1a\x45\xdf\xa3")
            p = root / "srt_goc" / f"{stem}.srt"
            p.parent.mkdir(exist_ok=True)
            p.write_text(srt_text(self.ref[stem]), encoding="utf-8")
        # video trong Output / audio_nguon / srt_pool: KHONG duoc tai (Q6)
        for d in ("Output/Show (2019) [tmdbid-1]/Season 01", "audio_nguon", "srt_pool"):
            (root / d).mkdir(parents=True, exist_ok=True)
            (root / d / "Show - S01E01.mkv").write_bytes(b"")

    def sub(self, stem, offset, scale=1.0, seed=9, keep=0.9):
        return transform_times(self.ref[stem], scale, offset, keep=keep, seed=seed)


def ss_routes(http, lib, pack_has_ep2=True, extra_subs=()):
    ep1_vi = lib.sub(EP1, 2.5, seed=11)
    ep2_vi = lib.sub(EP2, -1.25, seed=12)
    pack = {"Show.S01E01.vi.srt": srt_text(ep1_vi), "Show.S01E02.vi.forced.srt": srt_text(ep2_vi[:30])}
    if pack_has_ep2:
        pack["Show.S01E02.vi.srt"] = srt_text(ep2_vi)
    preview = srt_text(sorted(ep1_vi)[:5])
    subs = [
        # ban nguoi dung tu dang: de TRONG hearingImpaired/foreignParts/framerate -> van phai duoc nhan
        {"subtitleId": 1, "language": "vietnamese", "releaseInfo": ["Show.S01.1080p.WEB-DL"], "framerate": "",
         "hearingImpaired": None, "foreignParts": None, "files": 2, "downloads": 50, "preview": preview},
        {"subtitleId": 2, "language": "english", "releaseInfo": ["Show.S01E01.1080p.WEB-DL-GRP"], "framerate": "23.976",
         "files": 1, "downloads": 900},
        {"subtitleId": 3, "language": "english", "releaseInfo": ["Show.S01E01.PAL.DVDRip"], "framerate": "25", "files": 1},
        {"subtitleId": 4, "language": "english", "releaseInfo": ["Show.S01E01.SDH"], "framerate": "23.976",
         "hearingImpaired": True, "files": 1},
        *extra_subs,
    ]

    def search(call):
        q = call.params.get("q", "")
        if q == "Show":
            return FakeResponse(200, {"data": [{"movieId": 100, "title": "Show", "season": 1, "subtitleCount": 4}]})
        if q.startswith("Dune"):
            return FakeResponse(200, {"data": [{"movieId": 200, "title": "Dune: Part Two", "releaseYear": 2024}]})
        return FakeResponse(200, {"data": []})

    def lst(call):
        mid = call.params["movieId"]
        return FakeResponse(200, {"data": subs if mid == "100" else [], "pagination": {"pages": 1}})

    http.add("GET", r"subsource\.net/api/v1/movies/search", search)
    http.add("GET", r"subsource\.net/api/v1/subtitles$", lst)
    http.add("GET", r"subsource\.net/api/v1/subtitles/1/download", FakeResponse(200, content=zip_bytes(pack)))
    http.add("GET", r"subsource\.net/api/v1/subtitles/2/download",
             FakeResponse(200, content=zip_bytes({"Show.S01E01.en.ass": ass_text(lib.sub(EP1, 0.4, seed=13))})))
    return pack


def os_routes(http, lib, remaining=100, dune_vi=None):
    http.add("POST", r"opensubtitles\.com/api/v1/login", FakeResponse(200, {
        "token": "tok", "base_url": "api.opensubtitles.com", "user": {"allowed_downloads": 100, "vip": False, "level": "Sub leecher"}}))
    http.add("GET", r"opensubtitles\.com/api/v1/infos/user",
             FakeResponse(200, {"data": {"remaining_downloads": remaining, "allowed_downloads": 100}}))
    items = dune_vi if dune_vi is not None else [
        {"id": "m", "attributes": {"release": "Dune.Part.Two.2024.2160p.WEB", "fps": 23.976, "download_count": 9000,
                                   "machine_translated": True, "files": [{"file_id": 501}]}},
        {"id": "n", "attributes": {"release": "Dune.Part.Two.2024.1080p", "fps": 23.976, "download_count": 10,
                                   "files": [{"file_id": 502}]}},
    ]

    def search(call):
        if call.params.get("languages") == "vi" and call.params.get("query", "").startswith("Dune"):
            return FakeResponse(200, {"data": items})
        return FakeResponse(200, {"data": []})

    http.add("GET", r"opensubtitles\.com/api/v1/subtitles", search)
    http.add("POST", r"opensubtitles\.com/api/v1/download", lambda call: FakeResponse(
        200, {"link": f"https://dl.opensubtitles.com/{call.json['file_id']}", "remaining": remaining - 1, "reset_time": "5 giờ"}))
    http.add("GET", r"dl\.opensubtitles\.com/50[12]",
             FakeResponse(200, content=srt_text(lib.sub(DUNE, 7.0, seed=14)).encode("utf-8")))


SECRETS = {"SubSourceApiKey": "demo-ss", "OsApiKey": "demo-os", "OsUsername": "u", "OsPassword": "p"}


def session(lib, http, cfg=None, secrets=None, **kw):
    cfg = cfg or C.Config({"Languages": ["vi", "en"], "OsExcludeMachine": False})
    job = tools.Job()
    s = subfetch.FetchSession(lib.root, cfg, secrets if secrets is not None else SECRETS, job, http, Sleeper(),
                              tool_paths={"mkvmerge": None, "mkvextract": None}, video_probe=lambda p: FPS_JSON, **kw)
    return s, job


def item(s, stem, lang):
    return next(i for i in s.items if i.video.stem == stem and i.lang == lang)


@pytest.fixture
def lib(movie_dir):
    return Lib(movie_dir)


def test_full_run(lib):
    http = FakeHTTP()
    pack = ss_routes(http, lib)
    os_routes(http, lib)
    # file ban tu bo vao srt_can_sync: dung tap 2 (do duoc) + ten giong nhung TAP KHAC
    sc = lib.root / "srt_can_sync" / "en"
    sc.mkdir(parents=True)
    (sc / "random name from web.srt").write_text(srt_text(lib.sub(EP2, 0.8, seed=15)), encoding="utf-8")
    (sc / "Show.S01E05.1080p.WEB-DL.en.srt").write_text(srt_text(lib.sub(EP2, 0.0, seed=16)), encoding="utf-8")
    # kho noi bo co ban khop cho Dune en -> khong hoi mang cho ngon ngu do
    pl = lib.root / "srt_pool" / "en"
    pl.mkdir(parents=True)
    (pl / "Dune.Part.Two.2024.en.srt").write_text(srt_text(lib.sub(DUNE, -3.0, seed=17)), encoding="utf-8")

    s, job = session(lib, http)
    asked = []

    def ask(it):
        asked.append((it.video.stem, it.lang, [c.release for c in it.candidates]))
        return it.candidates[0]

    s.scan()
    assert sorted({i.video.stem for i in s.items}) == [DUNE, EP1, EP2]  # Output/audio_nguon/srt_pool bi bo qua
    ex = item(s, EP2, "en").existing
    assert ex and ex.file.name == "random name from web.srt" and ex.votes >= 20
    s.plan_all(ask)

    # Dune vi: 2 ban OpenSubtitles -> phai hoi; ban thuong xep TRUOC ban dich may (Q2)
    assert asked == [(DUNE, "vi", ["Dune.Part.Two.2024.1080p", "Dune.Part.Two.2024.2160p.WEB"])]
    dv = item(s, DUNE, "vi")
    assert [c.machine for c in dv.candidates] == [False, True] and "dịch máy" in dv.candidates[1].info
    # Dune en: kho noi bo khop -> khong tim tren mang
    de = item(s, DUNE, "en")
    assert de.chosen.src == "Kho" and de.chosen.tier == 1
    assert not [c for c in http.calls_to(r"opensubtitles.*/subtitles$") if c.params.get("languages") == "en"
                and c.params.get("query", "").startswith("Dune")]
    # Tap 1 vi: goi season, uoc tinh tu preview (dau ~), tu lay vi chi 1 ung vien
    v1 = item(s, EP1, "vi")
    assert v1.chosen.is_pack and v1.chosen.offset_label.endswith("~") and abs(v1.chosen.off.offset - 2.5) <= 0.25
    # Tap 2 vi: goi da tai -> DO THANG file cua tap 2 trong goi; dung lai lua chon (cung season + ngon ngu)
    v2 = item(s, EP2, "vi")
    assert v2.chosen.ident == v1.chosen.ident and v2.choice_reason == "dùng lại lựa chọn trước"
    assert v2.chosen.off.total == -1 and abs(v2.chosen.off.offset + 1.25) < 0.1
    # Tap 1 en: ban PAL bi loai (StrictFpsMatch), SDH bi loai; con 1 -> tu lay
    e1 = item(s, EP1, "en")
    assert [c.release for c in e1.candidates] == ["Show.S01E01.1080p.WEB-DL-GRP"]

    rep = s.execute()
    out = lib.root / "srt_can_sync"
    assert (rep.ok, rep.used_existing, rep.missing) == (5, 1, 0)
    # buoc 1 KHONG sua thoi gian: chep nguyen byte
    assert (out / "vi" / f"{EP1}.vi.srt").read_bytes() == pack["Show.S01E01.vi.srt"].encode("utf-8")
    assert (out / "vi" / f"{EP2}.vi.srt").read_bytes() == pack["Show.S01E02.vi.srt"].encode("utf-8")
    assert (out / "en" / f"{EP1}.en.ass").exists()  # Q7: ASS giu la ASS
    assert not (out / "en" / f"{EP1}.en.srt").exists()
    assert (out / "en" / f"{EP2}.en.srt").exists() and (sc / "random name from web.srt").exists()  # CHEP, khong di chuyen
    assert (out / "vi" / f"{DUNE}.vi.srt").exists() and (out / "en" / f"{DUNE}.en.srt").exists()
    assert len(http.calls_to(r"subtitles/1/download")) == 1  # goi tai MOT lan
    assert rep.os_remaining == 99
    assert rep.report_path and rep.report_path.name.startswith("fetch_")

    # hoan tac: moi file tao/chep -> Thung rac, file goc cua ban con nguyen
    runs = undo.list_runs(lib.root, "_subfetch_logs", "subfetch")
    trashed = []
    undo.execute_undo(undo.plan_undo(runs[0].path), trash_fn=lambda p, r=True: (trashed.append(p), os.remove(p)) and True)
    assert len(trashed) == 6
    assert (sc / "random name from web.srt").exists()
    assert not (out / "vi" / f"{EP1}.vi.srt").exists()


def test_episode_specific_choice_not_reused_and_failed_choice_tries_next(lib):
    """Ghi nhớ theo season + ngôn ngữ, KHÔNG dùng lại bản riêng của tập khác; bản chọn không có
    tập này → tự thử ứng viên kế tiếp và xoá khỏi bộ nhớ."""
    http = FakeHTTP()
    ep2_en = {"subtitleId": 7, "language": "english", "releaseInfo": ["Show.S01E02.1080p.WEB-DL-GRP"],
              "framerate": "23.976", "files": 1}
    ss_routes(http, lib, pack_has_ep2=False, extra_subs=[ep2_en])
    http.add("GET", r"subtitles/7/download", FakeResponse(200, content=zip_bytes({"Show.S01E02.en.srt": srt_text(lib.sub(EP2, 0.3))})))
    os_routes(http, lib)
    alt_vi = {"subtitleId": 8, "language": "vietnamese", "releaseInfo": ["Show.S01E02.vi.alt"], "framerate": "", "files": 1}
    http.add("GET", r"subtitles/8/download", FakeResponse(200, content=zip_bytes({"Show.S01E02.vi.srt": srt_text(lib.sub(EP2, 0.1))})))
    # them ban vi rieng tap 2 vao danh sach
    s, job = session(lib, http, C.Config({"Languages": ["vi", "en"]}))
    s.scan()
    s.ss._list_cache[100] = s.ss.list_all(100) + [alt_vi]
    s.plan_all(lambda it: it.candidates[0])
    e2 = item(s, EP2, "en")
    assert e2.chosen.release == "Show.S01E02.1080p.WEB-DL-GRP"  # khong dung lai ban rieng cua tap 1
    v2 = item(s, EP2, "vi")
    assert v2.chosen.release == "Show.S01.1080p.WEB-DL"  # nho tu tap 1 (goi season)
    s.execute()
    assert (lib.root / "srt_can_sync" / "vi" / f"{EP2}.vi.srt").read_text(encoding="utf-8").count("-->") > 30
    assert any("Thử bản khác" in m and "Show.S01E02.vi.alt" in m for _, m in job.lines)
    assert s.memory.get("100|vi") is None  # ban nho khong dung duoc -> xoa


def test_no_subsource_entry_and_os_off_still_uses_pool(lib):
    """TĐ-06 + TĐ-07: không có SubSource key, OpenSubtitles tắt → kho nội bộ vẫn chạy."""
    pl = lib.root / "srt_pool" / "vi"
    pl.mkdir(parents=True)
    (pl / "Dune.Part.Two.2024.vi.srt").write_text(srt_text(lib.sub(DUNE, 1.0)), encoding="utf-8")
    http = FakeHTTP()
    s, job = session(lib, http, C.Config({"Languages": ["vi"], "UseOpenSubtitles": False}), secrets={})
    s.plan_all()
    assert item(s, DUNE, "vi").chosen.src == "Kho"
    assert not http.calls
    assert any("SubSource API key" in m for _, m in job.lines)
    # cac tap Show: file kho cua phim KHAC chi khop yeu -> khong bao gio tu lay (TD-28)
    for stem in (EP1, EP2):
        it = item(s, stem, "vi")
        assert it.chosen is None and all(c.fps == "đo: nghi ngờ" for c in it.candidates)
    rep = s.execute()
    assert rep.ok == 1 and rep.missing + rep.skipped == 2


def test_keep_paid_downloads_off_trashes_weak_os_file_and_tries_next(lib, monkeypatch):
    http = FakeHTTP()
    os_routes(http, lib)
    weak = srt_text(make_ref_times(300, start=4300.0, seed=999))  # gan nhu khong khop
    http.add("GET", r"dl\.opensubtitles\.com/501", FakeResponse(200, content=weak.encode("utf-8")))
    trashed = []
    monkeypatch.setattr(undo, "trash", lambda p, r=True: trashed.append(os.path.basename(p)) or True)
    cfg = C.Config({"Languages": ["vi"], "OsExcludeMachine": False, "KeepPaidDownloads": False})
    s, job = session(lib, http, cfg, secrets={k: v for k, v in SECRETS.items() if k != "SubSourceApiKey"})
    s.plan_all(lambda it: next(c for c in it.candidates if c.machine))  # chon ban 501 (yeu)
    rep = s.execute()
    assert trashed == ["os_501.srt"]
    assert item(s, DUNE, "vi").result_src == "OpenSub" and item(s, DUNE, "vi").chosen.payload["id"] == "n"
    assert rep.ok == 1


def test_non_interactive_and_strict_unknown_fps(lib):
    http = FakeHTTP()
    ss_routes(http, lib)
    os_routes(http, lib)
    cfg = C.Config({"Languages": ["vi"], "Interactive": False, "AllowUnknownFps": False, "OsExcludeMachine": False})
    s, _ = session(lib, http, cfg)
    s.plan_all()
    assert item(s, EP1, "vi").candidates == []  # goi vi khong khai bao fps -> loai khi AllowUnknownFps tat
    assert item(s, DUNE, "vi").chosen.release == "Dune.Part.Two.2024.1080p"  # tu dong: ban dung dau


def test_os_quota_low_stops_downloads(lib):
    http = FakeHTTP()
    os_routes(http, lib, remaining=5)
    s, job = session(lib, http, C.Config({"Languages": ["vi"], "OsExcludeMachine": False}),
                     secrets={k: v for k, v in SECRETS.items() if k != "SubSourceApiKey"})
    s.plan_all(lambda it: it.candidates[0])
    rep = s.execute()
    assert rep.ok == 0 and not http.calls_to(r"/download", "POST")
    assert any("DỪNG dùng nguồn này" in m for _, m in job.lines)


def test_skip_language_button(lib):
    http = FakeHTTP()
    os_routes(http, lib)
    s, _ = session(lib, http, C.Config({"Languages": ["vi"], "OsExcludeMachine": False}),
                   secrets={k: v for k, v in SECRETS.items() if k != "SubSourceApiKey"})
    s.plan_all(lambda it: None)  # bam "Bo qua ngon ngu nay"
    rep = s.execute()
    assert item(s, DUNE, "vi").skip and rep.skipped == 1 and rep.ok == 0


def test_skip_if_exists_and_srt_goc_before_mkv(lib):
    dest = lib.root / "srt_can_sync" / "vi" / f"{DUNE}.vi.ass"
    dest.parent.mkdir(parents=True)
    dest.write_text("x", encoding="utf-8")
    s, _ = session(lib, FakeHTTP(), C.Config({"Languages": ["vi"]}), secrets={})
    s.scan()
    it = item(s, DUNE, "vi")
    assert it.already == dest  # da co (ke ca .ass) -> bo qua
    # TD-08: moc goc lay tu srt_goc du khong co mkvmerge/mkvextract
    assert it.video.ref and "dùng lại srt_goc" in it.video.ref.source


def test_ref_found_by_episode_in_srt_goc(lib):
    g = lib.root / "srt_goc"
    os.rename(g / f"{EP1}.srt", g / "[Group] Show - S01E01 khac ten.srt")
    s, _ = session(lib, FakeHTTP(), C.Config({"Languages": ["vi"]}), secrets={})
    s.scan()
    assert "tìm theo tập/tên" in item(s, EP1, "vi").video.ref.source


def test_candidate_sort_order():
    O = subfetch.Offset
    mk = lambda tier, off=None, score=0.0, machine=False, rel="": subfetch.Candidate("X", tier, "", score, rel, "", None, off, machine)
    cands = [mk(2, rel="t2"), mk(1, O(5.0, 30, -1), rel="t1-lech5"), mk(1, rel="t1-chuado", score=0.9),
             mk(1, O(-0.5, 30, -1), rel="t1-lech0.5"), mk(1, O(0.1, 30, -1), machine=True, rel="t1-may"), mk(3, rel="t3")]
    assert [c.release for c in subfetch.sort_candidates(cands)] == ["t1-lech0.5", "t1-lech5", "t1-may", "t1-chuado", "t2", "t3"]


@pytest.mark.parametrize("declared,video,tier", [("23.976", 23.976, 1), ("24", 23.976, 1), ("25", 23.976, 3),
                                                 ("", 23.976, 2), (None, 23.976, 2), ("23,976", 23.976, 1), ("25", None, 2)])
def test_fps_tier(declared, video, tier):
    assert subfetch.fps_tier(declared, video) == tier
