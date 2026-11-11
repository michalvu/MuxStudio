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
            p.write_text(srt_text(self.ref[stem]), encoding="utf-8", newline="")
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
    (sc / "random name from web.srt").write_text(srt_text(lib.sub(EP2, 0.8, seed=15)), encoding="utf-8", newline="")
    (sc / "Show.S01E05.1080p.WEB-DL.en.srt").write_text(srt_text(lib.sub(EP2, 0.0, seed=16)), encoding="utf-8", newline="")
    # TD-38 (1.2.0): kho noi bo srt_pool DA BO -> file trong do khong con la nguon (Dune en: khong nguon nao co)
    pl = lib.root / "srt_pool" / "en"
    pl.mkdir(parents=True)
    (pl / "Dune.Part.Two.2024.en.srt").write_text(srt_text(lib.sub(DUNE, -3.0, seed=17)), encoding="utf-8", newline="")

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
    # Dune en: file trong srt_pool KHONG duoc dung nua -> hoi mang (khong co) -> thieu
    de = item(s, DUNE, "en")
    assert de.chosen is None and de.candidates == [] and de.status == "không nguồn nào có"
    assert [c for c in http.calls_to(r"opensubtitles.*/subtitles$") if c.params.get("languages") == "en"
            and c.params.get("query", "").startswith("Dune")]
    assert not any("kho" in m.lower() for _, m in job.lines)
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
    assert (rep.ok, rep.used_existing, rep.missing) == (4, 1, 1)
    # buoc 1 KHONG sua thoi gian: chep nguyen byte
    assert (out / "vi" / f"{EP1}.vi.srt").read_bytes() == pack["Show.S01E01.vi.srt"].encode("utf-8")
    assert (out / "vi" / f"{EP2}.vi.srt").read_bytes() == pack["Show.S01E02.vi.srt"].encode("utf-8")
    assert (out / "en" / f"{EP1}.en.ass").exists()  # Q7: ASS giu la ASS
    assert not (out / "en" / f"{EP1}.en.srt").exists()
    assert (out / "en" / f"{EP2}.en.srt").exists() and (sc / "random name from web.srt").exists()  # CHEP, khong di chuyen
    assert (out / "vi" / f"{DUNE}.vi.srt").exists() and not (out / "en" / f"{DUNE}.en.srt").exists()
    assert len(http.calls_to(r"subtitles/1/download")) == 1  # goi tai MOT lan
    assert rep.os_remaining == 99
    assert rep.report_path and rep.report_path.name.startswith("fetch_")

    # hoan tac: moi file tao/chep -> Thung rac, file goc cua ban con nguyen
    runs = undo.list_runs(lib.root, "_subfetch_logs", "subfetch")
    trashed = []
    undo.execute_undo(undo.plan_undo(runs[0].path), trash_fn=lambda p, r=True: (trashed.append(p), os.remove(p)) and True)
    assert len(trashed) == 5
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


def test_no_sources_and_legacy_pool_folder_is_ignored(lib):
    """TĐ-38: kho nội bộ đã bỏ — không có SubSource key, OpenSubtitles tắt → không nguồn nào, không gọi mạng,
    file trong ``srt_pool`` không được dùng (và video trong đó vẫn không bị quét)."""
    pl = lib.root / "srt_pool" / "vi"
    pl.mkdir(parents=True)
    (pl / "Dune.Part.Two.2024.vi.srt").write_text(srt_text(lib.sub(DUNE, 1.0)), encoding="utf-8", newline="")
    http = FakeHTTP()
    s, job = session(lib, http, C.Config({"Languages": ["vi"], "UseOpenSubtitles": False}), secrets={})
    s.plan_all()
    assert sorted(i.video.stem for i in s.items) == [DUNE, EP1, EP2]
    assert all(it.chosen is None and not it.candidates for it in s.items)
    assert not http.calls
    assert any("SubSource API key" in m for _, m in job.lines)
    assert "pool" not in s.sources and not hasattr(s, "pool_index")
    rep = s.execute()
    assert rep.ok == 0 and rep.missing == 3
    assert (pl / "Dune.Part.Two.2024.vi.srt").exists()


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
    dest.write_text("x", encoding="utf-8", newline="")
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


