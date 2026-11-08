"""Đối chiếu bản Python với HÀM GỐC trong reference/*.bat (chạy bằng PowerShell ``pwsh``).

Chỗ nào bản mới CỐ Ý khác .bat (lỗi đã sửa, docs/SPEC.md mục 7) thì test kiểm tra rằng nó
khác ĐÚNG như mô tả — không bỏ qua.

Không có ``pwsh`` hoặc thiếu ``reference/*.bat`` → bỏ qua cả file.
"""
from __future__ import annotations

import json
import random
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

from conftest import REFERENCE, ROOT
from core import encoding, episodes, naming, pgs, srt, timing
from synth import make_ref_times, pgs_stream, srt_text, transform_times

# pwsh (PowerShell 7) hoac, tren Windows, powershell.exe 5.1 - dung ban .bat dang chay
PWSH = shutil.which("pwsh") or (shutil.which("powershell") if sys.platform == "win32" else None)
BATS = {"SF": "SubFetch_v30.bat", "SP": "SubPrep_v20.bat", "MT": "MuxTool_v17.bat"}
pytestmark = [
    pytest.mark.golden,
    pytest.mark.skipif(PWSH is None, reason="không có PowerShell (pwsh / powershell.exe)"),
    pytest.mark.skipif(not all((REFERENCE / b).exists() for b in BATS.values()), reason="thiếu reference/*.bat"),
]

_CASES: list[dict] = []


def case(bat: str, fn: str, *args) -> str:
    cid = f"c{len(_CASES)}"
    _CASES.append({"id": cid, "bat": BATS[bat], "fn": fn, "args": list(args)})
    return cid


# ---------------------------------------------------------------- du lieu
rng = random.Random(2024)
ARR = {n: [round(rng.uniform(0, 6000), 3) for _ in range(n)] for n in (50, 401, 1000, 1234)}
EVEN = {(n, k): case("SP", "Get-EvenSample", ARR[n], k) for n in ARR for k in (100, 400)}

REF = make_ref_times(700, seed=77)
TGT_PAL = transform_times(REF, 25 / 23.976, 12.3, seed=5)
TGT_SAME = transform_times(REF, 1.0, -30.45, seed=6)
OTHER = make_ref_times(650, seed=999)
REL = {
    "pal": case("SP", "Get-TimingRelation", REF, TGT_PAL),
    "same": case("SP", "Get-TimingRelation", REF, TGT_SAME),
    "other": case("SP", "Get-TimingRelation", REF, OTHER),
}
R400 = timing.even_sample(REF, 400)
VOTE = {
    "same": case("SP", "Get-OffsetVote", R400, timing.even_sample(TGT_SAME, 100)),
    "rand": case("SP", "Get-OffsetVote", sorted(ARR[401][:400]), sorted(ARR[1000][:100])),
    "tie": case("SP", "Get-OffsetVote", [1.5, 3.0], [0.0]),
}
PV = {
    "ok": [r - 42.5 for r in REF[20:25]],
    "jit": [r - 7.25 + d for r, d in zip(REF[40:45], (0.03, -0.02, 0.01, -0.03, 0.02))],
    "rand": sorted(rng.uniform(0, 5000) for _ in range(5)),
    "two": [r - 1 for r in REF[1:3]],
}
QUICK = {k: case("SF", "Get-QuickOffset", REF, v) for k, v in PV.items()}

DOUBLES = ["23.976", "23,976", " 25 ", "1e3", "", "abc", "1,234.5", "-0.5", None]
DBL = {s: case("SP", "ConvertTo-Double", s) for s in DOUBLES}

TITLE_NAMES = ["The.Lord.of.the.Rings.2001.1080p.BluRay.x264-GRP[YTS] (Extended)", "Action.Movie.Drama.avi",
               "Dune Part Two (2024) [YTS.MX]", "28.Years.Later.2025.1080p.AMZN.WEB-DL.DDP5.1",
               "[Judas] One Piece - 1085 [1080p]", "Người Nhện Xa Nhà 2019", "Show.S01E05.1080p.vi"]
TOK = {n: case("SP", "Get-TitleTokens", n) for n in TITLE_NAMES}
SCORE_PAIRS = [(TITLE_NAMES[2], "Dune.Part.Two.2024.2160p"), (TITLE_NAMES[0], "Lord of the Rings"), ("a b c", "d e f")]
SCORE = {p: case("SP", "Get-TitleScore", *p) for p in SCORE_PAIRS}

STAMPS = [0.0, 10.5, 1799.0, 1800.0, 2700.0, 3599.9, 3600.0, 4000.25, 5400.0, 6000.25, 7300.004]
STAMP = {s: case("SF", "Format-SrtStamp", s) for s in STAMPS}

