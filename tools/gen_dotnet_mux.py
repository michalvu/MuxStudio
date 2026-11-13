"""Xuất dữ liệu đối chiếu cho bản .NET: bước ③ (``core/mux.py`` + ``core/donor.py``).

Ghi ra ``dotnet/tests/MuxStudio.Core.Tests/Data/python_mux_cases.json``:
* ``plans``: mỗi ca = một thư mục phim giả (JSON ``mkvmerge -J`` giả + phụ đề ngoài thật trên đĩa, cấu hình, bản mkvmerge, thao tác sửa
  kế hoạch) → kế hoạch (trạng thái, lý do, audio, phụ đề, Default, thẻ codec, tên…), NHẬT KÝ của lần lập kế hoạch cuối, ``build_args``
  và ``preview_commands`` (đường dẫn gốc thay bằng ``<ROOT>``);
* ``donor``: như trên nhưng có thư mục ``audio_nguon`` + bảng mốc phụ đề (thay ``mkvextract``) → kế hoạch ghép audio + lệnh;
* ``stretch`` (``stretch_filter`` / ``stretch_factor``), ``find_donor``, ``manual_offset``, ``codec_tag``.
Ngẫu nhiên nhưng cố định hạt giống.

Chạy lại sau khi sửa ``core/mux.py`` / ``core/donor.py``:  ``python tools/gen_dotnet_mux.py``
"""
from __future__ import annotations

import json
import os
import random
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests"))

from core import config as C  # noqa: E402
from core import donor as D  # noqa: E402
from core import mux, tools, timing  # noqa: E402
from synth import make_ref_times  # noqa: E402
from test_mux import A, J, S, V  # noqa: E402

OUT = ROOT / "dotnet" / "tests" / "MuxStudio.Core.Tests" / "Data" / "python_mux_cases.json"

AUDIO = [("AC-3", "A_AC3", 6), ("E-AC-3", "A_EAC3", 6), ("DTS-HD Master Audio", "A_DTS", 8), ("DTS-HD High Resolution Audio", "A_DTS", 6),
         ("DTS", "A_DTS", 6), ("TrueHD Atmos", "A_TRUEHD", 8), ("TrueHD", "A_TRUEHD", 8), ("AAC", "A_AAC", 2), ("FLAC", "A_FLAC", 2),
         ("Opus", "A_OPUS", 2), ("MP3", "A_MPEG/L3", 2), ("PCM", "A_PCM/INT/LIT", 2), ("AC-3", "A_AC3", 2)]
DIMS = ["3840x2160", "2560x1440", "1920x1080", "1920x800", "1280x720", "720x576", "720x480"]
VCODECS = [("V_MPEGH/ISO/HEVC", "HEVC/H.265/MPEG-H"), ("V_MPEG4/ISO/AVC", "AVC/H.264/MPEG-4p10"), ("V_AV1", "AV1"), ("V_VP9", "VP9"),
           ("V_MPEG2", "MPEG-1/2")]
STEMS = ["Phim.Thu.2020.1080p.WEB-DL", "Dune.Part.Two.2024.2160p.DV.HDR10.HEVC", "Show.S01E05.1080p", "[Judas] One Piece - 0643 [1080p]",
         "The.Movie.Atmos.2019.2160p", "Anime - 012 [720p]", "Phim Có Dấu (2021) [YTS.MX]"]
AUDIO_NAMES = ["", "", "", "Director's Commentary", "Audio Description", "English", "Stereo", "Atmos 7.1", "Komentář"]
SUB_NAMES = ["", "", "", "SDH", "English SDH", "Forced", "Commentary", "Signs & Songs"]
EXT_SUFFIXES = [".vi.srt", ".vi.ass", ".cs.srt", ".en.srt", ".forced.srt", ".srt", ".en.sdh.srt", "_japanese.srt", ".English.ass",
                ".vi.sup", ".cs.forced.srt", "-ja.srt", ".de.srt"]
BOOLS = ["UseTrackOrder", "SetTitleFromName", "AddCodecTag", "CodecTagForTv", "RemoveForcedSubs", "RemoveImageSubs", "RemoveCommentarySubs",
         "ExternalSubOverridesInternal", "SkipOnMultipleEnAudio", "SkipOnAmbiguousUnd", "RemoveCommentaryAudio", "RemoveDescriptiveAudio",
         "AskWhichAudio"]
