"""Bước ③ (MuxTool): quy tắc audio (§6.7), phụ đề (§6.8), dựng lệnh mkvmerge (§6.2, SPEC 4.4), thẻ codec,
thực thi + mã thoát + hoàn tác. JSON ``mkvmerge -J`` giả lập — không cần mkvmerge thật (test thật ở test_e2e_mux.py).
"""
from __future__ import annotations

import os
from pathlib import Path

import pytest

from core import config as C
from core import mux, probe, tools, undo
from fakehttp import FakeHTTP, Sleeper

MOVIE = "Phim.Thu.2020.1080p.WEB-DL"


# ---------------------------------------------------------------- JSON gia lap
def V(tid=0, dim="1920x1080", codec_id="V_MPEGH/ISO/HEVC", codec="HEVC/H.265/MPEG-H", lang="und", fps=23.976):
    return {"id": tid, "type": "video", "codec": codec,
            "properties": {"codec_id": codec_id, "pixel_dimensions": dim, "language": lang,
                           "default_duration": int(round(1e9 / fps))}}


def A(tid, lang="eng", codec="E-AC-3", codec_id="A_EAC3", ch=6, name="", bps=None, ietf=None, **kw):
    p = {"codec_id": codec_id, "language": lang, "audio_channels": ch, "audio_sampling_frequency": 48000}
    if ietf:
        p["language_ietf"] = ietf
    if name:
        p["track_name"] = name
    if bps:
        p["tag_bps"] = str(bps)
    p.update(kw)
    return {"id": tid, "type": "audio", "codec": codec, "properties": p}


def S(tid, lang="eng", codec_id="S_TEXT/UTF8", name="", forced=False, sdh=False, **kw):
    p = {"codec_id": codec_id, "language": lang, "forced_track": forced}
    if name:
        p["track_name"] = name
    if sdh:
        p["flag_hearing_impaired"] = True
    p.update(kw)
    return {"id": tid, "type": "subtitles", "codec": "SubRip/SRT", "properties": p}


def J(*tracks, dur=5400.0):
    return {"container": {"properties": {"duration": int(dur * 1e9)}}, "tracks": list(tracks)}


class Lib:
    def __init__(self, root: Path, monkeypatch, cfg=None, secrets=None, http=None, ver=82):
        self.root = root
        monkeypatch.setattr(tools, "mkvmerge_version", lambda path, job=None: tools.ToolVersion(True, ver, f"mkvmerge v{ver}.0"))
        self.cfg = C.Config({"UseDonorAudio": False, **(cfg or {})})
        self.s = mux.MuxSession(root, self.cfg, secrets or {}, tools.Job(), http or FakeHTTP(), Sleeper(),
                                tool_paths={"mkvmerge": "mkvmerge-gia", "mkvextract": None, "ffmpeg": None, "ffprobe": None})
        self.s._ui_lang = []

    def add(self, stem: str, j: dict, rel: str = "") -> Path:
        d = self.root / rel if rel else self.root
        d.mkdir(parents=True, exist_ok=True)
        p = d / f"{stem}.mkv"
        p.write_bytes(b"\x1a\x45\xdf\xa3")
        self.s.json_cache[os.path.normcase(os.path.abspath(p))] = j
        return p

    def plan(self, p: Path, db="none") -> mux.MuxPlan:
        self.s.db = db
        return self.s.plan_file(p)


@pytest.fixture
def lib(movie_dir, monkeypatch):
    return Lib(movie_dir, monkeypatch)


def audio_kept(plan):
    return [a.id for a in plan.audio if a.keep]


# ==========================================================================
#  probe: tên codec, kênh, bitrate
# ==========================================================================


def test_audio_codec_name_reads_track_level_codec():
    """TĐ-33: mkvmerge ghi ``codec`` ở cấp track; không có thì dịch codec_id."""
    assert probe.audio_codec_name(A(1)) == "E-AC-3"
    assert probe.audio_codec_name(A(1, codec="AC-3", codec_id="A_AC3")) == "Dolby Digital"
    assert probe.audio_codec_name(A(1, codec="DTS-HD Master Audio", codec_id="A_DTS")) == "DTS-HD MA"
    assert probe.audio_codec_name(A(1, codec="TrueHD Atmos", codec_id="A_TRUEHD")) == "TrueHD"
    no_codec = {"id": 1, "type": "audio", "properties": {"codec_id": "A_EAC3"}}
    assert probe.audio_codec_name(no_codec) == "E-AC-3"
    assert probe.audio_codec_name({"properties": {"codec_id": "A_PCM/INT/LIT"}}) == "PCM"


