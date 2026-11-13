"""Xuất dữ liệu đối chiếu cho bản .NET: các hàm thuần của ``core/subprep.py`` (bước ②).

Ghi ra ``dotnet/tests/MuxStudio.Core.Tests/Data/python_subprep_cases.json``:
* ``refs``: các bộ mốc dùng chung (để file nhỏ);
* ``trim`` (``remove_leading_extra``), ``repair`` (``repair_collapsed_cues``), ``health`` (``sub_health`` — test C# ghi cùng nội dung
  file rồi gọi), ``resolve`` (``RefIndex.build`` + ``resolve_ref``), ``audio`` (``pick_audio_stream``), ``num`` (``_num``/``_signed``).
Gồm các ca của ``test_golden_subprep.py`` (đối chiếu với .bat) và ca sinh ngẫu nhiên (cố định hạt giống).

Chạy lại sau khi sửa ``core/subprep.py``:  ``python tools/gen_dotnet_subprep.py``
"""
from __future__ import annotations

import json
import random
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests"))

from core import probe, srt, subprep  # noqa: E402
from synth import make_ref_times, srt_text  # noqa: E402
from test_subfetch import ass_text  # noqa: E402

OUT = ROOT / "dotnet" / "tests" / "MuxStudio.Core.Tests" / "Data" / "python_subprep_cases.json"


def blocks(times, dur=1.0):
    return [srt.Block(t, t + dur, "", f"dòng {i}") for i, t in enumerate(times)]


def recap(ref, n, delay):
    return [1.0 + 2 * i for i in range(n)] + [r + delay for r in ref]