TEXT = {"utf-8": "Xin chào – tiếng Việt", "cp1258": "Đây là tiên", "cp1250": "Příliš žluťoučký", "utf-8-sig": "Hello"}


class _Ver:
    def __init__(self, v):
        self.v = v

    def __call__(self, path, job=None):
        return tools.ToolVersion(True, self.v, f"mkvmerge v{self.v}.0")


def rel(x, root: str):
    """Thay thư mục gốc bằng ``<ROOT>`` trong mọi chuỗi (đệ quy)."""
    if isinstance(x, str):
        return x.replace(root, "<ROOT>")
    if isinstance(x, (list, tuple)):
        return [rel(v, root) for v in x]
    if isinstance(x, dict):
        return {k: rel(v, root) for k, v in x.items()}
    return x


def session(root: Path, cfg_over: dict, ver: int, ffmpeg: str | None = None):
    tools.mkvmerge_version = _Ver(ver)  # type: ignore[assignment]
    cfg = C.Config({"UseDonorAudio": False, **cfg_over})
    s = mux.MuxSession(root, cfg, {}, tools.Job(), None, lambda _s: None,
                       tool_paths={"mkvmerge": "mkvmerge-gia", "mkvextract": None, "ffmpeg": ffmpeg, "ffprobe": None})
    s._ui_lang = []
    return s


def plan_out(s, plan, lines_from: int, root: str) -> dict:
    lines = [[lv, m] for lv, m in s.job.lines[lines_from:]]
    o = {
        "status": plan.status, "reasons": plan.reasons, "notes": plan.notes, "out_base": plan.out_base,
        "out_path": os.fspath(plan.out_path) if plan.out_path else None, "codec_tag": plan.codec_tag,
        "audio": [[a.id, a.code, a.keep, a.reason, a.default, a.label, a.describe()] for a in plan.audio],
        "default_audio": plan.default_audio, "lang_override": [[k, v] for k, v in plan.lang_override.items()],
        "ext_subs": [[e.path.name, e.code, e.rank] for e in plan.ext_subs],
        "int_subs": [[x.id, x.code, x.keep, x.reason, x.default, x.label] for x in plan.int_subs],
        "default_sub": list(plan.default_sub),
        "sub_items": [[i.key, i.kind, i.id, i.code, i.auto_keep, i.header, i.tip, i.order] for i in plan.sub_items],
        "needs_audio_table": plan.needs_audio_table, "rule_applied": plan.rule_applied,
        "donor": {"note": plan.donor.note, "manual": plan.donor.manual, "summary": plan.donor.summary,
                  "donor": plan.donor.donor.name if plan.donor.donor else None,
                  "timing": None if plan.donor.timing is None else [plan.donor.timing.scale, plan.donor.timing.offset,
                                                                     plan.donor.timing.votes, plan.donor.timing.name,
                                                                     plan.donor.timing.how, plan.donor.timing.sure,
                                                                     plan.donor.timing.close],
                  "tracks": [[t.tid, t.a_index, t.code, t.name, t.offset_ms, t.stretch, t.filter, t.out_channels, t.bitrate]
                             for t in plan.donor.tracks]},
        "args": s.build_args(plan) if plan.json is not None and plan.out_path else None,
        "preview": s.preview_commands(plan),
        "log": lines,
    }
    return rel(o, root)