EP_SAME = ["Show.S01E05.1080p.WEB-DL", "One Piece S21E1085 1080p", "Show 1x05 720p", "One Piece - 0643",
           "[Judas] One Piece - 1085 [1080p]", "Naruto EP 055", "Show Season 2 Episode 5", "Kimetsu - 05v2 [1080p]",
           "Phim Tap 12", "[SubsPlease] Frieren - 12 (1080p) [ABCD1234]", "Movie.2019.DDP5.1.x264",
           "Blade Runner 2049", "Movie - 1080p", "Avengers.Endgame.2019.2160p"]
EP_DIFF = ["1917 - 2019", "Show.S01E05E06", "One Piece - 1085[1080p]"]  # TD-03
SP_EP = {n: case("SP", "Get-EpisodeInfo", n) for n in EP_SAME + EP_DIFF}

MT_EP_SAME = ["Show.S01E05.1080p.WEB-DL", "One.Piece.S16E643.1080p", "Show.S01E05-E06.1080p", "Show.S01E05E06",
              "Show 1x05 720p", "One Piece - 0643", "[Judas] One Piece - 1085 [1080p]", "Naruto EP 055",
              "Doctor.Who.2005.S01E01.720p", "1917 - 2019", "Movie - 1080p", "Blade Runner 2049"]
MT_EP_DIFF = ["Show Season 2 Episode 5", "Phim Tap 12"]  # TD-03
MT_EP = {n: case("MT", "Split-EpisodeName", n) for n in MT_EP_SAME + MT_EP_DIFF}

REL_SAME = ["Avengers.Endgame.2019.2160p.BluRay.x265-GRP", "28.Years.Later.2025.1080p.AMZN.WEB-DL.DDP5.1",
            "[YTS.MX] Dune Part Two (2024) [2160p]", "1917 - 2019", "Blade.Runner.2049.2017.1080p",
            "2001.A.Space.Odyssey.1968.1080p", "Movie - 1080p"]
REL_DIFF = ["Blade Runner 2049", "2001 A Space Odyssey", "Charlotte's.Web.2006.720p", "Uncut.Gems.2019.1080p"]  # TD-05
MT_REL = {n: case("MT", "Split-ReleaseName", n) for n in REL_SAME + REL_DIFF}

SAFE = ["Mission: Impossible", 'What? <Why> "No" | a/b\\c *', "Movie.  ", "  A   B "]
MT_SAFE = {s: case("MT", "Get-SafeFileName", s) for s in SAFE}
NORM = {s: case("MT", "Get-NormTitle", s) for s in ["Spider-Man: No Way Home", "Amélie (2001)"]}
LANGS = [("cze", "cs"), ("ger", "de"), ("eng", "en"), ("und", ""), ("spa", "es-419"), ("hun", "hu"), ("jpn", ""), ("chi", "zh-Hans")]
MT_LANG = {p: case("MT", "Get-LangCode", *p) for p in LANGS}

CONFLICT_SAME = [("Show.S01E10", "Show.S01E01"), ("Show.S01E05", "Show.S01E05.vi"), ("Show.S01E01", "Show.S02E01"),
                 ("Show.S01E05", "Movie (2019)")]
CONFLICT_DIFF = [("One Piece - 0643", "One Piece - 0644")]  # TD-02
SF_CONF = {p: case("SF", "Test-EpisodeConflict", *p) for p in CONFLICT_SAME + CONFLICT_DIFF}

FILES: dict[str, Path] = {}


