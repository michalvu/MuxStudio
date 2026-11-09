"""Đối chiếu bước ③ với HÀM GỐC trong reference/MuxTool_v17.bat (chạy bằng PowerShell ``pwsh``).

Tên codec / kênh / bitrate / độ phân giải / HDR / thẻ codec, nhận diện commentary / audio description, mã ngôn ngữ cho
mkvmerge, ``--sub-charset``, ``Get-SubAutoPlan`` (quy tắc phụ đề), ``Find-DonorFile``, đặt tên TMDB
(``Get-MovieFileName``, ``Get-TmdbTvName`` gồm One Piece trùng tên + số tập tuyệt đối) và ``Resolve-TvdbEpisode``
— ``Invoke-TmdbApi``/``Invoke-TvdbApi`` của .bat được thay bằng API giả trả CÙNG dữ liệu với API giả bên Python.
Chỗ bản mới CỐ Ý khác (TĐ-33) thì test kiểm tra rằng nó khác ĐÚNG như mô tả.
"""
from __future__ import annotations

import json
import os
import re
import subprocess

import pytest

from conftest import REFERENCE, ROOT
from core import config as C
from core import donor as D
from core import encoding, mux, naming, probe, tmdb, tools, tvdb
from fakehttp import FakeHTTP, FakeResponse, Sleeper
from test_golden_powershell import PWSH
from test_mux import A, J, S, V
from test_tmdb import ANIME, ANIME_SEASONS, LIVE

BAT = "MuxTool_v17.bat"
pytestmark = [
    pytest.mark.golden,
    pytest.mark.skipif(PWSH is None, reason="không có PowerShell (pwsh / powershell.exe)"),
    pytest.mark.skipif(not (REFERENCE / BAT).exists(), reason="thiếu reference/MuxTool_v17.bat"),
]

# (codec cap track, codec_id, so kenh) - nhu mkvmerge -J that ghi
AUDIO = [("AC-3", "A_AC3", 6), ("E-AC-3", "A_EAC3", 6), ("DTS-HD Master Audio", "A_DTS", 8), ("DTS-HD High Resolution Audio", "A_DTS", 6),
         ("DTS-ES", "A_DTS", 7), ("DTS", "A_DTS", 6), ("TrueHD Atmos", "A_TRUEHD", 8), ("AAC", "A_AAC", 2), ("FLAC", "A_FLAC", 2),
         ("Opus", "A_OPUS", 2), ("Vorbis", "A_VORBIS", 2), ("MP3", "A_MPEG/L3", 2), ("PCM", "A_PCM/INT/LIT", 2)]
DIMS = ["3840x2160", "2560x1440", "1920x1080", "1920x800", "1280x720", "720x576", "720x480", "x", ""]
VCODECS = [("V_MPEGH/ISO/HEVC", ""), ("V_MPEG4/ISO/AVC", ""), ("V_AV1", ""), ("V_VP9", ""), ("V_MPEG2", ""), ("V_THEORA", "")]
HDR_NAMES = ["Movie.2160p.DV.HEVC", "Movie.2160p.DoVi.HDR10+", "Movie.HDR.2160p", "Movie.HLG", "Movie.Dolby.Vision", "Movie.1080p"]
HDR_DIFF = ["Movie.2160p.DV.HDR10.HEVC", "Movie.HDR10.1080p"]  # TD-33c
TRACK_NAMES = ["Director's Commentary", "Comments", "Commentary by cast", "Isolated Score", "Binh luan", "Audio Description",
               "Descriptive Audio", "English", "", "Narration for the blind", "Visually Impaired"]
LANG_MUX = [("vi", 50), ("cs", 82), ("vi", 49), ("cs", 49), ("en", 49), ("ja", 49), ("zh", 49), ("de", 49), ("hu", 49)]

GOT = {"id": 1399, "name": "Game of Thrones", "original_name": "Game of Thrones", "first_air_date": "2011-04-17"}
DUNE = {"id": 693134, "title": "Dune: Part Two", "original_title": "Dune: Part Two", "release_date": "2024-02-27"}
TMDB_FAKE = {
    "/search/movie": {"results": [DUNE]},
    "/search/tv": {"results": [GOT]},
    "/tv/1399": {**GOT, "seasons": [{"season_number": 1, "episode_count": 10}]},
    "/tv/1399/season/1/episode/5": {"name": "The Wolf and the Lion"},
}
OP_FAKE = {
    "/search/tv": {"results": [LIVE, ANIME]},
    "/tv/37854": {**ANIME, "seasons": ANIME_SEASONS},
    "/tv/111110": {**LIVE, "seasons": [{"season_number": 1, "episode_count": 8}]},
    "/tv/37854/season/15/episode/65": {"name": "Hody: Jones?"},
}
TVDB_EPS = [{"id": 10000 + n, "seasonNumber": 15 if n > 578 else 1, "number": n - 578 if n > 578 else n, "absoluteNumber": n,
             "nameTranslations": ["eng"]} for n in range(1, 700)]
