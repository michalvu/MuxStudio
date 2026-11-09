"""Đối chiếu bước ② với HÀM GỐC trong reference/SubPrep_v20.bat (chạy bằng PowerShell ``pwsh``).

``Remove-LeadingExtra``, ``Repair-CollapsedCues``, ``Test-SubHealth``, ``Resolve-Ref`` (+ ``Get-RefIndex``).
Chỗ bản mới CỐ Ý khác (TĐ-14, TĐ-31) thì test kiểm tra rằng nó khác ĐÚNG như mô tả.
Không có ``pwsh`` hoặc thiếu ``reference/*.bat`` → bỏ qua cả file.
"""
from __future__ import annotations

import json
import random
import re
import subprocess

import pytest

from conftest import REFERENCE, ROOT
from core import config as C
from core import srt, subprep, tools
from synth import make_ref_times, srt_text
from test_golden_powershell import PWSH
from test_subfetch import ass_text

BAT = "SubPrep_v20.bat"
pytestmark = [
    pytest.mark.golden,
    pytest.mark.skipif(PWSH is None, reason="không có PowerShell (pwsh / powershell.exe)"),
    pytest.mark.skipif(not (REFERENCE / BAT).exists(), reason="thiếu reference/SubPrep_v20.bat"),
]

REF = make_ref_times(300, start=30, seed=1)
HREF = make_ref_times(400, seed=5)


def recap(ref, n, delay):
    return [1.0 + 2 * i for i in range(n)] + [r + delay for r in ref]


rng = random.Random(7)
TRIM = {
    "recap30": recap(REF, 30, 65),
    "none": [r + 5 for r in REF],
    "long": recap(REF, 200, 420),
    "fps": [1.0 + 2 * i for i in range(30)] + [(r + 65) / (25 / 23.976) for r in REF],
    "random": sorted(rng.uniform(0, 300) for _ in range(40)) + [r + 320 for r in REF],
    "short": recap(REF[:9], 30, 65),
}
REPAIR = {"five": [0.0] * 5 + REF[:100], "two": [0.0, 0.01] + REF[:100], "few_left": [0.0] * 5 + REF[:8]}
# (moc, moc goc, thoi luong phim, so dong truoc sync, moc truoc sync, do dai dong)
HEALTH = {
    "good": (HREF, HREF, HREF[-1] + 30, 400, [t - 3 for t in HREF], 1.0),
    "zero": ([0.0] * 5 + HREF, HREF, HREF[-1] + 30, 0, None, 1.0),
    "overlap": (HREF, HREF, HREF[-1] + 30, 0, None, 6.0),
    "lost": (HREF, HREF, HREF[-1] + 30, 500, None, 1.0),
    "overrun": (HREF, None, HREF[-1] - 200, 0, None, 1.0),
    "weak": (HREF, make_ref_times(400, seed=77), 0, 0, None, 1.0),
    "fps": ([r / (25 / 23.976) for r in HREF], HREF, 0, 0, None, 1.0),
    "offset": ([r + 2 for r in HREF], HREF, 600, 0, None, 1.0),
    "stretch": (HREF, None, 0, 0, [t * 0.995 for t in HREF], 1.0),
    "few": (HREF[:40], None, 0, 0, None, 1.0),
    "mixed": ([0.0] * 4 + [r + 1.5 for r in HREF[:300]], HREF, 1000, 350, HREF[:304], 3.0),
}