def rand_tracks(rnd: random.Random):
    tracks = []
    dim = rnd.choice(DIMS)
    cid, cname = rnd.choice(VCODECS)
    tracks.append(V(0, dim=dim, codec_id=cid, codec=cname, lang=rnd.choice(["und", "und", "eng", "jpn"]),
                    fps=rnd.choice([23.976, 24, 25, 29.97])))
    tid = 1
    for _ in range(rnd.choice([0, 1, 1, 2, 2, 3, 4])):
        c, ac, ch = rnd.choice(AUDIO)
        kw = {}
        if rnd.random() < 0.15:
            kw["flag_commentary"] = True
        if rnd.random() < 0.1:
            kw["flag_visual_impaired"] = True
        tracks.append(A(tid, rnd.choice(["eng", "eng", "cze", "jpn", "und", "vie", "ger"]), codec=c, codec_id=ac, ch=ch,
                        name=rnd.choice(AUDIO_NAMES), bps=rnd.choice([None, None, 192000, 448000, 640000, 3500000]),
                        ietf=rnd.choice([None, None, "cs", "en-US"]), **kw))
        tid += 1
    for _ in range(rnd.choice([0, 1, 2, 3, 4, 6])):
        codec = rnd.choice(["S_TEXT/UTF8", "S_TEXT/UTF8", "S_TEXT/ASS", "S_HDMV/PGS", "S_VOBSUB"])
        name = rnd.choice(SUB_NAMES)
        tracks.append(S(tid, rnd.choice(["eng", "eng", "cze", "vie", "fre", "und", "jpn"]), codec_id=codec, name=name,
                        forced=rnd.random() < 0.15, sdh=rnd.random() < 0.15))
        tid += 1
    return tracks


def plan_cases(base: Path, rnd: random.Random) -> list:
    out = []
    for n in range(80):
        d = base / f"[P{n}] Thư mục phim"
        d.mkdir(parents=True)
        root = str(d)
        stem = rnd.choice(STEMS)
        tracks = rand_tracks(rnd)
        j = J(*tracks, dur=rnd.choice([5400.0, 1440.5, 7322.25]))
        cfg = {}
        for b in BOOLS:
            if rnd.random() < 0.35:
                cfg[b] = rnd.random() < 0.5
        if rnd.random() < 0.3:
            cfg["SubKeepLangs"] = rnd.choice([["vi", "en"], ["vi", "en", "cs", "ja"], ["cs"], ["en", "fr", "de"]])
        if rnd.random() < 0.3:
            cfg["SubDefaultPriority"] = rnd.choice([["en", "vi"], ["cs", "en"], ["vi"], ["ja", "vi", "en"]])
        if rnd.random() < 0.3:
            cfg["CodecTagParts"] = rnd.sample(["res", "hdr", "vcodec", "acodec"], rnd.randint(1, 4))
        if rnd.random() < 0.2:
            cfg["SubExtPriority"] = rnd.choice([[".ass", ".srt"], [".srt", ".sup", ".ass"]])
        if rnd.random() < 0.2:
            cfg["DefaultExternalLang"] = rnd.choice(["en", "cs"])
        if rnd.random() < 0.2:
            cfg["AskAudioMinTracks"] = rnd.choice([1, 3])
        ver = rnd.choice([49, 73, 82, 82])
        exts = []
        for suf in rnd.sample(EXT_SUFFIXES, rnd.randint(0, 4)):
            enc = "cp1258" if ".vi" in suf and rnd.random() < 0.5 else "cp1250" if ".cs" in suf and rnd.random() < 0.5 else \
                rnd.choice(["utf-8", "utf-8-sig"])
            exts.append([f"{stem}{suf}", enc])
        actions = []
        r = rnd.random()
        aud_ids = [t["id"] for t in tracks if t["type"] == "audio"]
        sub_ids = [t["id"] for t in tracks if t["type"] == "subtitles"]
        if r < 0.15:
            actions.append(["keep_all_audio"])
        elif r < 0.35 and aud_ids:
            actions.append(["set_audio_choice", rnd.sample(aud_ids, rnd.randint(1, len(aud_ids)))])
        elif r < 0.5:
            codes = rnd.sample(["vi", "en", "cs", "ja"], rnd.randint(0, 2))
            actions.append(["set_sub_choice", codes, rnd.sample(sub_ids, rnd.randint(0, len(sub_ids))) if sub_ids else []])
        elif r < 0.55:
            actions.append(["keep_all_audio_all"])
        p = d / f"{stem}.mkv"
        p.write_bytes(b"\x1a\x45\xdf\xa3")
        for name, enc in exts:
            txt = TEXT.get(enc, "x")
            (d / name).write_bytes(f"1\r\n00:00:01,000 --> 00:00:02,000\r\n{txt}\r\n\r\n".encode(enc))
        s = session(d, cfg, ver)
        s.json_cache[os.path.normcase(os.path.abspath(p))] = j
        s.db = "none"
        plan = s.plan_file(p)
        for a in actions:
            if a[0] == "keep_all_audio":
                s.keep_all_audio(plan)
            elif a[0] == "keep_all_audio_all":
                s.keep_all_audio(None)
            elif a[0] == "set_audio_choice":
                s.set_audio_choice(plan, a[1], apply_to_rest=False)
            elif a[0] == "set_sub_choice":
                s.set_sub_choice(plan, a[1], a[2])
        k = len(s.job.lines)
        plan = s.plan_file(p)
        out.append({"stem": stem, "json": j, "exts": exts, "cfg": cfg, "ver": ver, "actions": actions,
                    "out": plan_out(s, plan, k, root)})
        s.close()
    return out


