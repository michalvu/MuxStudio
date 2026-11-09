"""Ghép audio từ file phim khác (bước ③, §6.9) — vd lấy tiếng Séc từ một bản phim khác trong ``audio_nguon``.

Thay ``Get-DonorTmpDir``, ``Get-DonorFiles``, ``Find-DonorFile``, ``Get-TrackCueTimes``, ``Measure-DonorTiming``,
``Get-DonorPlan``, ``Remove-DonorTemp`` (MuxTool).

Cách làm:
1. Ghép tập với tập (``S16E643`` ↔ ``- 0643``), phim lẻ theo tên + năm.
2. Đo độ lệch + chênh FPS bằng PHỤ ĐỀ CHUNG của hai bản (ưu tiên tiếng Anh; dùng cả ``.en.srt`` cạnh phim; đọc được PGS):
   ``phim_của_bạn ≈ nguồn × scale + offset``, ngưỡng ``DonorMinVotes`` mốc.
3. Cùng FPS → mkvmerge ghép thẳng, KHÔNG encode lại, chỉ thêm ``--sync TID:ms``.
   Khác FPS → ffmpeg đổi tốc độ CHÍNH XÁC (``stretch_filter``, TĐ-34) rồi mới ghép (E-AC-3 640k/256k, ≤ 6 kênh).
4. Không đo được → chỉ ghép khi thời lượng hai bản chênh ≤ ``DonorMaxDurationDiff``; còn lại bỏ qua và báo.

Lập kế hoạch (``plan_donor``) chỉ đọc; ``prepare`` chạy ffmpeg ra thư mục tạm lúc thực thi.
"""
from __future__ import annotations

import os
import uuid
from dataclasses import dataclass, field
from fractions import Fraction
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from . import episodes, naming, pgs, probe, srt, timing, tools

DONOR_EXTS = (".mkv", ".mka", ".mp4", ".m4v", ".ts")


def donor_files(target_dir: Path, folder: str) -> list[Path]:
    """``Get-DonorFiles``: ``.mkv .mka .mp4 .m4v .ts`` trong thư mục nguồn (đệ quy)."""
    if not folder:
        return []
    return tools.walk_files(target_dir / folder, True, DONOR_EXTS)


def find_donor_file(video_stem: str, pool: Sequence[Path]) -> Path | None:
    """``Find-DonorFile``: phim bộ theo tập (``episodes.same_episode``); phim lẻ theo tên + năm; chỉ có MỘT file
    nguồn phim lẻ → lấy luôn."""
    if not pool:
        return None
    ep = episodes.parse_episode(video_stem)
    if ep is not None:
        for d in pool:
            if episodes.same_episode(ep, episodes.parse_episode(d.stem)):
                return d
        return None
    me = naming.split_release_name(video_stem)
    for d in pool:
        if episodes.parse_episode(d.stem) is not None:
            continue
        dn = naming.split_release_name(d.stem)
        if naming.norm_title(dn.title) != naming.norm_title(me.title):
            continue
        if me.year and dn.year and me.year != dn.year:
            continue
        return d
    if len(pool) == 1 and episodes.parse_episode(pool[0].stem) is None:
        return pool[0]
    return None


def external_ref_subs(video: Path) -> list[tuple[str, Path]]:
    """Phụ đề ngoài cạnh phim của bạn (đã đồng bộ đúng với phim) — mốc tốt khi đo audio ghép (``.en.srt``…)."""
    out = []
    pre = (video.stem + ".").lower()
    try:
        entries = sorted(os.scandir(video.parent), key=lambda e: e.name.lower())
    except OSError:
        return out
    for e in entries:
        if not e.is_file():
            continue
        p = Path(e.path)
        if p.suffix.lower() not in (".srt", ".ass", ".ssa") or not p.stem.lower().startswith(pre):
            continue
        suffix = p.stem[len(video.stem):]
        if "forced" in suffix.lower():
            continue
        code = naming.lang_of_suffix(suffix)  # TD-25: bang ngon ngu chung
        if code:
            out.append((code, p))
    return out