# Cay thu muc cho Resolve-Ref: ten file -> (che do, so phu de, {ten phu de: (ket qua .bat, ket qua moi)})
SHOW = [f"Show.S01E0{i}.1080p.WEB-DL" for i in (1, 2, 3)]
TREES = {
    "[T1] Lib": (["Dune.Part.Two.2024.2160p.mkv", "Avengers.Endgame.2019.1080p.mkv", "Other.Name.S02E07.mkv",
                  *[s + ".mkv" for s in SHOW]], "video", 5, {
        "Show.S01E02.vi": (SHOW[1], SHOW[1]),
        "Show.S01E01.1080p.WEB-DL.en": (SHOW[0], SHOW[0]),
        "Dune.Part.Two.2024.WEB.vi": ("Dune.Part.Two.2024.2160p",) * 2,
        "Avengers.Endgame.2019.vi": ("Avengers.Endgame.2019.1080p",) * 2,
        "Totally Unknown Thing.vi": ("", ""),
        "Phim Bo S02E07.vi": ("Other.Name.S02E07",) * 2,
        "Unknown.Film.Part.3.vi": ("", ""),
        "Show - 03": ("", SHOW[2]),  # TD-31a: .bat "nhap nhang theo ten phim"
    }),
    "[T2] goc": (["srt_goc/Movie.A.2010.srt", "srt_goc/Movie.A.2010.en.srt", "srt_goc/Movie.B.2012.srt", "Movie.A.2010.mkv"],
                 "sub", 2, {"Movie.A.2010.vi": ("Movie.A.2010",) * 2, "Movie B 2012 WEB.cs": ("Movie.B.2012",) * 2}),
    "[T3] anime": ([f"[Judas] One Piece - {e} [1080p].mkv" for e in ("0643", "0644", "0645")], "video", 1,
                   {"One Piece - 0644.vi": ("", "[Judas] One Piece - 0644 [1080p]")}),  # TD-31a
    "[T4] thieu tap": (["Show.S01E06.1080p.WEB-DL.mkv"], "video", 1, {"Show.S01E05.vi": ("Show.S01E06.1080p.WEB-DL", "")}),  # TD-31b
    "[T5] tang3": (["Frieren.Beyond.Journeys.End.S01E05.1080p.mkv", "Frieren.Beyond.Journeys.End.S02E05.1080p.mkv"], "video", 1,
                   {"Sousou no Frieren - 05.vi": ("*", "")}),  # TD-31c: .bat lay bua 1 file
    "[T6] tang4": (["Foo.Bar.S01E05.mkv"], "video", 1, {"Completely Different - 07": ("Foo.Bar.S01E05", "")}),  # TD-31b
    "[T7] tang4 ok": (["Foo.Bar.2019.mkv"], "video", 1, {"Something E05": ("Foo.Bar.2019",) * 2}),
}


def _flag_code(f: str) -> str:
    for pat, code in ((r"dong dinh o 00:00:00", "zero"), (r"cho chong lan", "overlap"), (r"^mat ", "lost"),
                      (r"^dong cuoi", "overrun"), (r"moc khop$", "weak"), (r"con lech FPS", "fps"), (r"^van lech", "offset"),
                      (r"^co gian", "stretch"), (r"ky tu hong", "badchar"), (r"^chi co \d+ dong", "few")):
        if re.search(pat, f):
            return code
    return "?" + f


def _nums(s: str) -> list[str]:
    return re.findall(r"-?\d+(?:\.\d+)?", s)


@pytest.fixture(scope="module")
def ps(tmp_path_factory):
    d = tmp_path_factory.mktemp("[golden] subprep")
    cases, ids = [], {}

    def case(fn, *args):
        cid = f"c{len(cases)}"
        cases.append({"id": cid, "bat": BAT, "fn": fn, "args": list(args)})
        return cid

    for k, t in TRIM.items():
        p = d / f"trim_{k}.srt"
        p.write_text(srt_text(t, 1.0), encoding="utf-8")
        ids[("trim", k)] = case("Remove-LeadingExtra", str(p), REF)
    for k, t in REPAIR.items():
        p = d / f"rep_{k}.srt"
        p.write_text(srt_text(t, 1.0), encoding="utf-8")
        ids[("rep", k)] = case("Repair-CollapsedCues", str(p))
    for k, (t, ref, dur, before, src, ln) in HEALTH.items():
        p = d / f"h_{k}.srt"
        p.write_text(srt_text(t, ln), encoding="utf-8")
        ids[("h", k)] = case("Test-SubHealth", str(p), ref, dur, before, src)
    bad = d / "h_badchar.srt"
    bad.write_bytes(srt_text(HREF, 1.0).encode("utf-8").replace("Dòng 7".encode(), b"D\xffng 7"))
    ids[("h", "badchar")] = case("Test-SubHealth", str(bad), HREF, 0, 0, None)
    ass = d / "h_ass.ass"
    ass.write_text(ass_text(HREF), encoding="utf-8")
    ids[("h", "ass")] = case("Test-SubHealth", str(ass), HREF, HREF[-1] + 30, 400, None)
    for name, (files, kind, n, subs) in TREES.items():
        root = d / name
        for f in files:
            (root / f).parent.mkdir(parents=True, exist_ok=True)
            if f.endswith(".srt"):
                (root / f).write_text(srt_text(REF[:50]), encoding="utf-8")
            else:
                (root / f).write_bytes(b"")
        ids[("rr", name)] = case("Resolve-Ref", str(root), kind, n, list(subs))
    inp, out = d / "in.json", d / "out.json"
    inp.write_text(json.dumps(cases), encoding="utf-8")
    r = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                        str(ROOT / "tests" / "golden" / "ps_harness.ps1"), "-BatDir", str(REFERENCE), "-InFile", str(inp),
                        "-OutFile", str(out)], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
    assert r.returncode == 0, r.stderr[-2000:]
    res = json.loads(out.read_text(encoding="utf-8-sig"))
    res["_ids"], res["_dir"] = ids, d
    return res


