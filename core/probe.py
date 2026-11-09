"""Đọc thông tin file phim: ``mkvmerge -J``, FPS, thời lượng, nhận diện track phụ đề.

Giai đoạn 2 mới có phần bước ① cần; phần audio/HDR/thẻ codec (bước ③) thêm ở Giai đoạn 4.

Thay: ``Get-VideoFps`` (SF), ``Get-VideoFpsOf``/``Get-DurationOf`` (MT), ``Get-VideoDuration`` (SP),
``Get-SubExtByCodec`` (SP), phần chọn track trong ``Get-RefTimes`` (SF) và ``Invoke-ExtractRefSubs``
(SP — bản được chọn, docs/SPEC.md 3.7), các kiểm tra Forced/SDH viết tay ở cả ba file.
"""
from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from typing import Any, Sequence

from . import naming, tools
from .timing import to_double


def mkv_json(path: str | os.PathLike[str], mkvmerge: str | None, job: tools.Job | None = None,
             cache: dict | None = None) -> dict | None:
    """``mkvmerge -J``. Mã thoát ≤ 1 (1 = có cảnh báo) là đọc được. Có bộ nhớ đệm theo lượt chạy."""
    if not mkvmerge:
        return None
    key = os.path.normcase(os.path.abspath(os.fspath(path)))
    if cache is not None and key in cache:
        return cache[key]
    r = tools.run([mkvmerge, "-J", os.fspath(path)], job, timeout=120)
    j = None
    if r.code <= 1:
        try:
            j = json.loads(r.stdout)
        except ValueError:
            j = None
    if cache is not None:
        cache[key] = j
    return j


def tracks(j: dict | None, kind: str) -> list[dict]:
    if not j:
        return []
    return [t for t in j.get("tracks") or [] if t.get("type") == kind]


def video_fps_exact(j: dict | None) -> float | None:
    """FPS = 1e9 / ``default_duration`` (nano giây/khung hình) của track video đầu tiên có giá trị."""
    for t in tracks(j, "video"):
        dd = to_double((t.get("properties") or {}).get("default_duration"))
        if dd > 0:
            return 1_000_000_000.0 / dd
    return None


def video_fps(j: dict | None) -> float | None:
    """Như ``Get-VideoFps`` (SubFetch): làm tròn 3 chữ số — dùng để so hạng FPS."""
    f = video_fps_exact(j)
    return round(f, 3) if f else None


def duration(j: dict | None) -> float:
    """Thời lượng (giây) từ ``container.properties.duration`` (nano giây); không có → 0."""
    if not j:
        return 0.0
    d = ((j.get("container") or {}).get("properties") or {}).get("duration")
    return to_double(d) / 1_000_000_000.0 if d else 0.0


# ---------------------------------------------------------------- nhan dien track

def _name(props: dict) -> str:
    return str(props.get("track_name") or "")


def is_forced(props: dict) -> bool:
    """Forced: tên có 'forced' hoặc cờ Matroska."""
    return bool(re.search(r"(?i)forced", _name(props))) or props.get("forced_track") is True or props.get("flag_forced") is True


def is_sdh(props: dict) -> bool:
    """SDH / khiếm thính: tên ``\\bSDH\\b`` hoặc 'hearing', hoặc cờ Matroska."""
    return bool(re.search(r"(?i)\bSDH\b|hearing", _name(props))) or props.get("flag_hearing_impaired") is True


def is_commentary(props: dict) -> bool:
    """``Test-CommentaryTrack``."""
    if props.get("flag_commentary") is True:
        return True
    nm = _name(props)
    return bool(nm) and bool(re.search(r"(?i)\b(commentary|comments?|director'?s? track|filmmaker|isolated score|binh luan)\b", nm))


def is_image_sub(codec_id: str | None) -> bool:
    """``Test-ImageSub``: PGS, VobSub, DVB."""
    return bool(re.search(r"(?i)S_HDMV/PGS|S_VOBSUB|S_DVBSUB|PGS|VOBSUB", codec_id or ""))