def test_channel_name_and_bitrate_priority():
    assert [probe.channel_name(n) for n in (1, 2, 3, 6, 7, 8, 4, None)] == ["1.0", "2.0", "2.1", "5.1", "6.1", "7.1", "4.0", ""]
    t = A(1, bps=640000)
    assert probe.audio_bitrate(t, {1: 999}) == (640000.0, "tag")
    assert probe.audio_bitrate(A(1), {1: 384000.0}) == (384000.0, "ffprobe")
    assert probe.audio_bitrate(A(1, codec="TrueHD", ch=8)) == (pytest.approx(4000 * 1000 * 8 / 6), "ước lượng")
    assert probe.ffprobe_bitrates([{"index": 1, "bit_rate": "448000"}, {"index": 2, "tags": {"BPS-eng": "96000"}}]) == \
        {1: 448000.0, 2: 96000.0}


def test_video_resolution_hdr_names():
    assert probe.resolution_name("3840x2160") == "2160p" and probe.resolution_name("1920x800") == "720p"
    assert probe.video_codec_name("V_MPEGH/ISO/HEVC") == "HEVC" and probe.video_codec_name("V_AV1") == "AV1"
    assert probe.dynamic_range({"color_transfer": "smpte2084", "side_data_list": [{"side_data_type": "DOVI configuration record"}]}, "x") == "DV HDR"
    assert probe.dynamic_range({"color_transfer": "smpte2084", "side_data_list": [{"side_data_type": "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)"}]}, "x") == "HDR10+"
    assert probe.dynamic_range(None, "Movie.2160p.DV.HDR10.HEVC") == "DV HDR"
    assert probe.dynamic_range(None, "Movie.2160p.HLG") == "HLG"
    assert probe.dynamic_range(None, "Movie.1080p") == ""


# ==========================================================================
#  AUDIO (§6.7)
# ==========================================================================


def test_commentary_and_description_removed(lib):
    p = lib.add(MOVIE, J(V(), A(1), A(2, codec="AC-3", codec_id="A_AC3", ch=2, name="Director's Commentary"),
                         A(3, codec="AAC", codec_id="A_AAC", ch=2, name="Audio Description"), A(4, "cze")))
    plan = lib.plan(p)
    assert audio_kept(plan) == [1, 4]
    assert {a.id: a.reason for a in plan.audio if not a.keep} == {2: "Commentary", 3: "Audio Description"}
    assert plan.audio[0].label == "English E-AC-3 5.1"


def test_all_commentary_keeps_everything(lib):
    p = lib.add(MOVIE, J(V(), A(1, name="Commentary 1"), A(2, name="Commentary 2", lang="cze")))
    assert audio_kept(lib.plan(p)) == [1, 2]


def test_und_single_becomes_en_and_two_und_skips(lib):
    p = lib.add(MOVIE, J(V(), A(1, lang="und")))
    plan = lib.plan(p)
    assert plan.lang_override == {1: "en"} and plan.status == "ok"
    args = lib.s.build_args(plan)
    assert ["--language", "1:en"] == args[args.index("--language"):args.index("--language") + 2]
    p2 = lib.add("Phim.Khac.2021", J(V(), A(1, lang="und"), A(2, lang="eng")))
    plan2 = lib.plan(p2)
    assert plan2.status == "skip" and "und" in plan2.reasons[0]


def test_two_english_audio_needs_choice_q3(lib):
    """Q3/TĐ-23: ≥ 2 audio tiếng Anh → 'Cần chọn' cho tới khi bạn tick, hoặc 'Giữ cả hai, Default = bitrate cao nhất'."""
    p = lib.add(MOVIE, J(V(), A(1, codec="AC-3", codec_id="A_AC3", bps=448000), A(2, codec="TrueHD", codec_id="A_TRUEHD", ch=8, bps=3500000)))
    plan = lib.plan(p)
    assert plan.status == "need_choice" and "2 audio tiếng Anh" in plan.reasons[0]
    lib.s.keep_all_audio(plan)
    plan = lib.plan(p)
    assert plan.status == "ok" and plan.default_audio == 2 and audio_kept(plan) == [1, 2]
    lib.s.audio_inputs.clear()
    lib.s.set_audio_choice(plan, [2], apply_to_rest=False)
    plan = lib.plan(p)
    assert plan.status == "ok" and audio_kept(plan) == [1] and plan.default_audio == 1
    lib2 = Lib(lib.root, pytest.MonkeyPatch(), cfg={"SkipOnMultipleEnAudio": False})
    lib2.s.json_cache = lib.s.json_cache
    plan = lib2.plan(p)
    assert plan.status == "ok" and plan.default_audio == 2  # tu lay bitrate cao nhat