class TrackTimes:
    """``Get-TrackCueTimes``: trích track phụ đề (``mkvextract``; PGS → SRT mốc) → mốc thời gian. Có bộ nhớ đệm."""

    def __init__(self, mkvextract: str | None, temp: Path, job: tools.Job, placeholder: str = "*"):
        self.mkvextract = mkvextract
        self.temp = temp
        self.job = job
        self.placeholder = placeholder
        self._cache: dict[tuple[str, int], list[float] | None] = {}

    def __call__(self, file: Path, trk: probe.SubRef) -> list[float] | None:
        key = (os.path.normcase(os.path.abspath(file)), trk.id)
        if key in self._cache:
            return self._cache[key]
        self._cache[key] = None
        if not self.mkvextract:
            return None
        self.temp.mkdir(parents=True, exist_ok=True)
        raw = self.temp / f"s_{uuid.uuid4().hex[:8]}{trk.ext}"
        tools.run([self.mkvextract, "tracks", os.fspath(file), f"{trk.id}:{os.fspath(raw)}"], self.job)
        if not raw.is_file():
            return None
        path = raw
        if trk.ext == ".sup":
            path = raw.with_suffix(".srt")
            if pgs.pgs_to_timing_srt(raw, path, self.placeholder) == 0:
                return None
        t = srt.cue_times(path)
        self._cache[key] = t
        return t


@dataclass
class DonorTiming:
    scale: float
    offset: float  # giay: phim_cua_ban ≈ nguon * scale + offset
    votes: int
    name: str  # ten ty le FPS
    how: str  # cach do (hien tren bang)
    sure: bool
    close: bool = False  # khong do duoc nhung thoi luong khop


def measure_timing(video: Path, j: dict | None, donor: Path, dj: dict | None,
                   times: Callable[[Path, probe.SubRef], list[float] | None], min_votes: int = 20,
                   max_duration_diff: float = 1.0, n_ref: int = 400, n_tgt: int = 100,
                   ext_refs: Sequence[tuple[str, Path]] | None = None) -> DonorTiming:
    """``Measure-DonorTiming``: ngôn ngữ chung (ưu tiên ``en``); mốc phía bạn: sub trong MKV, rồi sub ngoài cạnh
    phim; ≥ ``min_votes`` mốc khớp. Không đo được → đoán theo FPS + thời lượng."""
    mine = probe.ref_sub_tracks(j)
    theirs = probe.ref_sub_tracks(dj)
    ext = list(ext_refs) if ext_refs is not None else external_ref_subs(video)
    langs = list(dict.fromkeys(c for c in ["en", *(t.code for t in theirs)] if c and c != "und"))
    for lg in langs:
        dt = next((t for t in theirs if t.code == lg), None)
        if dt is None:
            continue
        mine_times, src = None, ""
        mt = next((t for t in mine if t.code == lg), None)
        if mt is not None:
            mine_times = times(video, mt)
            src = f"sub {naming.lang_name(lg)} trong MKV"
        if not mine_times:
            er = next((p for c, p in ext if c == lg), None)
            if er is not None:
                mine_times = srt.cue_times(er)
                src = f"sub {naming.lang_name(lg)} ngoài"
        if not mine_times:
            continue
        donor_times = times(donor, dt)
        if not donor_times:
            continue
        rel = timing.timing_relation(mine_times, donor_times, n_ref, n_tgt)
        if rel and rel.votes >= min_votes:
            return DonorTiming(rel.scale, rel.offset, rel.votes, rel.name, f"đo bằng {src} ({rel.votes} mốc khớp)", True)
    fm, fd = probe.video_fps_exact(j) or 0.0, probe.video_fps_exact(dj) or 0.0
    scale = 1.0
    if fm > 0 and fd > 0 and abs(fm / fd - 1.0) > 0.001:
        scale = fd / fm
    dm = probe.duration(j)
    dd = probe.duration(dj) * scale
    diff = abs(dm - dd)
    close = dm > 0 and dd > 0 and diff <= max_duration_diff
    how = (f"ƯỚC TÍNH: không có phụ đề chung, thời lượng khớp (chênh {diff:.1f}s)" if close
           else f"không đo được (thời lượng chênh {diff:.1f}s)")
    name = next((fr.name for fr in timing.FPS_RATIOS if abs(fr.ratio - scale) < 1e-6), "")
    return DonorTiming(scale, 0.0, 0, name, how, False, close)


def manual_offset(video_stem: str, offsets: Mapping[str, Any]) -> float | None:
    """``DonorOffsetMs``: khoá = tên file, ``S01E03``, ``tap #643`` hoặc ``643`` (không phân biệt hoa thường)."""
    ep = episodes.parse_episode(video_stem)
    keys = {video_stem.lower()}
    if ep is not None:
        keys |= {ep.ref_label.lower(), str(ep.episode)}
    for k, v in offsets.items():
        if str(k).strip().lower() in keys:
            return timing.to_double(v)
    return None