@pytest.fixture(scope="module")
def ps(tmp_path_factory):
    d = tmp_path_factory.mktemp("[golden] du lieu")
    # file phu de cho Get-CueTimes / Read-SrtBlocks / Get-SubEncodingKind
    srt_times = [5.0, 1900.0, 2700.5, 4000.25]
    FILES["srt"] = d / "a.srt"
    FILES["srt"].write_text(srt_text(srt_times), encoding="utf-8")
    FILES["srtdot"] = d / "b.srt"
    FILES["srtdot"].write_text("1\n00:00:01.50 --> 00:00:02.00\nA\n\n2\n00:00:03,250 --> 00:00:04,000\nB\n", encoding="utf-8")
    FILES["ass"] = d / "c.ass"
    FILES["ass"].write_text("[Events]\nDialogue: 0,0:00:05.20,0:00:07.00,D,,0,0,0,,X\nDialogue: Marked=0,0:01:02.34,0:01:03.00,D,,0,0,0,,Y\n", encoding="utf-8")
    FILES["u16"] = d / "d.srt"
    FILES["u16"].write_bytes(b"\xff\xfe" + srt_text([1.0, 2.0]).encode("utf-16-le"))
    FILES["sup"] = d / "e.sup"
    FILES["sup"].write_bytes(pgs_stream([(100, "show"), (102, "hide"), (1900, "show"), (1902, "palette"), (1903, "hide"),
                                         (2700, "show"), (2701, "show"), (2702.5, "hide"), (4000, "show"), (4000.05, "hide")]))
    FILES["supout"] = d / "e_ps.srt"
    enc_files = {"utf8": "Tiếng".encode("utf-8"), "bom": b"\xef\xbb\xbfabc", "le": b"\xff\xfea\x00", "be": b"\xfe\xff\x00a",
                 "leg": b"Vi\xea\xf2t", "empty": b""}
    for k, data in enc_files.items():
        FILES[f"enc_{k}"] = d / f"enc_{k}.srt"
        FILES[f"enc_{k}"].write_bytes(data)

    ids = {}
    for k in ("srt", "srtdot", "ass", "u16"):
        ids[("cue", k)] = case("SF", "Get-CueTimes", str(FILES[k]))
    ids[("blocks", "srt")] = case("SP", "Read-SrtBlocks", str(FILES["srt"]))
    ids["pgs"] = case("SP", "Convert-PgsToTimingSrt", str(FILES["sup"]), str(FILES["supout"]))
    for k in enc_files:
        ids[("enc", k)] = case("SP", "Get-SubEncodingKind", str(FILES[f"enc_{k}"]))

    inp = d / "in.json"
    out = d / "out.json"
    inp.write_text(json.dumps(_CASES), encoding="utf-8")
    r = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tests" / "golden" / "ps_harness.ps1"),
                        "-BatDir", str(REFERENCE), "-InFile", str(inp), "-OutFile", str(out)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
    assert r.returncode == 0, r.stderr[-2000:]
    res = json.loads(out.read_text(encoding="utf-8-sig"))
    res["_ids"] = ids
    return res


def approx_list(a, b, tol=1e-9):
    return len(a) == len(b) and all(abs(x - y) <= tol for x, y in zip(a, b))


# ---------------------------------------------------------------- so khop thoi gian
def test_even_sample(ps):
    for (n, k), cid in EVEN.items():
        assert approx_list(ps[cid], timing.even_sample(ARR[n], k)), (n, k)


def test_offset_vote(ps):
    for key, cid in VOTE.items():
        c = next(x for x in _CASES if x["id"] == cid)
        v = timing.offset_vote(*c["args"])
        assert ps[cid]["Votes"] == v.votes, key
        assert ps[cid]["Offset"] == pytest.approx(v.offset, abs=1e-12), key


def test_timing_relation(ps):
    for key, cid in REL.items():
        c = next(x for x in _CASES if x["id"] == cid)
        mine = timing.timing_relation(*c["args"])
        g = ps[cid]
        assert g["Scale"] == mine.scale, key
        assert g["Votes"] == mine.votes, key
        assert g["Offset"] == pytest.approx(mine.offset, abs=1e-12), key
        assert g["Drift"] == pytest.approx(mine.drift, abs=1e-9), key
        assert g["FpsMismatch"] == mine.fps_mismatch, key


def test_quick_offset(ps):
    for key, cid in QUICK.items():
        mine = timing.quick_offset(REF, PV[key])
        g = ps[cid]
        if mine is None:
            assert g is None, key
        else:
            assert (g["Offset"], g["Votes"], g["Total"]) == (mine.offset, mine.votes, mine.total), key


def test_convert_to_double(ps):
    for s, cid in DBL.items():
        assert ps[cid] == timing.to_double(s), s


# ---------------------------------------------------------------- tu khoa ten
def test_title_tokens_and_score(ps):
    for n, cid in TOK.items():
        assert (ps[cid] or []) == naming.title_tokens(n), n
    for (a, b), cid in SCORE.items():
        assert ps[cid] == pytest.approx(naming.title_score(naming.title_tokens(a), naming.title_tokens(b))), (a, b)