def donor_cases(base: Path, rnd: random.Random) -> list:
    pal = 25 / 23.976
    out = []
    for n in range(60):
        d = base / f"[D{n}] Ghép audio"
        (d / "audio_nguon").mkdir(parents=True)
        root = str(d)
        stem = rnd.choice(["Phim.A.2020.1080p", "Show.S01E03.1080p", "[Judas] One Piece - 0643 [1080p]"])
        dstem = {"Phim.A.2020.1080p": "Phim.A.2020.CZ", "Show.S01E03.1080p": "Show.S01E03.CZ",
                 "[Judas] One Piece - 0643 [1080p]": "One.Piece.S16E643.CZ"}[stem]
        fps_d = rnd.choice([23.976, 23.976, 25, 24])
        scale = {23.976: 1.0, 25: pal, 24: 24 / 23.976}[fps_d]
        mine_tracks = [V(0), A(1, rnd.choice(["eng", "eng", "eng", "eng", "cze"]), ch=6)]
        if rnd.random() < 0.8:
            mine_tracks.append(S(2, "eng"))
        dn_tracks = [V(0, fps=fps_d)]
        tid = 1
        for k in range(rnd.randint(1, 3)):
            c, ac, ch = rnd.choice(AUDIO)
            dn_tracks.append(A(tid, "cze" if k == 0 and rnd.random() < 0.85 else rnd.choice(["cze", "eng", "slo"]), codec=c, codec_id=ac, ch=ch,
                               bps=rnd.choice([None, 192000, 448000]), name=rnd.choice(["", "", "Komentář Commentary"])))
            tid += 1
        if rnd.random() < 0.8:
            dn_tracks.append(S(tid, "eng", codec_id=rnd.choice(["S_TEXT/UTF8", "S_HDMV/PGS"])))
        dur = rnd.choice([6000.0, 2700.0])
        j = J(*mine_tracks, dur=dur)
        dj = J(*dn_tracks, dur=dur / scale + rnd.choice([0.0, 0.4, 30.0]))
        ref = make_ref_times(400, seed=100 + n)
        off = rnd.choice([0.75, -1.5, 0.0, 12.25])
        times = {}
        if rnd.random() < 0.9:
            times[f"{stem}.mkv|2"] = ref
            times[f"{dstem}.mkv|{tid}"] = [(r - off) / scale for r in ref]
        cfg = {"UseDonorAudio": True}
        for b in ("DonorSetDefault", "DonorSkipIfPresent", "DonorAcceptDurationMatch", "UseTrackOrder"):
            if rnd.random() < 0.4:
                cfg[b] = rnd.random() < 0.5
        if rnd.random() < 0.25:
            cfg["DonorLangs"] = rnd.choice([["cs", "sk"], ["en"], ["cs"]])
        if rnd.random() < 0.2:
            cfg["DonorOffsetMs"] = {rnd.choice(["S01E03", "tap #643", "phim.a.2020.1080p", "643"]): rnd.choice([1200, -350])}
        ffmpeg = "ffmpeg" if rnd.random() < 0.8 else None
        override = rnd.choice([None, None, None, 500.0, -2250.0])
        p = d / f"{stem}.mkv"
        p.write_bytes(b"\x1a\x45\xdf\xa3")
        dp = d / "audio_nguon" / f"{dstem}.mkv"
        dp.write_bytes(b"")
        s = session(d, cfg, 82, ffmpeg)
        s.json_cache[os.path.normcase(os.path.abspath(p))] = j
        s.json_cache[os.path.normcase(os.path.abspath(dp))] = dj
        s.donor_pool = D.donor_files(d, "audio_nguon")
        s.track_times = lambda f, trk, t=times: t.get(f"{Path(f).name}|{trk.id}")
        s.db = "none"
        if override is not None:
            s.donor_offsets[os.path.normcase(os.path.abspath(p))] = override
        k = len(s.job.lines)
        plan = s.plan_file(p)
        out.append({"stem": stem, "dstem": dstem, "json": j, "djson": dj, "times": times, "cfg": cfg, "ffmpeg": ffmpeg,
                    "override": override, "out": plan_out(s, plan, k, root)})
        s.close()
    return out