TVDB_FAKE = {"/series/81797/episodes/default?page=0": {"data": {"episodes": TVDB_EPS}, "links": {"next": None}},
             "/episodes/10643/translations/eng": {"data": {"name": "Tên tập 643"}}}
TVDB_CASES = [(81797, 0, 643, True), (81797, 1, 5, True), (81797, 15, 65, False), (81797, 3, 900, True), (81797, 16, 643, True)]
DONOR_POOL = ["One.Piece.S16E643.CZ.mkv", "One.Piece.S16E644.CZ.mkv", "Show.S02E05.CZ.mkv", "Dune.2021.CZ.mkv"]
DONOR_CASES = ["[Judas] One Piece - 0643 [1080p]", "One Piece - 0645", "Show - 05", "Show.S02E05.1080p", "Dune.2021.2160p", "Dune.1984"]


def mkv_track(codec, codec_id, ch, tid=1, **p):
    return {"id": tid, "type": "audio", "codec": codec, "properties": {"codec_id": codec_id, "audio_channels": ch, **p}}


@pytest.fixture(scope="module")
def ps(tmp_path_factory):
    d = tmp_path_factory.mktemp("[golden] muxtool")
    cases, ids = [], {}

    def case(fn, *args):
        cid = f"c{len(cases)}"
        cases.append({"id": cid, "bat": BAT, "fn": fn, "args": list(args)})
        return cid

    for c, cid, ch in AUDIO:
        t = mkv_track(c, cid, ch)
        ids[("codec_real", c)] = case("Get-CodecName", t["properties"])  # nhu mkvmerge -J that: KHONG co codec trong properties
        ids[("codec_prop", c)] = case("Get-CodecName", {**t["properties"], "codec": c})  # y dinh cua ham: co codec
        ids[("bps", c)] = case("Get-AudioBitrate", {**t, "properties": {**t["properties"], "codec": c}})
    ids[("bps", "tag")] = case("Get-AudioBitrate", mkv_track("AC-3", "A_AC3", 6, tag_bps="640000"))
    for dim in DIMS:
        ids[("res", dim)] = case("Get-ResolutionName", dim)
    for cid, c in VCODECS:
        ids[("vc", cid)] = case("Get-VideoCodecName", cid, c)
    for n in HDR_NAMES + HDR_DIFF:
        ids[("hdr", n)] = case("Get-DynamicRange", n)
    for n in TRACK_NAMES:
        ids[("cm", n)] = case("Test-CommentaryTrack", {"track_name": n})
        ids[("ds", n)] = case("Test-DescriptiveTrack", {"track_name": n})
    ids[("cm", "flag")] = case("Test-CommentaryTrack", {"flag_commentary": True})
    ids[("ds", "flag")] = case("Test-DescriptiveTrack", {"flag_visual_impaired": True})
    for code, ver in LANG_MUX:
        ids[("lm", code, ver)] = case("Get-LangForMux", code, ver)
    # the codec: track audio co codec trong properties (y dinh) va khong co (thuc te)
    jt = J(V(dim="3840x2160"), {**mkv_track("TrueHD Atmos", "A_TRUEHD", 8), "properties": {"codec_id": "A_TRUEHD", "audio_channels": 8, "codec": "TrueHD Atmos"}})
    ids["tag_prop"] = case("Get-CodecTag", jt, "Movie.2160p.DV", 1)
    jr = J(V(dim="1920x1080"), mkv_track("E-AC-3", "A_EAC3", 6))
    ids["tag_real"] = case("Get-CodecTag", jr, "Movie.1080p.WEB", 1)
    # --sub-charset
    for code, enc, txt in (("vi", "cp1258", "Đây là tiên"), ("cs", "cp1250", "Příliš"), ("en", "utf-8", "Hello"), ("ja", "shift_jis", "日本")):
        p = d / f"cs_{code}.srt"
        p.write_bytes(f"1\r\n00:00:01,000 --> 00:00:02,000\r\n{txt}\r\n".encode(enc))
        ids[("charset", code)] = case("Get-SubCharsetArg", str(p), code)
    # Get-SubAutoPlan: file phim + sub ngoai + JSON
    sp = d / "[SP] lib"
    sp.mkdir()
    vid = sp / "Phim.Thu.2020.mkv"
    vid.write_bytes(b"")
    for suf in (".vi.srt", ".vi.ass", ".cs.srt", ".forced.srt", ".en.sdh.srt", ".srt"):
        (sp / f"Phim.Thu.2020{suf}").write_text("x", encoding="utf-8")
    jsp = J(V(), A(1), S(2, "eng"), S(3, "eng", name="SDH"), S(4, "eng", forced=True), S(5, "cze", codec_id="S_HDMV/PGS"),
            S(6, "vie"), S(7, "fre"), S(8, "eng"), S(9, "cze", name="Commentary"))
    ids["sap"] = case("Get-SubAutoPlan", str(vid), jsp)
    # Find-DonorFile
    dn = d / "[DN] lib"
    (dn / "audio_nguon").mkdir(parents=True)
    for n in DONOR_POOL:
        (dn / "audio_nguon" / n).write_bytes(b"")
    for n in DONOR_CASES:
        ids[("donor", n)] = case("Find-DonorFile", str(dn), n)
    # TMDB / TVDB voi API gia
    root = d / "Output"
    ids["movie"] = case("Get-MovieFileName", "Dune.Part.Two.2024.2160p", TMDB_FAKE)
    ids["tv_got"] = case("Get-TmdbTvName", "Game.of.Thrones.S01E05.1080p", TMDB_FAKE, str(root))
    ids["tv_got7"] = case("Get-TmdbTvName", "Game.of.Thrones.S01E07.1080p", TMDB_FAKE, str(root))
    ids["tv_op"] = case("Get-TmdbTvName", "[Judas] One Piece - 0643 [1080p]", OP_FAKE, str(root))
    for c_ in TVDB_CASES:
        ids[("tvdb", c_)] = case("Resolve-TvdbEpisode", *c_, TVDB_FAKE)

    inp, out = d / "in.json", d / "out.json"
    inp.write_text(json.dumps(cases), encoding="utf-8")
    r = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                        str(ROOT / "tests" / "golden" / "ps_harness.ps1"), "-BatDir", str(REFERENCE), "-InFile", str(inp),
                        "-OutFile", str(out)], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
    assert r.returncode == 0, r.stderr[-2000:]
    res = json.loads(out.read_text(encoding="utf-8-sig"))
    res["_ids"], res["_dir"] = ids, d
    return res


