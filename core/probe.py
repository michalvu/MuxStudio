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


def summarize_tracks(j: dict | None) -> dict[str, Any]:
    """Tóm tắt để hiển thị trên bảng quét."""
    return {
        "video": len(tracks(j, "video")),
        "audio": len(tracks(j, "audio")),
        "subtitles": len(tracks(j, "subtitles")),
        "fps": video_fps(j),
        "duration": duration(j),
    }
