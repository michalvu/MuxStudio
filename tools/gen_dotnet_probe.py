"""Xuất dữ liệu đối chiếu cho bản .NET: ``core/tools.py`` + ``core/probe.py`` + vài hành vi thư viện chuẩn Python mà hai
module này dựa vào (``os.path.join``, ``pathlib``, ``round(x, n)``, ``str.splitlines``, ``str.strip``).

Ghi ra ``dotnet/tests/MuxStudio.Core.Tests/Data/python_probe_cases.json``: ``{nhóm: [[đầu vào, kết quả], ...]}``.
Test C# (``ProbeTests.Same_as_python``, ``ToolsTests.Same_as_python``) chạy cùng đầu vào và so từng kết quả.

Chạy lại sau khi sửa ``core/tools.py`` / ``core/probe.py``:  ``python tools/gen_dotnet_probe.py``
"""
from __future__ import annotations

import json
import ntpath
import random
import sys
from pathlib import Path, PureWindowsPath

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from core import probe, tools  # noqa: E402

OUT = ROOT / "dotnet" / "tests" / "MuxStudio.Core.Tests" / "Data" / "python_probe_cases.json"

# Tên track: chỉ chữ có sẵn dạng dựng sẵn (NFC). Dấu tổ hợp rời / ①² / dấu nối khác '_' cho \b khác nhau giữa Python và
# .NET (đã ghi trong docs/HANDOFF_DOTNET.md), nên không đưa vào đây.
NAME_PARTS = ["Forced", "forced", "FORCED", "SDH", "sdh", "English SDH", "English (SDH)", "[SDH]", "Sdhx", "SDH_", "xSDH",
              "ÄSDH", "SDHé", "hearing impaired", "Hearing", "Commentary", "Director's Commentary", "Directors track",
              "director track", "Director's Track", "comments", "Comment", "Filmmaker", "filmmakers", "Isolated Score",
              "Binh luan", "Bình luận", "Audio Description", "descriptive", "Descriptive Audio", "Visually Impaired",
              "Narration for the blind", "Tiếng Việt", "Čeština", "commentaryX", "English", "Signs & Songs", "-", " "]
CODEC_TEXTS = ["AC-3", "E-AC-3", "EAC3", "AC3", "DTS-HD Master Audio", "DTS-HD High Resolution Audio", "DTS-ES", "DTS",
               "dts", "TrueHD Atmos", "TrueHD", "MLP FBA", "AAC", "HE-AAC", "FLAC", "Opus", "Vorbis", "MP3",
               "MPEG-1 Audio layer 3", "MP2", "PCM", "ALAC", "Lạ", "", None, "A DTS", "xDTS", "dts-hd master"]
CODEC_IDS = ["A_AC3", "A_EAC3", "A_DTS", "A_TRUEHD", "A_MLP", "A_AAC", "A_AAC/MPEG4/LC", "A_FLAC", "A_OPUS", "A_VORBIS",
             "A_MPEG/L3", "A_MPEG/L2", "A_PCM/INT/LIT", "A_ALAC", "A_LAMMAS", "a_eac3", "X_EAC3", "", None]
SUB_CODECS = ["S_TEXT/UTF8", "S_TEXT/ASS", "S_TEXT/SSA", "S_TEXT/WEBVTT", "S_HDMV/PGS", "S_VOBSUB", "S_DVBSUB",
              "S_HDMV/TEXTST", "s_text/utf8", "S_TEXT/USF", "", None, "PGS", "vobsub"]
LANGS = [("eng", "en"), ("cze", "cs"), ("ces", "cs-CZ"), ("vie", "vi"), ("vi", None), ("ger", "de"), ("und", None),
         (None, None), ("zh", "zh-Hans"), ("chi", "zh-Hant"), ("fre", "fr-CA"), ("xyz", None), ("", ""), ("ENG", "EN")]
PRIORITIES = [["en", "vi", "cs"], ["cs", "en"], ["vi"], ["de", "en"], ["xyz"], [], ["und"], ["zh", "en"]]