def test_default_audio_english_else_first(lib):
    p = lib.add(MOVIE, J(V(), A(1, "jpn"), A(2, "eng", ch=2), A(3, "cze")))
    assert lib.plan(p).default_audio == 2
    p2 = lib.add("Anime.2020", J(V(), A(1, "jpn"), A(2, "cze")))
    assert lib.plan(p2).default_audio == 1


def test_audio_rule_remembers_what_is_kept(lib):
    """'Nhớ cái GIỮ': giữ English 5.1 (bỏ English 2.0) → file sau: ngôn ngữ lạ bị loại, English 2.0 bị loại."""
    p1 = lib.add("Show.S01E01", J(V(), A(1, ch=6), A(2, ch=2), A(3, "cze")))
    plan1 = lib.plan(p1)
    rule = lib.s.set_audio_choice(plan1, [2, 3], apply_to_rest=True)
    assert rule.keep_langs == ["en"] and rule.keep_sig == {"en": ["5.1"]}
    assert rule.describe() == "chỉ giữ English (5.1)"
    p2 = lib.add("Show.S01E02", J(V(), A(1, ch=6), A(2, ch=2), A(3, "cze"), A(4, "ger")))
    plan2 = lib.plan(p2)
    assert audio_kept(plan2) == [1] and plan2.rule_applied == "chỉ giữ English (5.1)"
    # quy tac lam mat het audio -> bo qua quy tac cho file do
    p3 = lib.add("Show.S01E03", J(V(), A(1, "jpn"), A(2, "cze")))
    assert audio_kept(lib.plan(p3)) == [1, 2]


def test_remember_audio_never(movie_dir, monkeypatch):
    lib = Lib(movie_dir, monkeypatch, cfg={"RememberAudioChoice": "never"})
    p = lib.add("Show.S01E01", J(V(), A(1), A(2, "cze")))
    assert lib.s.set_audio_choice(lib.plan(p), [2], apply_to_rest=True) is None and lib.s.audio_rule is None


def test_needs_audio_table_flag(lib):
    assert lib.plan(lib.add(MOVIE, J(V(), A(1), A(2, "cze")))).needs_audio_table
    assert not lib.plan(lib.add("Phim.Mot.Audio.2020", J(V(), A(1)))).needs_audio_table


# ==========================================================================
#  PHỤ ĐỀ (§6.8)
# ==========================================================================


def write_sub(p: Path, enc="utf-8", text="Xin chào"):
    p.write_bytes(f"1\r\n00:00:01,000 --> 00:00:02,000\r\n{text}\r\n\r\n".encode(enc))
    return p


def test_internal_sub_rules(lib):
    p = lib.add(MOVIE, J(V(), A(1), S(2, "eng"), S(3, "eng", name="English SDH"), S(4, "eng", name="Forced", forced=True),
                         S(5, "cze", codec_id="S_HDMV/PGS"), S(6, "vie", name="Commentary"), S(7, "fre"), S(8, "cze")))
    plan = lib.plan(p)
    got = {s.id: (s.keep, s.reason) for s in plan.int_subs}
    assert got == {2: (True, "giữ"), 3: (False, "SDH (đã có English thường)"), 4: (False, "Forced"),
                   5: (False, "phụ đề dạng ảnh"), 6: (False, "Commentary"), 7: (False, "ngoài danh sách ngôn ngữ"),
                   8: (True, "giữ")}
    p2 = lib.add("Phim.SDH.2020", J(V(), A(1), S(2, "eng", name="SDH")))
    s = lib.plan(p2).int_subs[0]
    assert s.keep and s.label == "English SDH" and s.reason.startswith("vớt vát")