def sub_ext_by_codec(codec_id: str | None, allow_pgs: bool = True) -> str | None:
    """``Get-SubExtByCodec``: phụ đề văn bản dùng trực tiếp; PGS đọc mốc thời gian (nếu bật)."""
    c = codec_id or ""
    if "S_TEXT/UTF8" in c:
        return ".srt"
    if "S_TEXT/ASS" in c:
        return ".ass"
    if "S_TEXT/SSA" in c:
        return ".ssa"
    if "S_TEXT/WEBVTT" in c:
        return ".vtt"
    if allow_pgs and "S_HDMV/PGS" in c:
        return ".sup"
    return None


@dataclass(frozen=True)
class RefTrack:
    id: int
    ext: str
    sdh: bool
    lang: str  # chuoi "language language_ietf" (de hien thi)
    name: str
    is_pgs: bool

    def describe(self) -> str:
        kind = "PGS" if self.is_pgs else "văn bản"
        extra = " [SDH — không có bản thường]" if self.sdh else ""
        return f"track #{self.id} ({kind}, {self.lang.strip() or 'und'}){extra}"


def pick_ref_sub_track(j: dict | None, lang_priority: Sequence[str], allow_pgs: bool = True) -> RefTrack | None:
    """Chọn track phụ đề làm MỐC thời gian (bản ``Invoke-ExtractRefSubs`` của SubPrep).

    Bỏ Forced (quá ít dòng). Điểm càng thấp càng tốt: thứ tự ngôn ngữ × 10, SDH +5, PGS +1000
    (PGS chỉ dùng khi không còn lựa chọn nào). Bằng điểm → track đứng trước.
    """
    best: RefTrack | None = None
    best_score = 10 ** 9
    for t in tracks(j, "subtitles"):
        p = t.get("properties") or {}
        ext = sub_ext_by_codec(p.get("codec_id"), allow_pgs)
        if not ext:
            continue  # VobSub / DVB -> khong ho tro
        if is_forced(p):
            continue
        is_pgs = ext == ".sup"
        sdh = is_sdh(p)
        lang = f"{p.get('language') or ''} {p.get('language_ietf') or ''}".lower()
        li = 99
        for i, want in enumerate(lang_priority):
            if naming.lang_match(lang, want):
                li = i
                break
        score = li * 10 + (5 if sdh else 0) + (1000 if is_pgs else 0)
        if score < best_score:
            best_score = score
            best = RefTrack(int(t.get("id", 0)), ext, sdh, lang, _name(p), is_pgs)
    return best


# ---------------------------------------------------------------- ffprobe

def ffprobe_streams(path: str | os.PathLike[str], ffprobe: str | None, kind: str = "a",
                    job: tools.Job | None = None, cache: dict | None = None) -> list[dict]:
    """``Get-AudioStreams`` (SP): ``ffprobe -show_streams -select_streams <kind>``. Thứ tự = số thứ tự
    tương đối (``0:a:N`` của ffmpeg). Không đọc được → ``[]``. Có bộ nhớ đệm theo lượt chạy."""
    if not ffprobe:
        return []
    key = (os.path.normcase(os.path.abspath(os.fspath(path))), kind)
    if cache is not None and key in cache:
        return cache[key]
    r = tools.run([ffprobe, "-v", "quiet", "-print_format", "json", "-show_streams", "-select_streams", kind,
                   os.fspath(path)], job, timeout=120)
    out: list[dict] = []
    if r.code == 0 and r.stdout.strip():
        try:
            out = list(json.loads(r.stdout).get("streams") or [])
        except ValueError:
            out = []
    if cache is not None:
        cache[key] = out
    return out