def tool_cases():
    """tool_info / mkvmerge_version: tools.run được thay bằng kết quả cho sẵn (code, stdout, stderr)."""
    outs = [
        (0, "mkvmerge v82.0 ('I'm The President') 64-bit\n", ""),
        (0, "mkvmerge v9.8.0 ('Kingdom') 64bit\n", ""),
        (1, "mkvmerge v74.0.0 ('You Oughta Know') 64-bit\n", "Warning: something\n"),
        (0, "mkvextract v82.0 ('I'm The President') 64-bit\r\n", ""),
        (0, "ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024 the FFmpeg developers\n"
            "built with gcc 14.2.0 (Rev1, Built by MSYS2 project)\n", ""),
        (0, "ffprobe version 7.1-full_build-www.gyan.dev Copyright (c) 2007-2024 the FFmpeg developers\n", ""),
        (0, "ffprobe version N-117771-g2cdb5e7c6d-20241201-a-very-long-build-name Copyright (c) 2007\n", ""),
        (0, "alass 2.0.0 (giả)\nUSAGE: alass [OPTIONS] <REFERENCE_FILE> <INCORRECT_SUB_FILE> <OUTPUT_FILE>\n", ""),
        (0, "alass-cli 2.0.0\nUSAGE: alass-cli [FLAGS]\n", ""),
        (0, "ALASS v1.2\n", ""),
        (2, "", "error: Found argument '--version' which wasn't expected\nalass-cli 2.0.0 Daniel\n"),
        (0, "USAGE: alass [OPTIONS]\n", ""),
        (127, "", "[WinError 2] Systém nemůže nalézt uvedený soubor"),
        (0, "", ""),
        (0, "   \n\t ", ""),
        (3221225781, "", ""),
        (0, "Chương trình lạ: một dòng rất dài để thử cắt bớt sáu mươi ký tự đầu tiên của câu trả lời Copyright x\n", ""),
        (0, "\x1c\x1dmkvmerge v1.2\x1f", ""),
        (0, "Hello\u2028mkvmerge v3.4\n", ""),
        (-9, "mkvmerge v5.", ""),
        (0, "v12.mkvmerge", ""),
    ]
    real_run = tools.run
    info, ver = [], []
    try:
        for code, so, se in outs:
            tools.run = lambda *a, _r=tools.RunResult(code, so, se), **k: _r  # noqa: E731
            for name in ("mkvmerge", "mkvextract", "ffmpeg", "ffprobe", "alass", "lạ"):
                i = tools.tool_info(name, ["x", "y"])
                info.append([[name, code, so, se], [i.name, i.path, i.ok, i.version, i.problem, i.text]])
            v = tools.mkvmerge_version("x")
            ver.append([[code, so, se], [v.ok, v.major, v.text]])
    finally:
        tools.run = real_run
    return info, ver


def alass_cases():
    helps = ["USAGE\n --no-split\n --Disable-FPS-Guessing", "", "alass 2.0.0\n    --split-penalty <n>\n    --no-split\n",
             "--NO-SPLIT --SPLIT-PENALTY", "chỉ có --speed-optimization"]
    wanted = [["--no-split", "--split-penalty", "7", "--disable-fps-guessing"], ["--a", "1"], [], ["7", "--no-split"],
              ["--split-penalty", "7", "8", "--no-split", "x"], ["--speed-optimization", "1", "--Split-Penalty", "3"]]
    out = []
    for h in helps:
        for w in wanted:
            f = tools.alass_supported_flags(["x"], w, help_text=h)
            out.append([[h, w], [f.ok, f.unsupported, f.help_read]])
    return out


def mkv(*tracks, dur=None):
    j = {"tracks": list(tracks)}
    if dur is not None:
        j["container"] = {"properties": {"duration": dur}}
    return j