# ---------------------------------------------------------------- TD-36: phim da co phu de -> khong tim nua

def _sub_track(tid, codec, lang, name="", forced=False, ietf=None):
    props = {"codec_id": codec, "language": lang, "track_name": name, "forced_track": forced}
    if ietf:
        props["language_ietf"] = ietf
    return {"id": tid, "type": "subtitles", "properties": props}


HAS_SUBS_JSON = {"tracks": [
    {"id": 0, "type": "video", "properties": {"default_duration": 41708333}},
    _sub_track(2, "S_TEXT/UTF8", "eng", "English SDH"),      # SDH van tinh la da co
    _sub_track(3, "S_TEXT/UTF8", "vie", "Forced", forced=True),  # Forced: khong tinh
    _sub_track(4, "S_HDMV/PGS", "cze"),                       # phu de dang anh: khong tinh
    _sub_track(5, "S_TEXT/ASS", "und"),                       # khong ghi ngon ngu: khong tinh
    _sub_track(6, "S_TEXT/ASS", "ger", ietf="de"),
]}


def test_embedded_sub_langs_counts_text_and_sdh_only():
    got = subfetch.embedded_sub_langs(HAS_SUBS_JSON)
    assert set(got) == {"en", "de"}
    assert got["en"] == "track 2 SRT 'English SDH'" and got["de"] == "track 6 ASS"
    assert subfetch.embedded_sub_langs(None) == {} and subfetch.embedded_sub_langs(FPS_JSON) == {}


def test_sidecar_sub_langs_reads_suffix_next_to_video(movie_dir):
    v = movie_dir / "Phim [YTS.MX] (2020).mkv"
    v.write_bytes(b"")
    for n in ("Phim [YTS.MX] (2020).vi.srt", "Phim [YTS.MX] (2020).CS.ass", "Phim [YTS.MX] (2020).en.forced.srt",
              "Phim [YTS.MX] (2020).srt", "Phim [YTS.MX] (2020).vi.txt", "Phim khác (2020).en.srt",
              "Phim [YTS.MX] (2020) Extra.en.srt"):
        (movie_dir / n).write_text("x", encoding="utf-8", newline="")
    got = subfetch.sidecar_sub_langs(v)
    assert {k: p.name for k, p in got.items()} == {"vi": "Phim [YTS.MX] (2020).vi.srt", "cs": "Phim [YTS.MX] (2020).CS.ass"}


def test_scan_skips_languages_the_video_already_has(lib):
    (lib.root / f"{DUNE}.vi.srt").write_text("x", encoding="utf-8", newline="")  # ket qua buoc ② nam canh phim
    cfg = C.Config({"Languages": ["vi", "en", "cs"]})
    job = tools.Job()
    probe_json = lambda p: HAS_SUBS_JSON if p.stem == DUNE else FPS_JSON  # noqa: E731
    s = subfetch.FetchSession(lib.root, cfg, {}, job, FakeHTTP(), Sleeper(),
                              tool_paths={"mkvmerge": None, "mkvextract": None}, video_probe=probe_json)
    s.scan()
    vi, en, cs = (item(s, DUNE, x) for x in ("vi", "en", "cs"))
    assert vi.already == lib.root / f"{DUNE}.vi.srt" and vi.already_note == "cạnh phim"
    assert en.already == lib.root / f"{DUNE}.mkv" and en.already_note.startswith("trong MKV: track 2 SRT")
    assert cs.already is None and not cs.already_note  # chi con thieu tieng Sec -> chi tim tieng Sec
    assert item(s, EP1, "vi").already is None and item(s, EP1, "en").already is None
    assert s.gather(vi) == [] and s.gather(en) == []  # khong hoi nguon nao
    assert item(s, DUNE, "cs").video.ref is not None  # con thieu ngon ngu -> van can moc
    rep = s.execute()
    assert rep.already == 2
    assert not (lib.root / "srt_can_sync" / "vi" / f"{DUNE}.vi.srt").exists()