@dataclass(frozen=True)
class AudioStream:
    """Một audio track (để hiện bảng ``Show-AudioStreams`` và chọn mốc đồng bộ)."""

    index: int  # so thu tu tuong doi, dem tu 0 (0:a:N)
    lang: str  # tag ngon ngu nguyen van ('und' neu khong co)
    codec: str
    channels: int
    title: str
    default: bool

    def describe(self) -> str:
        ch = f" {self.channels}ch" if self.channels else ""
        ti = f" - {self.title}" if self.title else ""
        return f"[{self.index}] {self.lang} {self.codec}{ch}{ti}" + (" (Default)" if self.default else "")


def audio_streams(streams: Sequence[dict]) -> list[AudioStream]:
    out = []
    for i, s in enumerate(streams):
        tags = s.get("tags") or {}
        disp = s.get("disposition") or {}
        out.append(AudioStream(i, str(tags.get("language") or "und"), str(s.get("codec_name") or ""),
                               int(s.get("channels") or 0), str(tags.get("title") or ""),
                               int(to_double(disp.get("default"))) == 1))
    return out


# ==========================================================================
#  BƯỚC ③: audio, video, HDR (Get-CodecName, Get-ChannelName, Get-AudioBitrate, Get-FfprobeBitrates,
#  Get-VideoCodecName, Get-ResolutionName, Get-DynamicRange, Test-DescriptiveTrack, Get-RefSubTracks)
# ==========================================================================

# codec_id Matroska -> ten dep (khi mkvmerge khong ghi truong 'codec' cap track)
_AUDIO_ID_NAMES = (
    ("A_TRUEHD", "TrueHD"), ("A_MLP", "TrueHD"), ("A_EAC3", "E-AC-3"), ("A_AC3", "Dolby Digital"), ("A_DTS", "DTS"),
    ("A_AAC", "AAC"), ("A_FLAC", "FLAC"), ("A_OPUS", "Opus"), ("A_VORBIS", "Vorbis"), ("A_MPEG/L3", "MP3"),
    ("A_MPEG/L2", "MP2"), ("A_PCM", "PCM"), ("A_ALAC", "ALAC"),
)


def _codec_from_text(c: str) -> str | None:
    if re.search(r"(?i)TrueHD", c):
        return "TrueHD"
    if re.search(r"(?i)DTS-HD Master", c):
        return "DTS-HD MA"
    if re.search(r"(?i)DTS-HD High", c):
        return "DTS-HD HRA"
    if re.search(r"(?i)DTS-ES", c):
        return "DTS-ES"
    if re.search(r"(?i)^DTS", c):
        return "DTS"
    if re.search(r"(?i)E-AC-3|EAC3", c):
        return "E-AC-3"
    if re.search(r"(?i)AC-3|AC3", c):
        return "Dolby Digital"
    if re.search(r"(?i)AAC", c):
        return "AAC"
    if re.search(r"(?i)FLAC", c):
        return "FLAC"
    if re.search(r"(?i)Opus", c):
        return "Opus"
    if re.search(r"(?i)Vorbis", c):
        return "Vorbis"
    if re.search(r"(?i)MP3|MPEG-1 Audio layer", c):
        return "MP3"
    if re.search(r"(?i)PCM", c):
        return "PCM"
    return None


def audio_codec_name(track: dict) -> str:
    """``Get-CodecName``: TrueHD, DTS-HD MA/HRA, DTS-ES, DTS, E-AC-3, Dolby Digital, AAC, FLAC, Opus, Vorbis, MP3, PCM.

    Sửa lỗi .bat (TĐ-33): ``mkvmerge -J`` ghi ``codec`` ở cấp TRACK, không nằm trong ``properties`` — hàm
    cũ đọc ``properties.codec`` (luôn trống) nên luôn rơi về ``codec_id`` (``A_EAC3`` → ``EAC3``, mọi DTS
    → ``DTS``). Bản mới đọc ``codec`` của track, không có thì dịch ``codec_id`` qua bảng.
    """
    props = track.get("properties") or {}
    c = str(track.get("codec") or props.get("codec") or "")
    if c:
        return _codec_from_text(c) or c
    cid = str(props.get("codec_id") or "")
    for prefix, name in _AUDIO_ID_NAMES:
        if cid.upper().startswith(prefix):
            return name
    return re.sub(r"^A_", "", cid)