def main() -> None:
    rnd = random.Random(20261113)
    refs = {"REF": make_ref_times(300, start=30, seed=1), "HREF": make_ref_times(400, seed=5),
            "R77": make_ref_times(400, seed=77), "R99": make_ref_times(300, start=30, seed=99),
            "R2600": make_ref_times(300, start=2600, seed=3)}
    REF, HREF = refs["REF"], refs["HREF"]
    data: dict = {"refs": refs}

    # ---------------------------------------------------------------- trim (remove_leading_extra)
    g = random.Random(7)
    trims = [("REF", recap(REF, 30, 65)), ("REF", [r + 5 for r in REF]), ("REF", recap(REF, 200, 420)),
             ("REF", [1.0 + 2 * i for i in range(30)] + [(r + 65) / (25 / 23.976) for r in REF]),
             ("REF", sorted(g.uniform(0, 300) for _ in range(40)) + [r + 320 for r in REF]),
             ("REF", recap(REF[:9], 30, 65)), ("REF", recap(refs["R99"], 30, 65)), ("R2600", recap(refs["R2600"], 30, 65)),
             (None, recap(REF, 30, 65))]
    for _ in range(25):
        name = rnd.choice(["REF", "HREF", "R2600"])
        base = refs[name]
        n_rec = rnd.choice([0, 5, 12, 30, 60, 90, 130])
        scale = rnd.choice([1.0, 1.0, 25 / 23.976, 23.976 / 25])
        delay = rnd.uniform(-20, 400)
        keep_n = rnd.choice([len(base), 60, 35])
        t = [1.0 + rnd.uniform(1.0, 3.0) * i for i in range(n_rec)] + [(r + delay) / scale for r in base[:keep_n]]
        trims.append((name, t))
    data["trim"] = []  # [[ten moc, moc], [ket qua voi min_votes 20, ... 40]]; ket qua = [so dong giu (dau tien bi cat), so dong cat]
    for name, t in trims:
        outs = []
        for mv in (20, 40):
            keep, cut = subprep.remove_leading_extra(blocks(t), refs[name] if name else None, mv)
            assert keep is None or [b.start for b in keep] == t[cut:]
            outs.append(None if keep is None else [len(keep), cut])
        data["trim"].append([[name, t], outs])

    # ---------------------------------------------------------------- repair (repair_collapsed_cues)
    reps = [[0.0] * 5 + REF[:100], [0.0, 0.01] + REF[:100], [0.0] * 5 + REF[:8], [], [0.04] * 3 + REF[:9], [0.05] * 4 + REF[:20]]
    reps += [[0.0] * rnd.randint(0, 8) + [rnd.uniform(0, 0.06) for _ in range(rnd.randint(0, 4))] + REF[:rnd.randint(0, 30)]
             for _ in range(30)]
    data["repair"] = []
    for t in reps:
        for mz in (0, 2, 5):
            keep, n = subprep.repair_collapsed_cues(blocks(t), mz)
            assert keep is None or [b.start for b in keep] == [x for x in t if x >= 0.05]
            data["repair"].append([[t, mz], None if keep is None else [len(keep), n]])

    # ---------------------------------------------------------------- health (sub_health)
    health_cases = [  # (fmt, times, ref, dur, before, src, len)
        ("srt", HREF, "HREF", HREF[-1] + 30, 400, [t - 3 for t in HREF], 1.0),
        ("srt", [0.0] * 5 + HREF, "HREF", HREF[-1] + 30, 0, None, 1.0),
        ("srt", HREF, "HREF", HREF[-1] + 30, 0, None, 6.0),
        ("srt", HREF, "HREF", HREF[-1] + 30, 500, None, 1.0),
        ("srt", HREF, None, HREF[-1] - 200, 0, None, 1.0),
        ("srt", HREF, "R77", 0, 0, None, 1.0),
        ("srt", [r / (25 / 23.976) for r in HREF], "HREF", 0, 0, None, 1.0),
        ("srt", [r + 2 for r in HREF], "HREF", 600, 0, None, 1.0),
        ("srt", HREF, None, 0, 0, [t * 0.995 for t in HREF], 1.0),
        ("srt", HREF[:40], None, 0, 0, None, 1.0),
        ("srt", [0.0] * 4 + [r + 1.5 for r in HREF[:300]], "HREF", 1000, 350, HREF[:304], 3.0),
        ("ass", HREF, "HREF", HREF[-1] + 30, 400, None, 1.0),
        ("ass", [r + 2 for r in HREF], "HREF", 600, 0, None, 1.0),
        ("srt", [], "HREF", 0, 0, None, 1.0),
    ]
    for _ in range(20):
        zero = rnd.choice([0, 0, 2, 3, 6])
        sh = rnd.choice([0.0, 0.0, 0.5, 2.0, -3.0])
        sc = rnd.choice([1.0, 1.0, 25 / 23.976, 1.0005])
        n = rnd.choice([400, 300, 45, 120])
        t = [0.0] * zero + [r / sc + sh for r in HREF[:n]]
        src = rnd.choice([None, [x - rnd.choice([0.0, 1.0, 4.0]) for x in t], [x * 0.997 for x in t]])
        health_cases.append((rnd.choice(["srt", "srt", "ass"]), t, rnd.choice(["HREF", None, "R77"]),
                             rnd.choice([0, HREF[-1] + 30, HREF[-1] - 300, 700]), rnd.choice([0, n, n + 40]), src,
                             rnd.choice([1.0, 3.0, 6.5])))
    data["health"] = []
    with tempfile.TemporaryDirectory() as d:
        for i, (fmt, t, rn, dur, before, src, ln) in enumerate(health_cases):
            p = Path(d) / f"h{i}.{fmt}"
            if fmt == "ass":
                p.write_text(ass_text(t), encoding="utf-8", newline="")
            else:
                p.write_text(srt_text(t, ln), encoding="utf-8", newline="")
            outs = []
            for mv, mz in ((20, 2), (60, 0)):
                h = subprep.sub_health(p, refs[rn] if rn else None, dur, before, src, mv, mz)
                outs.append([h.level, h.flags, h.codes, h.cues, h.shift_first, h.shift_last, h.res_offset, h.res_votes,
                             h.res_fps, h.fmt, h.text, h.spread])
            data["health"].append([[fmt, t, rn, dur, before, src, ln], outs])  # ket qua: (min_votes, max_zero) = (20, 2), (60, 0)

    # ---------------------------------------------------------------- resolve (RefIndex.build + resolve_ref)
    show = [f"Show.S01E0{i}.1080p.WEB-DL" for i in (1, 2, 3)]
    trees = [
        (["Dune.Part.Two.2024.2160p.mkv", "Avengers.Endgame.2019.1080p.mkv", "Other.Name.S02E07.mkv", *[s + ".mkv" for s in show]],
         "video", 5, ["Show.S01E02.vi", "Show.S01E01.1080p.WEB-DL.en", "Dune.Part.Two.2024.WEB.vi", "Avengers.Endgame.2019.vi",
                      "Totally Unknown Thing.vi", "Phim Bo S02E07.vi", "Unknown.Film.Part.3.vi", "Show - 03", "Dune.2021.vi"]),
        (["srt_goc/Movie.A.2010.srt", "srt_goc/Movie.A.2010.en.srt", "srt_goc/Movie.B.2012.srt", "Movie.A.2010.mkv"], "sub", 2,
         ["Movie.A.2010.vi", "Movie B 2012 WEB.cs", "Movie.A.2010.en"]),
        ([f"[Judas] One Piece - {e} [1080p].mkv" for e in ("0643", "0644", "0645")], "video", 1, ["One Piece - 0644.vi", "One Piece - 0646"]),
        (["Show.S01E06.1080p.WEB-DL.mkv"], "video", 1, ["Show.S01E05.vi", "Show.S01E06.vi"]),
        (["Frieren.Beyond.Journeys.End.S01E05.1080p.mkv", "Frieren.Beyond.Journeys.End.S02E05.1080p.mkv"], "video", 1,
         ["Sousou no Frieren - 05.vi", "Frieren S02E05.vi"]),
        (["Foo.Bar.S01E05.mkv"], "video", 1, ["Completely Different - 07", "Foo Bar 05"]),
        (["Foo.Bar.2019.mkv"], "video", 1, ["Something E05"]),
        (["Foo.Bar.2019.mkv"], "video", 2, ["Something E05"]),
        (["Movie.Night.2010.mkv", "Movie.Night.2012.mkv"], "video", 3, ["Movie Night.vi", "Movie Night 2012.vi"]),
        (["One.Piece.S16E644.1080p.mkv", "One.Piece.S16E645.1080p.mkv"], "video", 2, ["One Piece - 0644.vi"]),
        (["Movie.A.2010.en.srt", "Movie.A.2010.srt", "Movie.A.2010.vi.srt"], "sub", 1, ["movie.a.2010", "Movie A 2010.cs"]),
        (["Movie.A.2010.en.srt", "Movie.A.2010.mkv"], "", 1, ["Movie.A.2010.vi"]),
    ]
    root = Path("C:/lib")
    data["resolve"] = []
    for files, kind, nsub, subs in trees:
        idx = subprep.RefIndex.build([root / f for f in files], kind, root, [".mkv", ".mp4"])
        info = [len(idx.items), list(idx.by_key.keys()), idx.canons(), idx.summary()]
        for s in subs:
            for ms, mm in ((0.5, 0.15), (0.3, 0.05)):
                m = subprep.resolve_ref(idx, s, nsub, ms, mm)
                data["resolve"].append([[files, kind, nsub, s, ms, mm],
                                        [info, m.ref.canon if m.ref else None, m.ref.file.name if m.ref else None, m.how, m.reason,
                                         m.details]])

    # ---------------------------------------------------------------- audio (pick_audio_stream)
    lists = [[], [probe.AudioStream(0, "eng", "eac3", 6, "", False), probe.AudioStream(1, "cze", "aac", 2, "Dabing", True)],
             [probe.AudioStream(0, "jpn", "flac", 2, "", False), probe.AudioStream(1, "eng", "aac", 2, "", False)],
             [probe.AudioStream(0, "und", "ac3", 6, "", True)]]
    choices = [subprep.AudioChoice("auto"), subprep.AudioChoice("index", 0), subprep.AudioChoice("index", 1),
               subprep.AudioChoice("index", 5), subprep.AudioChoice("index", -1), subprep.AudioChoice("index", None),
               subprep.AudioChoice("lang", "en"), subprep.AudioChoice("lang", "cs"), subprep.AudioChoice("lang", "vi"),
               subprep.AudioChoice("lang", ""), subprep.AudioChoice("lang", "ja")]
    data["audio"] = []
    for st in lists:
        for ch in choices:
            data["audio"].append([[[[a.index, a.lang, a.codec, a.channels, a.title, a.default] for a in st], ch.mode, ch.value],
                                  list(subprep.pick_audio_stream(st, ch))])

    # ---------------------------------------------------------------- num / signed
    nums = [0.0, -0.0, 1.0, -2.0, 2.675, 0.125, -0.125, 1.005, -65.0004, 0.995, 3.14159, 1e-9, -1e-9, 12345.6789, 0.05, -0.05]
    nums += [rnd.uniform(-500, 500) for _ in range(60)] + [round(rnd.uniform(-10, 10), 3) for _ in range(60)]
    data["num"] = [[[x, d], [subprep._num(x, d), subprep._signed(x, d), f"{x:+.2f}", f"{x:.{d}f}"]] for x in nums for d in (1, 2, 4)]

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":")), encoding="utf-8", newline="")
    print(f"{sum(len(v) for k, v in data.items() if k != 'refs')} ca -> {OUT.relative_to(ROOT)} "
          f"({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