@dataclass
class DonorTrack:
    """Một audio ghép thêm vào file của bạn."""

    source: Path
    tid: int  # ID track trong file nguon
    a_index: int  # thu tu audio trong file nguon (0:a:N cho ffmpeg)
    code: str
    name: str  # ten track
    offset_ms: int
    stretch: bool = False
    filter: str = ""  # asetrate=...,aresample=...,atempo=... (khi khac FPS)
    out_channels: int = 0
    bitrate: str = ""
    prepared: Path | None = None  # .mka tam sau ffmpeg (luc thuc thi)

    @property
    def input_path(self) -> Path:
        return self.prepared if self.stretch and self.prepared else self.source

    @property
    def input_tid(self) -> int:
        return 0 if self.stretch else self.tid

    def ffmpeg_args(self, ffmpeg: str, out: str | os.PathLike[str]) -> list[str]:
        return [ffmpeg, "-y", "-v", "error", "-i", os.fspath(self.source), "-map", f"0:a:{self.a_index}", "-vn", "-sn",
                "-dn", "-af", self.filter, "-ac", str(self.out_channels), "-c:a", "eac3", "-b:a", self.bitrate,
                os.fspath(out)]


@dataclass
class DonorPlan:
    donor: Path | None = None
    timing: DonorTiming | None = None
    tracks: list[DonorTrack] = field(default_factory=list)
    note: str = ""  # ly do khong ghep / mo ta (cot "Audio ghep")
    manual: bool = False

    @property
    def summary(self) -> str:
        if not self.tracks:
            return self.note
        t = self.tracks[0]
        sign = "+" if t.offset_ms >= 0 else ""
        langs = ", ".join(x.code for x in self.tracks)
        fps = " (đổi tốc độ)" if t.stretch else ""
        return f"{langs} lệch {sign}{t.offset_ms} ms{fps}"


def stretch_filter(sr: int, scale: float) -> str:
    """Bộ lọc ffmpeg đổi tốc độ audio sao cho thời lượng nhân đúng ``scale`` (PAL 25 → 23.976…).

    Sửa lỗi .bat (TĐ-34): chuỗi cũ ``asetrate=<số nguyên>,aresample=<sr>,atempo=<bù>`` — ``atempo`` làm audio SỚM
    ~21 ms cố định (đo bằng tiếng bíp) ⇒ không đạt "sai số < 20 ms". Bản mới viết ``scale`` thành phân số p/q
    (25/23.976 = 3125/2997), đổi tần số trung gian R = p·k gần ``sr`` rồi ``asetrate`` = q·k — số nguyên CHÍNH XÁC,
    không cần ``atempo``: ``aresample=R,asetrate=q·k,aresample=sr`` (lệch < 0,2 ms). Tỉ lệ không viết gọn được
    thành phân số → dùng lại chuỗi cũ.
    """
    fr = Fraction(scale).limit_denominator(100000)
    if abs(float(fr) - scale) <= 1e-9 * abs(scale):
        p, q = fr.numerator, fr.denominator
        k = max(1, round(sr / p))
        r = p * k
        if sr / 4 <= r <= sr * 4:
            return f"aresample={r},asetrate={q * k},aresample={sr}"
    n_rate = int(round(sr / scale))
    flt = f"asetrate={n_rate},aresample={sr}"
    fix = sr / (n_rate * scale)
    if abs(fix - 1.0) > 0.0000001:
        flt += f",atempo={timing.fmt_inv(fix, 10)}"
    return flt


def stretch_factor(flt: str, sr: int) -> float:
    """Hệ số thời lượng mà chuỗi lọc tạo ra (để kiểm chứng): ``aresample`` giữ thời lượng, ``asetrate=Y`` nhân
    thời lượng với tần số_hiện_tại/Y, ``atempo=t`` chia cho t."""
    rate, factor = float(sr), 1.0
    for part in flt.split(","):
        k, _, v = part.partition("=")
        x = timing.to_double(v)
        if k == "aresample":
            rate = x
        elif k == "asetrate":
            factor *= rate / x
            rate = x
        elif k == "atempo":
            factor /= x
    return factor