def test_external_subs_priority_and_override(lib):
    p = lib.add(MOVIE, J(V(), A(1), S(2, "vie"), S(3, "eng")))
    write_sub(p.with_name(f"{MOVIE}.vi.ass"))
    write_sub(p.with_name(f"{MOVIE}.vi.srt"))  # .srt dung truoc .ass trong SubExtPriority
    write_sub(p.with_name(f"{MOVIE}.forced.srt"))
    write_sub(p.with_name(f"{MOVIE}.ja.srt"))  # ja khong nam trong SubKeepLangs
    plan = lib.plan(p)
    assert [(e.path.name, e.code) for e in plan.ext_subs] == [(f"{MOVIE}.vi.srt", "vi")]
    assert {s.id: s.keep for s in plan.int_subs} == {2: False, 3: True}  # sub ngoai vi thay sub trong vi
    assert plan.default_sub == ("ext", 0)


def test_external_sub_no_suffix_and_any_language_td25(movie_dir, monkeypatch):
    """TĐ-25: hậu tố theo bảng ngôn ngữ chung — thêm 'ja' vào SubKeepLangs là ghép được Phim.ja.srt."""
    lib = Lib(movie_dir, monkeypatch, cfg={"SubKeepLangs": ["vi", "en", "ja"], "SubDefaultPriority": ["vi", "en"]})
    p = lib.add(MOVIE, J(V(), A(1)))
    write_sub(p.with_name(f"{MOVIE}.srt"))  # khong hau to = DefaultExternalLang (vi)
    write_sub(p.with_name(f"{MOVIE}_japanese.srt"))
    write_sub(p.with_name(f"{MOVIE}.English.srt"))
    plan = lib.plan(p)
    assert [e.code for e in plan.ext_subs] == ["vi", "en", "ja"]


def test_default_sub_priority_prefers_non_sdh(lib):
    p = lib.add(MOVIE, J(V(), A(1), S(2, "eng", name="SDH"), S(3, "cze"), S(4, "eng")))
    plan = lib.plan(p)
    assert plan.default_sub == ("int", 4)  # vi khong co -> en, ban khong SDH


def test_sub_auto_plan_items_and_picker_choice(lib):
    p = lib.add(MOVIE, J(V(), A(1), S(2, "eng"), S(3, "eng"), S(4, "cze", codec_id="S_HDMV/PGS")))
    write_sub(p.with_name(f"{MOVIE}.vi.srt"))
    plan = lib.plan(p)
    keys = [(i.key, i.auto_keep) for i in plan.sub_items]
    assert keys == [("E|vi", True), ("I|English", True), ("I|English #2", True), ("I|Czech [ảnh]", False)]
    assert plan.sub_items[0].header == "NGOÀI\nVietnamese (SRT)"
    lib.s.set_sub_choice(plan, [], [3, 4])
    plan = lib.plan(p)
    assert plan.ext_subs == [] and [s.id for s in plan.keep_subs] == [3, 4]
    lib.s.reset_sub_choices()
    assert lib.plan(p).ext_subs


def test_sub_charset_for_legacy_external(lib):
    p = lib.add(MOVIE, J(V(), A(1)))
    write_sub(p.with_name(f"{MOVIE}.vi.srt"), enc="cp1258", text="Đây là tiên")
    write_sub(p.with_name(f"{MOVIE}.cs.srt"), enc="cp1250", text="Příliš")
    args = lib.s.build_args(lib.plan(p))
    cs = [args[i + 1] for i, a in enumerate(args) if a == "--sub-charset"]
    assert cs == ["0:windows-1258", "0:windows-1250"]


# ==========================================================================
#  LỆNH MKVMERGE (§6.2, SPEC 4.4)
# ==========================================================================


def full_plan(lib, ver=82):
    lib.s.ver = tools.ToolVersion(True, ver, "")
    lib.s.f_def, lib.s.f_for = tools.mkvmerge_flag_names(ver)
    p = lib.add(MOVIE, J(V(), A(1, "eng", bps=640000), A(2, "eng", name="Commentary"), A(3, "cze", ch=2),
                         S(4, "eng"), S(5, "eng", name="SDH"), S(6, "cze"), S(7, "fre")))
    write_sub(p.with_name(f"{MOVIE}.vi.srt"))
    return lib.plan(p), p