def blocks(times, dur=1.0):
    return [srt.Block(t, t + dur, "", "x") for t in times]


def test_remove_leading_extra_same_as_bat(ps):
    for k, t in TRIM.items():
        g = ps[ps["_ids"][("trim", k)]]
        keep, cut = subprep.remove_leading_extra(blocks(t), REF)
        assert (g["Ok"], g["Cut"]) == (keep is not None, cut), k
    assert ps[ps["_ids"][("trim", "recap30")]]["Cut"] == 30


def test_repair_collapsed_same_as_bat(ps):
    for k, t in REPAIR.items():
        g = ps[ps["_ids"][("rep", k)]]
        keep, fix = subprep.repair_collapsed_cues(blocks(t), 2)
        assert (g["Ok"], g["Fix"]) == (keep is not None, fix), k


def test_sub_health_same_as_bat(ps):
    d = ps["_dir"]
    for k in [*HEALTH, "badchar"]:
        g = ps[ps["_ids"][("h", k)]]
        if k == "badchar":
            args = (HREF, 0, 0, None)
        else:
            t, ref, dur, before, src, _ = HEALTH[k]
            args = (ref, dur, before, src)
        h = subprep.sub_health(d / f"h_{k}.srt", *args)
        assert (g["Level"], g["Cues"], g["ResVotes"], bool(g["ResFps"])) == (h.level, h.cues, h.res_votes, bool(h.res_fps)), k
        for a, b in ((g["ShiftFirst"], h.shift_first), (g["ShiftLast"], h.shift_last), (g["ResOffset"], h.res_offset)):
            assert (a is None) == (b is None) and (a is None or a == pytest.approx(b, abs=1e-9)), k
        flags = list(g["Flags"] or [])
        assert [_flag_code(f) for f in flags] == h.codes, (k, flags, h.flags)
        assert [_nums(f) for f in flags] == [_nums(f) for f in h.flags], (k, flags, h.flags)


def test_sub_health_ass_differs_as_documented(ps):
    """TĐ-14: .bat không đọc được ASS → 'CÓ VẤN ĐỀ'; bản mới đọc mốc ASS → TỐT."""
    g = ps[ps["_ids"][("h", "ass")]]
    assert (g["Level"], g["Flags"]) == (2, ["khong doc duoc noi dung"])
    h = subprep.sub_health(ps["_dir"] / "h_ass.ass", HREF, HREF[-1] + 30, 400, None)
    assert (h.level, h.cues) == (0, 400)


def test_resolve_ref_vs_bat(ps):
    for name, (_files, kind, n, subs) in TREES.items():
        g = ps[ps["_ids"][("rr", name)]]
        with subprep.PrepSession(ps["_dir"] / name, C.Config({}), tools.Job(), tool_paths={}) as s:
            idx = s.build_index("sub" if kind == "sub" else "audio")
            mine = [(m.ref.canon if m.ref else "") for m in (subprep.resolve_ref(idx, x, n) for x in subs)]
        for (sub, (want_bat, want_new)), theirs, ours in zip(subs.items(), g, mine):
            if want_bat == "*":
                assert theirs != "", (name, sub)  # .bat: chon bua mot file, khong bao nhap nhang
            else:
                assert theirs == want_bat, (name, sub, theirs)
            assert ours == want_new, (name, sub, ours)