def channel_name(n: Any) -> str:
    """``Get-ChannelName``: 1→1.0, 2→2.0, 3→2.1, 6→5.1, 7→6.1, 8→7.1, khác → ``n.0``."""
    k = int(to_double(n)) if n not in (None, "") else 0
    if not k:
        return ""
    return {1: "1.0", 2: "2.0", 3: "2.1", 6: "5.1", 7: "6.1", 8: "7.1"}.get(k, f"{k}.0")


def is_descriptive(props: dict) -> bool:
    """``Test-DescriptiveTrack``: audio description cho người khiếm thị."""
    if props.get("flag_visual_impaired") is True:
        return True
    nm = _name(props)
    return bool(nm) and bool(re.search(r"(?i)\b(audio description|descriptive|narration for the blind|visually impaired)\b", nm))


_HEURISTIC_KBPS = {"PCM": 4608, "TrueHD": 4000, "DTS-HD MA": 3500, "DTS-HD HRA": 2000, "FLAC": 1400, "DTS-ES": 1509,
                   "DTS": 1509, "E-AC-3": 768, "Dolby Digital": 640, "AAC": 256, "Opus": 256, "Vorbis": 224, "MP3": 192}


def ffprobe_bitrates(streams: Sequence[dict]) -> dict[int, float]:
    """``Get-FfprobeBitrates``: chỉ số stream (= ID track của mkvmerge trong MKV) → bitrate (bit/s)."""
    out: dict[int, float] = {}
    for s in streams:
        br = to_double(s.get("bit_rate"))
        if br <= 0:
            tags = s.get("tags") or {}
            for tn in ("BPS", "BPS-eng", "BPS-en"):
                if to_double(tags.get(tn)) > 0:
                    br = to_double(tags.get(tn))
                    break
        if br > 0 and s.get("index") is not None:
            out[int(s["index"])] = br
    return out


def audio_bitrate(track: dict, ff_map: dict[int, float] | None = None) -> tuple[float, str]:
    """``Get-AudioBitrate``: 1) tag ``BPS`` của mkvmerge, 2) ffprobe, 3) ước lượng theo codec × kênh/6.
    Trả (bit/s, nguồn: ``tag`` | ``ffprobe`` | ``ước lượng``)."""
    p = track.get("properties") or {}
    for k in ("tag_bps", "tag_bps_eng"):
        v = to_double(p.get(k))
        if v > 0:
            return v, "tag"
    if ff_map:
        v = ff_map.get(int(track.get("id", -1)), 0.0)
        if v > 0:
            return float(v), "ffprobe"
    base = _HEURISTIC_KBPS.get(audio_codec_name(track), 300)
    ch = int(to_double(p.get("audio_channels"))) or 2
    return base * 1000.0 * (max(ch, 1) / 6.0), "ước lượng"


def format_kbps(bps: float | None) -> str:
    """``Format-Kbps``: ``640 kbps`` ('' nếu không có)."""
    if not bps or bps <= 0:
        return ""
    return f"{round(bps / 1000):,} kbps"


def video_codec_name(codec_id: str | None, codec: str | None = None) -> str:
    """``Get-VideoCodecName``: AV1, HEVC, H264, VP9, MPEG2."""
    s = f"{codec_id or ''} {codec or ''}"
    if re.search(r"(?i)AV1", s):
        return "AV1"
    if re.search(r"(?i)HEVC|H\.?265|MPEGH", s):
        return "HEVC"
    if re.search(r"(?i)AVC|H\.?264|MPEG4", s):
        return "H264"
    if re.search(r"(?i)VP9", s):
        return "VP9"
    if re.search(r"(?i)MPEG-?2", s):
        return "MPEG2"
    return ""