def test_scan_all_languages_present_skips_ref_and_switch_off(lib):
    cfg = C.Config({"Languages": ["en", "de"]})
    s = subfetch.FetchSession(lib.root, cfg, {}, tools.Job(), FakeHTTP(), Sleeper(),
                              tool_paths={"mkvmerge": None, "mkvextract": None}, video_probe=lambda p: HAS_SUBS_JSON)
    s.scan()
    assert all(it.already is not None for it in s.items)
    assert all(v.ref is None for v in s.videos)  # du ca roi -> khoi doc/trich phu de goc
    cfg2 = C.Config({"Languages": ["en", "de"], "SkipIfVideoHasSub": False})
    s2 = subfetch.FetchSession(lib.root, cfg2, {}, tools.Job(), FakeHTTP(), Sleeper(),
                               tool_paths={"mkvmerge": None, "mkvextract": None}, video_probe=lambda p: HAS_SUBS_JSON)
    s2.scan()
    assert all(it.already is None for it in s2.items) and all(v.ref is not None for v in s2.videos)


# ---------------------------------------------------------------- TD-40: nguon Titulky.com (chi tieng Sec)

from pathlib import Path as _Path  # noqa: E402

TT_DATA = _Path(__file__).resolve().parent / "data" / "titulky"
BB = "Breaking.Bad.S01E05.720p.BRRip.x264.AC3"
DUNE21 = "Dune.2021.1080p.WEBRip.x264"


def _tt_page(name):
    return (TT_DATA / name).read_text(encoding="utf-8")


def _tt_preview(times, ad=True):
    """Trang xem trước như trang thật: dòng đầu là quảng cáo 'www.titulky.com' (không có trong phim)."""
    cues = ([(0.042, "www.titulky.com")] if ad else []) + [(t, "x") for t in times]
    body = "".join(f"{i}<br />{srt.format_stamp(t)} --&gt; {srt.format_stamp(t + 1)}<br />{txt}<br /><br />"
                   for i, (t, txt) in enumerate(cues, 1))
    return f'<html><body><div id="nahled_zdroj" style="x">\n{body}</div></body></html>'


def _tt_lib(root, stems):
    refs = {}
    for n, stem in enumerate(stems):
        (root / f"{stem}.mkv").write_bytes(b"\x1a\x45\xdf\xa3")
        refs[stem] = make_ref_times(600, seed=40 + n)
        p = root / "srt_goc" / f"{stem}.srt"
        p.parent.mkdir(exist_ok=True)
        p.write_text(srt_text(refs[stem]), encoding="utf-8", newline="")
    return refs


def _tt_session(root, http, cfg=None, secrets=None, **kw):
    job = tools.Job()
    s = subfetch.FetchSession(root, C.Config({"Languages": ["cs", "vi"], "UseOpenSubtitles": False, **(cfg or {})}),
                              secrets or {}, job, http, Sleeper(), tool_paths={"mkvmerge": None, "mkvextract": None},
                              video_probe=lambda p: FPS_JSON, **kw)
    return s, job