def g(ps, key):
    return ps[ps["_ids"][key]]


def test_codec_name_bug_td33_and_same_when_codec_given(ps):
    """TĐ-33: mkvmerge ghi ``codec`` ở cấp track ⇒ hàm gốc chỉ thấy codec_id (``EAC3``, mọi DTS → ``DTS``)."""
    for c, cid, ch in AUDIO:
        t = mkv_track(c, cid, ch)
        assert g(ps, ("codec_prop", c)) == probe.audio_codec_name(t), c  # cung y dinh -> cung ket qua
        assert g(ps, ("codec_real", c)) == re.sub(r"^A_", "", cid), c  # .bat thuc te: codec_id bo 'A_'
    assert probe.audio_codec_name(mkv_track("E-AC-3", "A_EAC3", 6)) == "E-AC-3"
    assert probe.audio_codec_name(mkv_track("DTS-HD Master Audio", "A_DTS", 8)) == "DTS-HD MA"


def test_audio_bitrate_same(ps):
    for c, cid, ch in AUDIO:
        assert g(ps, ("bps", c)) == pytest.approx(probe.audio_bitrate(mkv_track(c, cid, ch))[0]), c
    assert g(ps, ("bps", "tag")) == probe.audio_bitrate(mkv_track("AC-3", "A_AC3", 6, tag_bps="640000"))[0] == 640000


def test_resolution_video_codec_same(ps):
    for dim in DIMS:
        assert g(ps, ("res", dim)) == probe.resolution_name(dim), dim
    for cid, c in VCODECS:
        assert g(ps, ("vc", cid)) == probe.video_codec_name(cid, c), cid


def test_dynamic_range_from_name(ps):
    for n in HDR_NAMES:
        assert g(ps, ("hdr", n)) == probe.dynamic_range(None, n), n
    for n in HDR_DIFF:  # TD-33c: 'HDR10' khong co '+' -> .bat bo sot HDR
        assert "HDR" not in g(ps, ("hdr", n)) and "HDR" in probe.dynamic_range(None, n), n


def test_commentary_descriptive_same(ps):
    for n in [*TRACK_NAMES, "flag"]:
        pc = {"flag_commentary": True} if n == "flag" else {"track_name": n}
        pd = {"flag_visual_impaired": True} if n == "flag" else {"track_name": n}
        assert g(ps, ("cm", n)) == probe.is_commentary(pc), n
        assert g(ps, ("ds", n)) == probe.is_descriptive(pd), n


def test_lang_for_mux_same(ps):
    for code, ver in LANG_MUX:
        assert g(ps, ("lm", code, ver)) == naming.lang_for_mux(code, ver), (code, ver)