def resolution_name(dim: str | None) -> str:
    """``Get-ResolutionName`` theo chiều cao ``pixel_dimensions`` (``1920x1080``)."""
    m = re.fullmatch(r"(\d+)x(\d+)", str(dim or ""))
    if not m:
        return ""
    h = int(m.group(2))
    if h >= 2000:
        return "2160p"
    if h >= 1400:
        return "1440p"
    if h >= 900:
        return "1080p"
    if h >= 600:
        return "720p"
    if h >= 500:
        return "576p"
    return "480p"


def dynamic_range(video_stream: dict | None, orig_name: str) -> str:
    """``Get-DynamicRange``: ``DV``, ``HDR``, ``HDR10+``, ``HLG`` (vd ``DV HDR``) từ stream video của ffprobe
    (``color_transfer``, ``side_data_list``); thiếu thì đọc từ tên file gốc."""
    dv, hdr = False, ""
    v = video_stream or {}
    ct = str(v.get("color_transfer") or "")
    if re.search(r"(?i)smpte2084", ct):
        hdr = "HDR"
    elif re.search(r"(?i)arib-std-b67", ct):
        hdr = "HLG"
    for sd in v.get("side_data_list") or []:
        t = str((sd or {}).get("side_data_type") or "")
        if re.search(r"(?i)dolby vision|DOVI", t):
            dv = True
        if re.search(r"(?i)HDR Dynamic Metadata", t):
            hdr = "HDR10+"
    if not dv and re.search(r"(?i)\b(DV|DoVi|Dolby[\. ]?Vision)\b", orig_name):
        dv = True
    if not hdr:
        if re.search(r"(?i)\bHDR10\+|\bHDR10P\b", orig_name):
            hdr = "HDR10+"
        elif re.search(r"(?i)\bHDR(?:10)?\b", orig_name):  # TD-33: 'HDR10' cung la HDR (.bat bo sot)
            hdr = "HDR"
        elif re.search(r"(?i)\bHLG\b", orig_name):
            hdr = "HLG"
    return " ".join(x for x in ("DV" if dv else "", hdr) if x)


@dataclass(frozen=True)
class SubRef:
    """Track phụ đề dùng làm mốc khi đo audio ghép (``Get-RefSubTracks``)."""

    id: int
    code: str
    ext: str
    rank: int  # 0 van ban, 1 SDH, 2 PGS


def ref_sub_tracks(j: dict | None) -> list[SubRef]:
    """``Get-RefSubTracks``: bỏ Forced; hạng văn bản 0 / SDH 1 / PGS 2 (sắp xếp ổn định theo hạng)."""
    out = []
    for t in tracks(j, "subtitles"):
        p = t.get("properties") or {}
        cid = str(p.get("codec_id") or "")
        if "S_TEXT/UTF8" in cid:
            ext = ".srt"
        elif "S_TEXT/ASS" in cid:
            ext = ".ass"
        elif "S_TEXT/SSA" in cid:
            ext = ".ssa"
        elif "S_HDMV/PGS" in cid:
            ext = ".sup"
        else:
            continue
        if is_forced(p):
            continue
        rank = 2 if ext == ".sup" else 1 if is_sdh(p) else 0
        out.append(SubRef(int(t.get("id", 0)), naming.lang_from_track(p.get("language"), p.get("language_ietf")), ext, rank))
    return sorted(out, key=lambda s: s.rank)


def track_lang(track: dict) -> str:
    p = track.get("properties") or {}
    return naming.lang_from_track(p.get("language"), p.get("language_ietf"))


def summarize_tracks(j: dict | None) -> dict[str, Any]:
    """Tóm tắt để hiển thị trên bảng quét."""
    return {
        "video": len(tracks(j, "video")),
        "audio": len(tracks(j, "audio")),
        "subtitles": len(tracks(j, "subtitles")),
        "fps": video_fps(j),
        "duration": duration(j),
    }