# ---------------------------------------------------------------- moc thoi gian, PGS, SRT
def test_format_stamp_same_except_hour_bug(ps):
    for s, cid in STAMP.items():
        mine = srt.format_stamp(s)
        theirs = ps[cid]
        assert mine[2:] == theirs[2:], s  # phut:giay,mili giong nhau
        assert int(mine[:2]) == int(s // 3600), s  # ban moi: phan nguyen
        # TD-01: .bat dung [int]TotalHours = lam tron ve so chan (0.5 -> 0, 0.75 -> 1, 1.5 -> 2)
        assert int(theirs[:2]) == round(s / 3600), s


def test_cue_times(ps):
    ids = ps["_ids"]
    for k in ("srt", "srtdot", "ass", "u16"):
        assert approx_list(ps[ids[("cue", k)]], srt.cue_times(FILES[k])), k


def test_read_srt_blocks(ps):
    g = ps[ps["_ids"][("blocks", "srt")]]
    mine = srt.read_blocks(FILES["srt"])
    assert [(x["S"], x["E"], x["T"]) for x in g] == [(b.start, b.end, b.text) for b in mine]


def test_pgs_same_cues_hours_fixed(ps):
    n = ps[ps["_ids"]["pgs"]]
    cues = pgs.pgs_cues(FILES["sup"].read_bytes())
    assert n == len(cues) == 4
    theirs = [srt.parse_stamp(m.split(" --> ")[0]) for m in FILES["supout"].read_text(encoding="utf-8").splitlines() if "-->" in m]
    for (s, _), t in zip(cues, theirs):
        assert (t % 3600) == pytest.approx(s % 3600, abs=0.001)  # cung moc, chi khac gio (TD-01)
    assert [round(t) for t in theirs] == [100, 5500, 6300, 6301]  # .bat: 1900 -> 01:31:40, 2700 -> 01:45:00
    assert [round(s) for s, _ in cues] == [100, 1900, 2700, 2701]


def test_encoding_kind(ps):
    for k in ("utf8", "bom", "le", "be", "leg", "empty"):
        assert ps[ps["_ids"][("enc", k)]] == encoding.detect_kind(FILES[f"enc_{k}"]), k


# ---------------------------------------------------------------- nhan dien tap
def _sp_tuple(g):
    return None if g is None else (g["Season"], g["Episode"], g["HasSeason"], g["Confident"])


def test_subprep_episode_info(ps):
    for n in EP_SAME:
        mine = episodes.parse_episode(n, allow_guess=True)
        mt = None if mine is None else (mine.season, mine.episode, mine.has_season, mine.confident)
        assert _sp_tuple(ps[SP_EP[n]]) == mt, n
    # TD-03: cac ca .bat sai
    assert _sp_tuple(ps[SP_EP["1917 - 2019"]]) == (0, 2019, False, True)
    assert episodes.parse_episode("1917 - 2019", allow_guess=True) is None or not episodes.parse_episode("1917 - 2019", allow_guess=True).confident
    assert _sp_tuple(ps[SP_EP["Show.S01E05E06"]]) == (0, 6, False, False)
    assert episodes.parse_episode("Show.S01E05E06").episode_end == 6
    assert _sp_tuple(ps[SP_EP["One Piece - 1085[1080p]"]])[3] is False
    assert episodes.parse_episode("One Piece - 1085[1080p]").confident


def test_muxtool_split_episode_name(ps):
    for n in MT_EP_SAME:
        g = ps[MT_EP[n]]
        mine = episodes.parse_episode(n)
        if g is None:
            assert mine is None, n
            continue
        assert (g["Series"], g["Season"], g["EpStart"], g["EpEnd"], g["Absolute"], g["Year"]) == \
            (mine.series, mine.season, mine.episode, mine.episode_end, mine.absolute, mine.year), n
    g = ps[MT_EP["Show Season 2 Episode 5"]]
    assert (g["Series"], g["Absolute"]) == ("Show Season 2", True)  # TD-03
    assert episodes.parse_episode("Show Season 2 Episode 5").season == 2
    assert ps[MT_EP["Phim Tap 12"]] is None and episodes.parse_episode("Phim Tap 12").episode == 12


def test_subfetch_episode_conflict(ps):
    for p in CONFLICT_SAME:
        assert ps[SF_CONF[p]] == episodes.episode_conflict(*p), p
    assert ps[SF_CONF[CONFLICT_DIFF[0]]] is False and episodes.episode_conflict(*CONFLICT_DIFF[0]) is True  # TD-02


# ---------------------------------------------------------------- ten phim, ten file, ngon ngu
def test_split_release_name(ps):
    for n in REL_SAME:
        g = ps[MT_REL[n]]
        r = naming.split_release_name(n, current_year=2026)
        assert (g["Title"], g["Year"]) == (r.title, r.year), n
    for n in REL_DIFF:  # TD-05
        g = ps[MT_REL[n]]
        r = naming.split_release_name(n, current_year=2026)
        assert (g["Title"], g["Year"]) != (r.title, r.year), n


def test_safe_filename_norm_title(ps):
    for s, cid in MT_SAFE.items():
        assert ps[cid] == naming.safe_filename(s), s
    for s, cid in NORM.items():
        assert ps[cid] == naming.norm_title(s), s


def test_lang_code(ps):
    for p, cid in MT_LANG.items():
        assert ps[cid] == naming.lang_from_track(*p), p