def test_titulky_series_candidates_probe_download_with_captcha(movie_dir):
    refs = _tt_lib(movie_dir, [BB])
    sub_cs = srt_text(transform_times(refs[BB], 1.0, 2.5, seed=3)).encode("utf-8")  # ban Sec lech +2,5 s
    http = FakeHTTP()
    http.add("GET", r"idown\.php\?id=tok", FakeResponse(200, content=zip_bytes({"Breaking.Bad.S01E05.cz.srt": sub_cs})))
    http.add("GET", r"captcha/captcha\.php", FakeResponse(200, content=b"\xff\xd8JPEG"))
    http.add("POST", r"titulky\.com/idown\.php$", FakeResponse(
        200, text='<html><body><a id="downlink" href="idown.php?id=tok">x</a><script>CountDown(3)</script></body></html>'))
    http.add("GET", r"titulky\.com/idown\.php$", FakeResponse(200, text=_tt_page("idown_captcha.html")))
    # ban xem truoc: ban 232758 khop phim (lech +2,5 s), cac ban khac la phim khac
    http.add("GET", r"titulky\.com/nahled\.php", lambda call: FakeResponse(200, text=_tt_preview(
        [t + 2.5 for t in refs[BB][:12]] if call.params["Titulky"] == 232758 else make_ref_times(12, seed=99))))
    http.add("GET", r"titulky\.com/Breaking-Bad-S01E05-\d+\.htm", FakeResponse(200, text=_tt_page("detail_breaking_bad_s01e05.html")))
    http.add("GET", r"titulky\.com/$", lambda call: FakeResponse(200, text=_tt_page("search_breaking_bad_s01e05.html")))
    s, job = _tt_session(movie_dir, http)
    assert s.tt is not None and s.sources["titulky"] and s.tt.has_account is False
    s.scan()
    assert any("Titulky: dùng kiểu khách" in m for _, m in job.lines)
    cs, vi = item(s, BB, "cs"), item(s, BB, "vi")
    cands = s.gather(cs)
    assert http.calls_to(r"titulky\.com/$")[0].params == {"Fulltext": "Breaking Bad S01E05"}  # tim dung kieu trang ghi tap
    assert len(cands) == 4 and {c.src for c in cands} == {"Titulky"}
    best = cands[0]  # FPS khop + da uoc tinh do lech (tu ban xem truoc) -> dung dau
    assert best.payload.id == 232758 and best.release == "Breaking.Bad.S01E05.Gray.Matter.720p.BRRip.x264.AC3"
    assert (best.tier, best.fps) == (1, "23.976") and best.fps_label == "23.976 KHỚP"
    assert best.off is not None and abs(best.off.offset + 2.5) <= 0.25 and best.offset_label.endswith("~")  # da bo qua dong quang cao dau file
    assert sum(1 for c in cands if c.tier == 1) == 3 and sum(1 for c in cands if c.off) == 1  # chi 3 ban dau duoc xem ky
    assert len(http.calls_to(r"-\d+\.htm$")) == 3 and len(http.calls_to(r"nahled\.php")) == 3
    # tieng Viet: KHONG hoi Titulky
    n = len(http.calls)
    assert s.gather(vi) == [] and len(http.calls) == n
    # tai: gap CAPTCHA -> hoi ban; cho du thoi gian; luu NGUYEN byte vao srt_can_sync\cs\
    asked = []
    s.captcha = lambda img, note: asked.append((img, note)) or "k9x2"
    s.choose(cs, best)
    rep = s.execute()
    dest = movie_dir / "srt_can_sync" / "cs" / f"{BB}.cs.srt"
    assert rep.ok == 1 and dest.read_bytes() == sub_cs and cs.result == dest and cs.result_src == "Titulky"
    assert asked == [(b"\xff\xd8JPEG", "Breaking.Bad.S01E05.Gray.Matter.720p.BRRip.x264.AC3")]
    assert http.calls_to(r"idown\.php$", "POST")[0].data["downkod"] == "k9x2"
    assert "Khớp thời gian" in cs.timing_note and any("Titulky yêu cầu chờ" in m for _, m in job.lines)
    runs = undo.list_runs(movie_dir, "_subfetch_logs", "subfetch")
    assert runs and runs[0].counts.get("Create") == 1