def test_mkvmerge_args_v82_exact(lib):
    plan, p = full_plan(lib)
    out = lib.root / "Output" / f"{plan.out_base}.mkv"
    vi = os.fspath(p.with_name(f"{MOVIE}.vi.srt"))
    assert plan.out_base == f"{MOVIE} - 1080p HEVC EAC35.1"
    assert lib.s.build_args(plan) == [
        "-o", os.fspath(out), "--track-order", "0:0,0:1,0:3,1:0,0:4,0:6",
        "--title", plan.out_base,
        "--audio-tracks", "1,3", "--subtitle-tracks", "4,6",
        "--track-name", "1:English E-AC-3 5.1", "--track-name", "3:Czech E-AC-3 2.0",
        "--track-name", "4:English", "--track-name", "6:Czech",
        "--default-track-flag", "1:1", "--default-track-flag", "3:0",
        "--default-track-flag", "4:0", "--forced-display-flag", "4:0",
        "--default-track-flag", "6:0", "--forced-display-flag", "6:0",
        os.fspath(p),
        "--language", "0:vi", "--track-name", "0:Vietnamese", "--default-track-flag", "0:1", "--forced-display-flag", "0:0", vi,
    ]


def test_mkvmerge_flag_names_v73_and_iso_codes_v49(lib):
    plan, _ = full_plan(lib, ver=73)
    a = lib.s.build_args(plan)
    assert "--default-track" in a and "--forced-track" in a and "--default-track-flag" not in a
    assert "0:vi" in a
    plan, _ = full_plan(lib, ver=49)
    a = lib.s.build_args(plan)
    assert "0:vie" in a and "--default-track" in a


def test_no_subtitles_when_none_kept_and_no_audio_list_when_all_kept(movie_dir, monkeypatch):
    lib = Lib(movie_dir, monkeypatch, cfg={"SubKeepLangs": ["vi"], "UseTrackOrder": False, "SetTitleFromName": False})
    p = lib.add(MOVIE, J(V(), A(1), A(2, "cze"), S(3, "eng")))
    a = lib.s.build_args(lib.plan(p))
    assert "--no-subtitles" in a and "--audio-tracks" not in a and "--track-order" not in a and "--title" not in a


def test_codec_tag_only_in_file_name_and_off_for_tv(movie_dir, monkeypatch):
    lib = Lib(movie_dir, monkeypatch)
    p = lib.add("Show.S01E01.1080p", J(V(), A(1)))
    plan = lib.plan(p)
    assert plan.codec_tag == "" and "phim bộ → không gắn thẻ codec" in plan.notes
    lib2 = Lib(movie_dir, monkeypatch, cfg={"CodecTagForTv": True, "CodecTagParts": ["res", "acodec"]})
    lib2.s.json_cache = lib.s.json_cache
    plan2 = lib2.plan(p)
    assert plan2.codec_tag == "1080p EAC35.1"
    atmos = J(V(dim="3840x2160"), A(1, codec="TrueHD Atmos", codec_id="A_TRUEHD", ch=8))
    assert mux.codec_tag(atmos, {"color_transfer": "smpte2084"}, "Movie.2160p.DV", atmos["tracks"][1],
                         ["res", "hdr", "vcodec", "acodec"]) == "2160p DV HDR HEVC TrueHD7.1 Atmos"


# ==========================================================================
#  THỰC THI: mã thoát, file dở dang, hoàn tác (TĐ-17), "Xem lệnh" không ghi gì (TĐ-21)
# ==========================================================================


def fake_run(codes, made):
    """Thay tools.run: ghi file đầu ra '-o' rồi trả mã thoát lần lượt theo ``codes``."""
    seq = list(codes)

    def run(args, job=None, timeout=None, cwd=None):
        args = [os.fspath(a) for a in args]
        if "-o" in args:
            out = Path(args[args.index("-o") + 1])
            out.write_bytes(b"x" * 100)
            made.append(out)
            code = seq.pop(0)
            return tools.RunResult(code, "Warning: something" if code == 1 else "", "Error: boom" if code >= 2 else "")
        return tools.RunResult(0, "", "")
    return run