def plan_donor(video: Path, j: dict | None, have_langs: Sequence[str], pool: Sequence[Path], cfg: Mapping[str, Any],
               donor_json: Callable[[Path], dict | None], times: Callable[[Path, probe.SubRef], list[float] | None],
               ffmpeg_ok: bool, override_ms: float | None = None, job: tools.Job | None = None) -> DonorPlan:
    """``Get-DonorPlan`` — chỉ lập kế hoạch (không chạy ffmpeg). ``override_ms``: độ lệch bạn sửa tay trong bảng
    (chỉ cho lượt chạy này, ưu tiên hơn ``DonorOffsetMs``)."""
    job = job or tools.NULL_JOB
    plan = DonorPlan()
    if not cfg["UseDonorAudio"]:
        return plan
    langs = [str(x).lower() for x in cfg["DonorLangs"]]
    want = [lg for lg in langs if not (cfg["DonorSkipIfPresent"] and lg in have_langs)]
    if not want:
        if pool:
            plan.note = "file đã có " + ", ".join(naming.lang_name(x) for x in langs) + " → bỏ qua"
        return plan
    d = find_donor_file(video.stem, pool)
    if d is None:
        if pool:
            plan.note = f"không có file nguồn khớp với file này trong '{cfg['DonorFolder']}'"
        return plan
    plan.donor = d
    dj = donor_json(d)
    if not dj:
        plan.note = f"không đọc được {d.name}"
        return plan
    d_audio = probe.tracks(dj, "audio")
    picks: list[tuple[dict, str]] = []
    for lg in want:
        c = [t for t in d_audio if probe.track_lang(t) == lg and not probe.is_commentary(t.get("properties") or {})
             and not probe.is_descriptive(t.get("properties") or {})]
        if not c:
            continue
        # nhieu track cung ngon ngu -> nhieu kenh nhat, roi bitrate cao nhat
        best = sorted(c, key=lambda t: (-int(timing.to_double((t.get("properties") or {}).get("audio_channels"))),
                                        -probe.audio_bitrate(t)[0]))[0]
        picks.append((best, lg))
    if not picks:
        plan.note = f"{d.name} không có audio " + "/".join(naming.lang_name(x) for x in want)
        return plan
    tm = measure_timing(video, j, d, dj, times, int(cfg["DonorMinVotes"]), float(cfg["DonorMaxDurationDiff"]),
                        int(cfg["FpsSampleRef"]), int(cfg["FpsSampleTgt"]))
    manual = override_ms if override_ms is not None else manual_offset(video.stem, cfg["DonorOffsetMs"] or {})
    if manual is not None:
        tm.offset, tm.sure = manual / 1000.0, True
        tm.how = "chỉnh tay" + (" (trong bảng)" if override_ms is not None else " (DonorOffsetMs)")
        plan.manual = True
    plan.timing = tm
    if not tm.sure and not (tm.close and cfg["DonorAcceptDurationMatch"]):
        plan.note = (f"{tm.how} → KHÔNG ghép để tránh lệch tiếng. Thêm phụ đề (vd tiếng Anh) cho cả hai bản, "
                     "hoặc nhập độ lệch tay.")
        return plan
    off_ms = int(round(tm.offset * 1000))
    stretch = abs(tm.scale - 1.0) > 0.0002
    for t, lg in picks:
        p = t.get("properties") or {}
        label = " ".join(x for x in (naming.lang_name(lg), probe.audio_codec_name(t), probe.channel_name(p.get("audio_channels"))) if x)
        a_index = next((i for i, x in enumerate(d_audio) if int(x.get("id", -1)) == int(t.get("id", -2))), 0)
        dt = DonorTrack(d, int(t.get("id", 0)), a_index, lg, label, off_ms)
        if stretch:
            if not ffmpeg_ok:
                job.error(f"      [!] Cần ffmpeg để đổi tốc độ audio nhưng không tìm thấy → bỏ qua {naming.lang_name(lg)}.")
                continue
            sr = int(timing.to_double(p.get("audio_sampling_frequency"))) or 48000
            ch = int(timing.to_double(p.get("audio_channels"))) or 2
            dt.stretch = True
            dt.out_channels = min(ch, 6)
            dt.bitrate = "640k" if dt.out_channels >= 6 else "256k"
            dt.filter = stretch_filter(sr, tm.scale)
            dt.name = " ".join(x for x in (naming.lang_name(lg), "E-AC-3", probe.channel_name(dt.out_channels)) if x)
        plan.tracks.append(dt)
    if not plan.tracks and not plan.note:
        plan.note = "không ghép được (thiếu ffmpeg)"
    return plan


def prepare(track: DonorTrack, ffmpeg: str, temp: Path, job: tools.Job) -> bool:
    """Lúc thực thi: ffmpeg đổi tốc độ audio ra ``.mka`` tạm (chỉ khi khác FPS)."""
    if not track.stretch:
        return True
    temp.mkdir(parents=True, exist_ok=True)
    out = temp / f"a_{uuid.uuid4().hex[:8]}.mka"
    job.dim(f"      [~] ffmpeg đang đổi tốc độ {naming.lang_name(track.code)} ({track.filter})...")
    r = tools.run(track.ffmpeg_args(ffmpeg, out), job)
    if r.code != 0 or not out.is_file():
        job.error(f"      [!] ffmpeg thất bại → bỏ qua {naming.lang_name(track.code)}.")
        for ln in r.output.splitlines()[-3:]:
            job.dim(f"         {ln}")
        return False
    track.prepared = out
    return True