def test_titulky_movie_filters_language_year_title_and_source_order(movie_dir):
    _tt_lib(movie_dir, [DUNE21])
    http = FakeHTTP()
    http.add("GET", r"titulky\.com/Dune-\d+\.htm|titulky\.com/Duna-\d+\.htm", FakeResponse(200, text="<html>FPS:</span> 25 </html>"))
    http.add("GET", r"titulky\.com/$", FakeResponse(200, text=_tt_page("search_dune.html")))
    # OpenSubtitles gia co 1 ban tieng Sec -> phai dung SAU Titulky
    http.add("POST", r"opensubtitles\.com/api/v1/login", FakeResponse(200, {"token": "t", "base_url": "api.opensubtitles.com",
                                                                            "user": {"allowed_downloads": 20, "vip": False}}))
    http.add("GET", r"opensubtitles\.com/api/v1/infos/user", FakeResponse(200, {"data": {"remaining_downloads": 20, "allowed_downloads": 20}}))
    http.add("GET", r"opensubtitles\.com/api/v1/subtitles", FakeResponse(200, {"data": [
        {"id": "o1", "attributes": {"release": "Dune.2021.1080p.WEBRip", "fps": 23.976, "download_count": 3, "files": [{"file_id": 9}]}}]}))
    s, job = _tt_session(movie_dir, http, {"Languages": ["cs"], "UseOpenSubtitles": True, "TitulkyProbeTop": 1},
                         {"OsApiKey": "demo-os", "OsUsername": "u", "OsPassword": "p"})
    s.scan()
    it = item(s, DUNE21, "cs")
    out = s.online_candidates(it)
    assert [c.src for c in out][-1] == "OpenSub" and {c.src for c in out[:-1]} == {"Titulky"}  # thu tu nguon
    tt = [c for c in out if c.src == "Titulky"]
    rows = [c.payload for c in tt]
    assert rows and all(r.lang == "CZ" and r.year == 2021 and r.title == "Dune" for r in rows)  # bo SK, bo 1984 / 2000, bo "Duna"
    assert len({c.ident for c in tt}) == len(tt)  # hai ban trung ten release van phan biet duoc (them #id)
    assert any(re.search(r" #\d+$", c.release) for c in tt)
    assert len(http.calls_to(r"-\d+\.htm$")) == 1  # TitulkyProbeTop = 1
    probed = next(c for c in tt if c.fps)
    assert (probed.fps, probed.tier) == ("25", 3)  # khai bao 25 fps, video 23,976 -> LECH
    cands = s.gather(it)
    assert all(c.tier != 3 for c in cands)  # StrictFpsMatch loai ban lech framerate
    assert any("Titulky: 13 kết quả, giữ" in m for _, m in job.lines)


def test_titulky_only_for_czech_and_can_be_switched_off(movie_dir):
    _tt_lib(movie_dir, [DUNE21])
    s, _ = _tt_session(movie_dir, FakeHTTP(), {"Languages": ["vi", "en"]})
    assert s.tt is None  # khong co 'cs' trong ngon ngu can tai -> khong tao nguon
    s2, _ = _tt_session(movie_dir, FakeHTTP(), {"UseTitulky": False})
    assert s2.tt is None and s2.sources["titulky"] is False
    s3, _ = _tt_session(movie_dir, FakeHTTP(), sources={"titulky": False})
    assert s3.tt is None
    # co tai khoan: dang nhap luc ket noi, mat khau khong lot vao nhat ky
    http = FakeHTTP()
    http.add("POST", r"titulky\.com/$", FakeResponse(200, text="<html><a>Odhlásit</a></html>"))
    http.add("GET", r"titulky\.com/$", FakeResponse(200, text="<html><table class='soupis'></table></html>"))
    s4, job = _tt_session(movie_dir, http, secrets={"TitulkyUsername": "anh", "TitulkyPassword": "demo-pass"})
    s4.scan()
    assert s4.tt.logged_in is True and any("Titulky: đã đăng nhập" in m for _, m in job.lines)
    assert http.calls_to(r"titulky\.com/$", "POST")[0].data["Login"] == "anh"
    assert not any("demo-pass" in m for _, m in job.lines)
    j2 = tools.Job()
    s4.set_job(j2)
    assert s4.tt.job is j2