def test_execute_codes_temp_name_and_undo(lib, monkeypatch):
    a = lib.add("Phim.A.2020", J(V(), A(1)))
    b = lib.add("Phim.B.2020", J(V(), A(1)))
    c = lib.add("Phim.C.2020", J(V(), A(1)))
    made: list[Path] = []
    monkeypatch.setattr(tools, "run", fake_run([0, 1, 2], made))
    plans = lib.s.plan_all("none", [a, b, c])
    assert not (lib.root / "Output").exists()  # lap ke hoach + Xem lenh khong tao thu muc (TD-21)
    assert lib.s.preview_commands(plans[0]) and not (lib.root / "Output").exists()
    rep = lib.s.execute(plans)
    assert (rep.ok, rep.warning, rep.failed) == (2, 1, 1)
    assert all(m.name.endswith(".muxstudio-tmp.mkv") for m in made)  # ghi ten tam roi moi doi ten
    out = lib.root / "Output"
    assert sorted(x.name for x in out.iterdir()) == ["Phim.A.2020 - 1080p HEVC EAC35.1.mkv", "Phim.B.2020 - 1080p HEVC EAC35.1.mkv"]
    rows = undo.read_run(rep.log_path)
    assert [(r["Action"], r["Status"]) for r in rows] == [("Mkdir", ""), ("Create", "OK"), ("Create", "OK-Warning"), ("Fail", "FAIL")]
    trashed = []
    up = undo.plan_undo(rep.log_path)
    undo.execute_undo(up, trash_fn=lambda path, rb=True: (trashed.append(Path(path)), os.remove(path))[0] is None)
    assert len(trashed) == 2 and not out.exists()  # file mux vao Thung rac, thu muc Output moi tao bi xoa


def test_execute_skips_pending_and_existing(lib, monkeypatch):
    p = lib.add(MOVIE, J(V(), A(1), A(2)))
    made: list[Path] = []
    monkeypatch.setattr(tools, "run", fake_run([0], made))
    plans = lib.s.plan_all("none", [p])
    assert plans[0].status == "need_choice"
    rep = lib.s.execute(plans)
    assert rep.pending == 1 and rep.ok == 0 and made == []


def test_skip_if_output_exists_incl_tagged_name(movie_dir, monkeypatch):
    lib = Lib(movie_dir, monkeypatch, cfg={"SkipIfOutputExists": True})
    p = lib.add(MOVIE, J(V(), A(1)))
    (movie_dir / "Output").mkdir()
    (movie_dir / "Output" / f"{MOVIE} - 2160p HEVC.mkv").write_bytes(b"")
    plan = lib.plan(p)
    assert plan.status == "exists" and "2160p" in plan.reasons[0]


def test_existing_output_goes_to_trash_before_replace(lib, monkeypatch):
    p = lib.add("Phim.A.2020", J(V(), A(1)))
    out = lib.root / "Output" / "Phim.A.2020 - 1080p HEVC EAC35.1.mkv"
    out.parent.mkdir()
    out.write_bytes(b"cu")
    trashed = []
    monkeypatch.setattr(undo, "trash", lambda path, rb=True: (trashed.append(Path(path)), os.remove(path))[0] is None)
    monkeypatch.setattr(tools, "run", fake_run([0], []))
    rep = lib.s.execute(lib.s.plan_all("none", [p]))
    assert rep.ok == 1 and trashed == [out] and out.read_bytes() == b"x" * 100


def test_plan_all_requires_database_choice(lib):
    with pytest.raises(ValueError):
        lib.s.plan_all(None)
    assert lib.s.db_status()["tmdb"].text == "TMDB: chưa điền key" and lib.s.db_status()["tvdb"].ok is None


def test_scan_skips_output_and_donor_folder(lib):
    lib.add(MOVIE, J(V(), A(1)))
    lib.add("x", J(V()), rel="Output/Phim (2020) [tmdbid-1]")
    lib.add("y", J(V()), rel="audio_nguon")
    lib.add("Show.S01E01", J(V()), rel="Season 1")
    assert [p.name for p in lib.s.scan()] == [f"{MOVIE}.mkv", "Show.S01E01.mkv"]


def test_keep_original_names_for_pending(movie_dir, monkeypatch):
    h = FakeHTTP()
    from fakehttp import FakeResponse

    h.add("GET", r"/search/movie$", FakeResponse(200, {"results": [{"id": 1, "title": "Khac Han", "release_date": "2001-01-01"}]}))
    lib = Lib(movie_dir, monkeypatch, secrets={"TmdbApiKey": "0123456789abcdef0123456789abcdef"}, http=h)
    p = lib.add("Phim.Thu.2020.1080p", J(V(), A(1)))
    plans = lib.s.plan_all("tmdb", [p])
    assert plans[0].status == "need_choice" and plans[0].name.candidates[0].title == "Khac Han"
    (np,) = lib.s.keep_original_names(plans)
    assert np.status == "ok" and np.out_base.startswith("Phim.Thu.2020.1080p - ")