def test_codec_tag(ps):
    jt = J(V(dim="3840x2160"), mkv_track("TrueHD Atmos", "A_TRUEHD", 8))
    parts = ["res", "hdr", "vcodec", "acodec"]
    assert g(ps, "tag_prop") == mux.codec_tag(jt, None, "Movie.2160p.DV", jt["tracks"][1], parts) == "2160p DV HEVC TrueHD7.1 Atmos"
    jr = J(V(dim="1920x1080"), mkv_track("E-AC-3", "A_EAC3", 6))
    assert g(ps, "tag_real") == "1080p HEVC EAC35.1"  # .bat: 'EAC3' tu codec_id (TD-33) -> rut gon trung hop
    assert mux.codec_tag(jr, None, "Movie.1080p.WEB", jr["tracks"][1], parts) == "1080p HEVC EAC35.1"


def test_sub_charset_same(ps):
    d = ps["_dir"]
    for code in ("vi", "cs", "en", "ja"):
        assert g(ps, ("charset", code)) == encoding.mkvmerge_charset(d / f"cs_{code}.srt", code), code


def test_sub_auto_plan_same(ps):
    d = ps["_dir"]
    vid = d / "[SP] lib" / "Phim.Thu.2020.mkv"
    jsp = J(V(), A(1), S(2, "eng"), S(3, "eng", name="SDH"), S(4, "eng", forced=True), S(5, "cze", codec_id="S_HDMV/PGS"),
            S(6, "vie"), S(7, "fre"), S(8, "eng"), S(9, "cze", name="Commentary"))
    mine = mux.sub_auto_plan(vid, jsp, C.Config({}))
    theirs = g(ps, "sap")
    ext_t = sorted((x["Key"], x["Code"], x["AutoKeep"]) for x in theirs if x["Kind"] == "ext")
    ext_m = sorted((x.key, x.code, x.auto_keep) for x in mine if x.kind == "ext")
    assert ext_t == ext_m == [("E|cs", "cs", True), ("E|en", "en", True), ("E|vi", "vi", True)]
    int_t = [(x["Id"], x["Code"], x["AutoKeep"]) for x in theirs if x["Kind"] == "int"]
    int_m = [(x.id, x.code, x.auto_keep) for x in mine if x.kind == "int"]
    assert int_t == int_m
    assert len({x.key for x in mine}) == len(mine)  # khoa cot khong trung


def test_find_donor_file_same(ps):
    pool = [ps["_dir"] / "[DN] lib" / "audio_nguon" / n for n in DONOR_POOL]
    for n in DONOR_CASES:
        f = D.find_donor_file(n, pool)
        assert g(ps, ("donor", n)) == (f.name if f else ""), n


def _tmdb(fake):
    h = FakeHTTP()
    for path, body in fake.items():
        h.add("GET", re.escape(path) + "$", FakeResponse(200, body))
    return tmdb.TmdbClient("0123456789abcdef0123456789abcdef", "en-US", 3, h, Sleeper(), tools.Job())


def test_tmdb_naming_same(ps):
    cfg = C.Config({"TmdbConfirm": False})
    o = naming.resolve_output_name("Dune.Part.Two.2024.2160p", "tmdb", {"tmdb": _tmdb(TMDB_FAKE)}, cfg, {})
    assert g(ps, "movie") == o.base == "Dune - Part Two (2024) [tmdbid-693134]"
    root = ps["_dir"] / "Output"
    for key, stem, fake in (("tv_got", "Game.of.Thrones.S01E05.1080p", TMDB_FAKE), ("tv_got7", "Game.of.Thrones.S01E07.1080p", TMDB_FAKE),
                            ("tv_op", "[Judas] One Piece - 0643 [1080p]", OP_FAKE)):
        o = naming.resolve_output_name(stem, "tmdb", {"tmdb": _tmdb(fake)}, cfg, {})
        t = g(ps, key)
        assert (t["Base"], os.path.relpath(t["Dir"], root)) == (o.base, o.rel_dir), key
    assert g(ps, "tv_op")["Base"] == "One Piece (1999) - S15E65 - Hody - Jones"


def test_tvdb_resolve_episode_same(ps):
    h = FakeHTTP().add("POST", r"/login$", FakeResponse(200, {"data": {"token": "t"}}))
    for path, body in TVDB_FAKE.items():
        h.add("GET", re.escape(path) + "$", FakeResponse(200, body))
    c = tvdb.TvdbClient("tvdb-demo-key", "", "eng", "default", 3, h, Sleeper(), tools.Job())
    for case in TVDB_CASES:
        t = g(ps, ("tvdb", case))
        m = c.resolve_episode(*case)
        assert (t["Season"], t["Episode"], t["Title"], t["Mapped"], t["Found"]) == \
            (m.season, m.episode, m.title, m.mapped, m.found), case