def main() -> None:
    rnd = random.Random(20261113)
    base = Path(tempfile.mkdtemp(prefix="muxgen_"))
    try:
        data: dict = {"plans": plan_cases(base, rnd), "donor": donor_cases(base, rnd)}
    finally:
        shutil.rmtree(base, ignore_errors=True)
    scales = [fr.ratio for fr in timing.FPS_RATIOS] + [25 / 23.976, 23.976 / 25, 24 / 23.976, 1.0123456789, 1.001, 0.999]
    scales += [rnd.uniform(0.9, 1.1) for _ in range(20)]
    data["stretch"] = []
    for sr in (48000, 44100, 32000, 96000, 22050):
        for sc in scales:
            f = D.stretch_filter(sr, sc)
            data["stretch"].append([[sr, sc], [f, D.stretch_factor(f, sr)]])
    pool = ["One.Piece.S16E642.CZ.mkv", "One.Piece.S16E643.CZ.mkv", "Show.S02E05.CZ.mkv", "Show.S01E05.CZ.mkv", "Dune.2021.CZ.mkv",
            "Dune.1984.mkv", "Ban.Sec.mkv", "Anime - 012.mka"]
    stems = ["[Judas] One Piece - 0643 [1080p]", "One Piece - 0645", "Show - 05", "Show.S02E05.1080p", "Show.S01E05", "Dune.2021.2160p",
             "Dune.1984", "Dune", "Phim.Gi.Do.2020", "Anime - 12", "Anime S01E12"]
    pools = [pool, ["Ban.Sec.mkv"], ["Show.S01E05.CZ.mkv"], pool[:3], [], ["Dune.2021.CZ.mkv", "Dune.1984.mkv"]]
    data["find_donor"] = [[[st, pl], D.find_donor_file(st, [Path("C:\\n") / x for x in pl]).name
                           if D.find_donor_file(st, [Path("C:\\n") / x for x in pl]) else None] for st in stems for pl in pools]
    offs = {"S01E03": 1200, "tap #643": -500, "Phim.Le.2020": "300", "77": 40.5, " s02e01 ": "1,5"}
    data["manual_offset"] = [[st, D.manual_offset(st, offs)] for st in
                             ["Show.S01E03.1080p", "[Judas] One Piece - 0643", "phim.le.2020", "Anime - 077", "Show.S01E04", "X.S02E01"]]
    tags = []
    for _ in range(150):
        c, ac, ch = rnd.choice(AUDIO)
        j = J(V(dim=rnd.choice(DIMS + ["x", ""]), codec_id=rnd.choice(VCODECS)[0]),
              A(1, codec=c, codec_id=ac, ch=ch, name=rnd.choice(AUDIO_NAMES)))
        vs = rnd.choice([None, {"color_transfer": "smpte2084"}, {"color_transfer": "arib-std-b67"},
                         {"color_transfer": "smpte2084", "side_data_list": [{"side_data_type": "DOVI configuration record"}]}])
        name = rnd.choice(STEMS)
        parts = rnd.sample(["res", "hdr", "vcodec", "acodec"], rnd.randint(0, 4))
        tags.append([[j, vs, name, parts], mux.codec_tag(j, vs, name, j["tracks"][1], parts)])
    data["codec_tag"] = tags
    OUT.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8", newline="")
    print(f"Đã ghi {OUT} ({OUT.stat().st_size // 1024} KB): {len(data['plans'])} kế hoạch, {len(data['donor'])} ca ghép audio")


if __name__ == "__main__":
    main()