def main() -> None:
    rnd = random.Random(20261113)
    data: dict[str, list] = {}

    # ---------------------------------------------------------------- thu vien chuan
    paths = ["C:/x/lib/", "C:\\x\\lib\\.\\", "C:\\x\\\\lib", "\\\\srv\\share\\a\\", "\\\\srv\\share\\", "\\\\srv\\share",
             "C:\\", "C:", "C:x\\", "a/b", ".", ".\\a", "a\\..\\b", "C:\\x\\ ", "C:\\x\\lib.", "C:\\[YTS.MX] Phim (2020)\\",
             "\\x\\y\\", "/", "C:/", "//srv/share/x/./y//", "a\\.\\.\\b\\"]
    data["pure_path"] = [[p, [str(PureWindowsPath(p)), str(Path(p) / "f.mkv")]] for p in paths]
    nums = [23.9765, 0.0625, 2.675, 1.0005, 2.0005, 25.0005, 0.0005, 1e-7, 123456.0005, -0.0625, -1.0005, 1e300, 5e-324,
            1e9 / 41708333, 1e9 / 41666667, 1e9 / 40000000, 1e9 / 33366667, 24000 / 1001, 30000 / 1001, 59.94005994005994]
    nums += [1e9 / rnd.randint(10_000_000, 50_000_000) for _ in range(300)]
    nums += [rnd.randint(0, 10_000_000) / 2 ** rnd.randint(1, 20) for _ in range(300)]  # nhieu so dung giua (x.5 o hang 3)
    data["round"] = [[[x, n], round(x, n)] for x in nums for n in (0, 1, 2, 3, 6)]
    lines = ["", "a", "a\n", "a\r\nb", "a\rb\n\nc", "\nx", "ab\x1cc\x1dd\x1ee\x1ff", "a\x85b\u2028c\u2029d\x0be\x0cf", "a\n\r",
             "mkvmerge v82.0 ('x') 64-bit\r\n"]
    data["splitlines"] = [[s, s.splitlines()] for s in lines]
    strips = ["  a  ", "\x1c\x1dx\x1e\x1f", "\u3000x\u00a0", "\t\r\nx y\n", "", "   ", "\u200bx\u200b", "\x85x\u2028"]
    data["strip"] = [[s, s.strip()] for s in strips]
    jl = ["", "C:", "C:\\", "C:\\a", "C:\\a\\", "c:/a/", "C:a", "D:", "D:\\b", "d:x", "\\x", "/x", "a", "a/", "a\\b",
          "\\\\srv\\share", "\\\\srv\\share\\d", "\\\\?\\UNC\\srv\\share\\e", "\\\\.\\dev", "\\\\srv"]
    data["join"] = [[[a, b], ntpath.join(a, b)] for a in jl for b in jl]
    data["splitroot"] = [[a, list(ntpath.splitroot(a))] for a in jl]

    # ---------------------------------------------------------------- tools
    data["tool_info"], data["mkvmerge_version"] = tool_cases()
    data["alass_flags"] = alass_cases()
    data["join_safe"] = [[[b, c], tools.join_safe(b, c)] for b in [None, "", "  ", "C:\\a", "C:\\a\\", "C:", "a/b/", "\\\\s\\h"]
                         for c in ["b", "Desktop\\alass", "x y"]]
    data["flag_names"] = [[m, list(tools.mkvmerge_flag_names(m))] for m in (0, 50, 73, 74, 82)]

    # ---------------------------------------------------------------- probe: nhan dien track
    names = list(NAME_PARTS) + [" ".join(rnd.sample(NAME_PARTS, rnd.randint(2, 3))) for _ in range(150)]
    flags = [{}, {"forced_track": True}, {"forced_track": 1}, {"flag_forced": True}, {"flag_hearing_impaired": True},
             {"flag_hearing_impaired": "true"}, {"flag_commentary": True}, {"flag_visual_impaired": True},
             {"track_name": None}, {"track_name": 0}, {"track_name": 12}]
    props = [{"track_name": n} for n in names] + flags
    data["track_flags"] = [[p, [probe.is_forced(p), probe.is_sdh(p), probe.is_commentary(p), probe.is_descriptive(p)]]
                           for p in props]
    data["sub_codec"] = [[c, [probe.is_image_sub(c), probe.sub_ext_by_codec(c), probe.sub_ext_by_codec(c, False)]]
                         for c in SUB_CODECS]

    # ---------------------------------------------------------------- probe: audio
    aud = []
    for c in CODEC_TEXTS:
        for cid in rnd.sample(CODEC_IDS, 4):
            for ch in rnd.sample([1, 2, 6, 8, 0, None, "6", 5.9, "x"], 2):
                t = {"id": rnd.randint(0, 5), "type": "audio", "properties": {"codec_id": cid, "audio_channels": ch}}
                if c is not None:
                    t["codec"] = c
                if rnd.random() < 0.2:
                    t["properties"]["codec"] = rnd.choice(CODEC_TEXTS)
                if rnd.random() < 0.25:
                    t["properties"][rnd.choice(["tag_bps", "tag_bps_eng"])] = rnd.choice(["640000", "0", "abc", 448000, "1,5"])
                aud.append(t)
    aud += [{"properties": {"codec_id": "A_PCM/INT/LIT"}}, {"properties": None}, {}, {"codec": "", "properties": {}}]
    ffmaps = [[], [[1, 384000.0], [3, 0.0]], [[0, 96000.0], [2, 1536000.0], [4, 640000.0]]]
    data["audio"] = []
    for t in aud:
        ffm = rnd.choice(ffmaps)
        bps, src = probe.audio_bitrate(t, {k: v for k, v in ffm})
        data["audio"].append([[t, ffm], [probe.audio_codec_name(t), bps, src]])
    data["channel_name"] = [[n, probe.channel_name(n)] for n in
                            [1, 2, 3, 4, 5, 6, 7, 8, 9, 0, -1, None, "", "6", "6.0", "5,1", 5.9, 6.0, "abc", True, False, 16]]
    streams = [
        [{"index": 1, "bit_rate": "448000"}, {"index": 2, "tags": {"BPS-eng": "96000"}}],
        [{"index": 0, "bit_rate": "0", "tags": {"BPS": "abc", "BPS-en": "1536000"}}, {"bit_rate": "640000"},
         {"index": "3", "bit_rate": 192000}, {"index": 4, "bit_rate": "N/A", "tags": {"BPS": "0"}},
         {"index": 5, "bit_rate": "1,5"}],
        [], [{"index": 7, "tags": None}, {"index": 8, "tags": {"BPS-eng": "640000", "BPS": "320000"}}],
    ]
    data["ffprobe_bitrates"] = [[s, [[k, v] for k, v in probe.ffprobe_bitrates(s).items()]] for s in streams]
    data["format_kbps"] = [[b, probe.format_kbps(b)] for b in
                           [None, 0, 0.0, -5.0, 640000.0, 1234567.89, 2500.0, 3500.0, 1536000, 499.0, 500.0, 1500.0, 999_999_999.0]]
    astreams = [
        [{"codec_name": "eac3", "channels": 6, "tags": {"language": "eng", "title": "Surround 5.1"}, "disposition": {"default": 1}},
         {"codec_name": "aac", "channels": 2, "tags": {"language": "cze"}, "disposition": {"default": 0}},
         {"codec_name": "dts", "tags": {}, "disposition": {"default": "1"}},
         {"codec_name": None, "channels": None, "tags": None, "disposition": None},
         {"channels": "8", "disposition": {"default": 1.0}, "tags": {"language": "", "title": ""}}],
        [],
    ]
    data["audio_streams"] = [[s, [[a.index, a.lang, a.codec, a.channels, a.title, a.default, a.describe()]
                                  for a in probe.audio_streams(s)]] for s in astreams]

    # ---------------------------------------------------------------- probe: video
    dims = ["3840x2160", "2560x1440", "1920x1080", "1920x800", "1280x720", "720x576", "720x480", "x", "", None, "1920x1080\n",
            "1920X1080", "0x0", "4096x2160", "1440x1000", "1920x0599", "1920x99999999999999999999", 1080]
    data["resolution"] = [[d, probe.resolution_name(d)] for d in dims]
    vc = [("V_MPEGH/ISO/HEVC", None), ("V_MPEG4/ISO/AVC", None), ("V_AV1", None), ("V_VP9", None), ("V_MPEG2", None),
          ("V_THEORA", None), (None, "HEVC/H.265/MPEG-H"), (None, "AVC/H.264/MPEG-4p10"), ("V_MS/VFW/FOURCC", "H265"),
          ("", "mpeg-2"), (None, None), ("V_MPEG1", "MPEG-1"), ("v_av1", "x")]
    data["video_codec"] = [[[a, b], probe.video_codec_name(a, b)] for a, b in vc]
    hdr_names = ["Movie.2160p.DV.HEVC", "Movie.2160p.DoVi.HDR10+", "Movie.HDR.2160p", "Movie.HLG", "Movie.Dolby.Vision",
                 "Movie.1080p", "Movie.2160p.DV.HDR10.HEVC", "Movie.HDR10.1080p", "Movie.HDR10P", "Movie.DolbyVision",
                 "Movie.Dolby Vision.HDR10+", "MovieDV", "Movie.DVD", "Movie.hdr10+.hlg", "Movie.HDRip", "x"]
    vstreams = [None, {}, {"color_transfer": "smpte2084"}, {"color_transfer": "arib-std-b67"}, {"color_transfer": "bt709"},
                {"color_transfer": "smpte2084", "side_data_list": [{"side_data_type": "DOVI configuration record"}]},
                {"color_transfer": "smpte2084",
                 "side_data_list": [{"side_data_type": "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)"}]},
                {"side_data_list": [None, {"side_data_type": "Dolby Vision RPU"}, {"side_data_type": None}]},
                {"color_transfer": None, "side_data_list": None}]
    data["dynamic_range"] = [[[v, n], probe.dynamic_range(v, n)] for v in vstreams for n in rnd.sample(hdr_names, 6)]
    dds = [41708333, 41666667, 40000000, 33366667, 16683333, 20854166, 41708333.0, "41708333", "41,708333", 0, -5, None,
           "abc", 1, 10 ** 12, 0.001]
    data["fps"] = []
    for dd in dds:
        j = mkv({"id": 0, "type": "video", "properties": {"default_duration": dd}}, dur=rnd.choice([5_400_000_000_000, 0, None, "123"]))
        data["fps"].append([j, [probe.video_fps(j), probe.video_fps_exact(j), probe.duration(j)]])
    odd = [None, {}, {"tracks": None}, {"tracks": [{"type": "video", "properties": None}, {"type": 5}, {"id": 1}]},
           {"tracks": [], "container": None}, {"container": {"properties": {"duration": 1}}},
           {"tracks": [{"type": "video"}, {"type": "video", "properties": {"default_duration": 41708333}}]}]
    for j in odd:
        data["fps"].append([j, [probe.video_fps(j), probe.video_fps_exact(j), probe.duration(j)]])
    data["summary"] = [[j, list(probe.summarize_tracks(j).values())] for j in odd + [d[0] for d in data["fps"][:3]]]

    # ---------------------------------------------------------------- probe: chon track phu de
    picks, refs = [], []
    for _ in range(120):
        subs = []
        for i in range(rnd.randint(0, 6)):
            lang, ietf = rnd.choice(LANGS)
            p = {"codec_id": rnd.choice(SUB_CODECS[:8]), "language": lang, "language_ietf": ietf}
            if rnd.random() < 0.4:
                p["track_name"] = rnd.choice(NAME_PARTS)
            if rnd.random() < 0.15:
                p[rnd.choice(["forced_track", "flag_forced", "flag_hearing_impaired"])] = True
            subs.append({"id": i + 1, "type": "subtitles", "properties": p})
        j = mkv({"id": 0, "type": "video", "properties": {}}, *subs)
        for pr in rnd.sample(PRIORITIES, 2):
            for pgs in (True, False):
                t = probe.pick_ref_sub_track(j, pr, pgs)
                picks.append([[j, pr, pgs], None if t is None else [t.id, t.ext, t.sdh, t.lang, t.name, t.is_pgs, t.describe()]])
        refs.append([j, [[s.id, s.code, s.ext, s.rank] for s in probe.ref_sub_tracks(j)]])
    data["pick_ref"] = picks
    data["ref_sub_tracks"] = refs
    data["track_lang"] = [[{"properties": {"language": a, "language_ietf": b}}, probe.track_lang({"properties": {"language": a, "language_ietf": b}})]
                          for a, b in LANGS] + [[{}, probe.track_lang({})]]

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(data, ensure_ascii=False, indent=0), encoding="utf-8", newline="")
    print(f"{sum(len(v) for v in data.values())} ca trong {len(data)} nhóm -> {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
