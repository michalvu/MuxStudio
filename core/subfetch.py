"""Bước ① — Tải phụ đề (thay SubFetch_v30.bat). docs/SPEC.md mục 4.1, §6.11.

Hai bước (§4 "Lập kế hoạch rồi mới thực thi"):

1. **Lập kế hoạch** (``FetchSession.scan`` → ``gather`` → ``auto_select``/``choose``): KHÔNG ghi gì
   vào thư mục phim, KHÔNG tốn quota. Phụ đề gốc trích từ MKV nằm ở thư mục tạm; gói ZIP của
   SubSource (miễn phí) có thể tải trước vào thư mục tạm để đo thẳng từng tập.
2. **Thực thi** (``FetchSession.execute``): tải/chép đúng kế hoạch, ghi nhật ký hoàn tác
   ``_subfetch_logs\\actions_*.csv`` và báo cáo ``fetch_*.csv``.

Nguồn theo thứ tự: file có sẵn trong ``srt_can_sync`` → kho ``srt_pool`` → SubSource → OpenSubtitles.
Bước ① KHÔNG sửa thời gian phụ đề — chỉ đo và báo (việc nắn thời gian là của bước ②).
Phụ đề ASS giữ nguyên là ASS (Q7): đích là ``<tên phim>.<mã>.ass``.
"""
from __future__ import annotations

import csv
import datetime as _dt
import os
import re
import shutil
import tempfile
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from . import episodes, naming, opensubs, pgs, pool, probe, srt, subsource, timing, tools, undo
from .tmdb import TmdbClient

SUB_EXTS = (".srt", ".ass", ".ssa")
REF_EXTS = (".srt", ".ass", ".ssa")


# ==========================================================================
#  ỨNG VIÊN
# ==========================================================================


@dataclass(frozen=True)
class Offset:
    offset: float
    votes: int
    total: int  # > 0: uoc tinh tu preview (hien dau ~); -1: do that


@dataclass
class Candidate:
    """``New-Cand``. ``src`` ∈ ``SubSource`` | ``Kho`` | ``OpenSub``."""

    src: str
    tier: int  # 1 = FPS khop video, 2 = khong khai bao, 3 = lech
    fps: str
    score: float  # do giong ten ban release voi ten file phim
    release: str
    info: str
    payload: Any
    off: Offset | None = None
    machine: bool = False
    is_pack: bool = False

    @property
    def ident(self) -> tuple[str, str]:
        return (self.src, self.release)

    @property
    def fps_label(self) -> str:
        """Cột FPS của bảng ứng viên: KHỚP / không khai báo / LỆCH."""
        if self.tier == 1:
            return "đo được: KHỚP" if self.fps == "đo: khớp" else f"{self.fps} KHỚP"
        if self.tier == 2:
            return "đo: nghi ngờ" if self.fps == "đo: nghi ngờ" else "không khai báo"
        return "đo được: LỆCH" if self.fps == "lệch" else f"{self.fps} LỆCH"

    @property
    def offset_label(self) -> str:
        """Cột Lệch: ``+1.23s`` (đo thật), ``+1.23s~`` (ước tính từ preview), ``?`` (chưa đo được)."""
        if not self.off:
            return "?"
        sign = "+" if self.off.offset >= 0 else ""
        txt = f"{sign}{round(self.off.offset, 2)}s"
        return txt + "~" if self.off.total > 0 else txt


def fps_tier(declared: Any, video_fps: float | None) -> int:
    """``Get-FpsTier``: 1 = khai báo khớp video (chênh < 0,05), 2 = không khai báo, 3 = lệch."""
    if not video_fps:
        return 2
    f = timing.to_double(declared)
    if f <= 0:
        return 2
    return 1 if abs(f - video_fps) < 0.05 else 3


def sort_candidates(cands: list[Candidate]) -> list[Candidate]:
    """Thứ tự (§6.11): hạng FPS → đã đo được độ lệch → bản thường trước bản dịch máy (Q2) →
    |độ lệch| nhỏ → giống tên. KHÔNG cộng điểm lượt tải hay codec."""
    return sorted(cands, key=lambda c: (c.tier, 0 if c.off else 1, 1 if c.machine else 0,
                                        abs(c.off.offset) if c.off else 999.0, -c.score))


# ==========================================================================
#  KẾ HOẠCH
# ==========================================================================


@dataclass
class RefInfo:
    """Phụ đề gốc làm mốc thời gian."""

    times: list[float]
    path: Path
    source: str  # mo ta cho bang quet
    pending: tuple[Path, Path] | None = None  # (file tam, dich trong srt_goc) - ghi luc thuc thi


@dataclass
class ExistingMatch:
    """Phụ đề bạn tự bỏ vào ``srt_can_sync`` được nhận cho tập này."""

    file: Path
    votes: int  # -1 = khong do duoc
    name_score: float
    fps_bad: bool

    @property
    def reason(self) -> str:
        return f"{self.votes} mốc khớp" if self.votes >= 0 else f"tên giống {round(self.name_score, 2)}"


@dataclass
class VideoInfo:
    path: Path
    stem: str
    ep: episodes.EpisodeInfo | None
    title: str
    year: int | None
    fps: float | None
    tokens: list[str]
    ref: RefInfo | None = None
    entry: dict | None = None
    entry_checked: bool = False
    imdb: str | None = None
    mapped: tuple[int, int] | None = None  # so tap tuyet doi -> (season, tap) theo TMDB
    lang_reported: bool = False

    @property
    def kind_text(self) -> str:
        if self.ep is None:
            return "phim lẻ"
        return f"phim bộ {self.ep.label}"


@dataclass
class FetchItem:
    video: VideoInfo
    lang: str
    lang_dir: Path
    already: Path | None = None
    existing: ExistingMatch | None = None
    candidates: list[Candidate] = field(default_factory=list)
    gathered: bool = False
    chosen: Candidate | None = None
    choice_reason: str = ""
    skip: bool = False
    status: str = ""
    result: Path | None = None
    result_src: str = ""
    timing_note: str = ""

    @property
    def memory_key(self) -> str:
        e = self.video.entry
        base = str(e.get("movieId")) if e and e.get("movieId") is not None else self.video.stem
        return f"{base}|{self.lang}"

    def dest(self, ext: str = ".srt") -> Path:
        return self.lang_dir / f"{self.video.stem}.{self.lang}{ext.lower()}"

    @property
    def needs_choice(self) -> bool:
        return not (self.already or self.existing or self.skip or self.chosen) and bool(self.candidates)


@dataclass
class FetchReport:
    ok: int = 0
    used_existing: int = 0
    already: int = 0
    missing: int = 0
    skipped: int = 0
    os_remaining: int = -1
    os_allowed: int = -1
    os_reset: str = ""
    log_path: Path | None = None
    report_path: Path | None = None
    stopped: bool = False


# ==========================================================================
#  NHỚ LỰA CHỌN (Select-Candidate)
# ==========================================================================


class ChoiceMemory:
    """Ghi nhớ lựa chọn theo season + ngôn ngữ; KHÔNG dùng lại bản riêng của một tập khác."""

    def __init__(self, job: tools.Job | None = None):
        self._m: dict[str, Candidate] = {}
        self.job = job or tools.NULL_JOB

    def recall(self, key: str, video_name: str, cands: Sequence[Candidate]) -> Candidate | None:
        prev = self._m.get(key)
        if prev is None:
            return None
        if episodes.episode_conflict(video_name, prev.release):
            self.job.dim("  [i] Lựa chọn trước là bản riêng của tập khác → chọn lại cho tập này.")
            self._m.pop(key, None)
            return None
        for c in cands:
            if c.ident == prev.ident:
                self.job.info(f"  [i] Dùng lại lựa chọn trước: {prev.src} - {prev.release}")
                return c
        return None

    def remember(self, key: str, video_name: str, cand: Candidate) -> bool:
        if episodes.episode_conflict(video_name, cand.release):
            return False
        self._m[key] = cand
        return True

    def forget(self, key: str, release: str | None = None) -> None:
        if key in self._m and (release is None or self._m[key].release == release):
            del self._m[key]

    def get(self, key: str) -> Candidate | None:
        return self._m.get(key)


# ==========================================================================
#  PHỤ ĐỀ GỐC LÀM MỐC (Get-RefTimes + Find-RefInFolder)
# ==========================================================================


class RefProvider:
    def __init__(self, target_dir: Path, cfg: Mapping[str, Any], mkvmerge: str | None, mkvextract: str | None,
                 temp_dir: Path, job: tools.Job, json_cache: dict):
        self.td = target_dir
        self.cfg = cfg
        self.mkvmerge = mkvmerge
        self.mkvextract = mkvextract
        self.temp = temp_dir
        self.job = job
        self.json_cache = json_cache
        self._cache: dict[str, RefInfo | None] = {}

    @property
    def ex_dir(self) -> Path | None:
        if self.cfg["KeepExtractedRef"] and self.cfg["ExtractFolder"]:
            return self.td / self.cfg["ExtractFolder"]
        return None

    def get(self, video: Path, j: dict | None = None) -> RefInfo | None:
        key = os.path.normcase(os.path.abspath(video))
        if key not in self._cache:
            self._cache[key] = self._get(video, j)
        return self._cache[key]

    def _get(self, video: Path, j: dict | None) -> RefInfo | None:
        ex = self.ex_dir
        stem = video.stem
        exname = self.cfg["ExtractFolder"]
        # TD-08: srt_goc TRUOC, voi moi file phim (ca MKV khong co phu de, ca MP4)
        if ex is not None and ex.is_dir():
            for ext in REF_EXTS:
                p = ex / f"{stem}{ext}"
                if p.is_file():
                    t = srt.cue_times(p)
                    if t and len(t) > 10:
                        self.job.dim(f"    Mốc gốc: dùng lại {exname}\\{p.name} ({len(t)} dòng)")
                        return RefInfo(t, p, f"dùng lại {exname}\\{p.name}")
            found = self._find_in_folder(video, ex)
            if found is not None:
                return found
        if video.suffix.lower() != ".mkv" or not self.mkvmerge or not self.mkvextract:
            return None
        j = j or probe.mkv_json(video, self.mkvmerge, self.job, self.json_cache)
        trk = probe.pick_ref_sub_track(j, list(self.cfg["RefSubLangs"]), bool(self.cfg["PgsTimingFallback"]))
        if trk is None:
            return None
        self.temp.mkdir(parents=True, exist_ok=True)
        raw = self.temp / f"ref_{uuid.uuid4().hex[:8]}{trk.ext}"
        tools.run([self.mkvextract, "tracks", os.fspath(video), f"{trk.id}:{os.fspath(raw)}"], self.job, timeout=600)
        if not raw.is_file() or raw.stat().st_size <= 32:
            self.job.warn(f"    [!] Không trích được phụ đề gốc từ {video.name}")
            return None
        if trk.is_pgs:
            out = raw.with_suffix(".srt")
            n = pgs.pgs_to_timing_srt(raw, out, str(self.cfg["PgsTimingText"]))
            raw.unlink(missing_ok=True)
            if n == 0:
                self.job.warn(f"    [!] Không đọc được mốc thời gian từ PGS: {video.name}")
                return None
            path, final_name = out, f"{stem}.srt"
        else:
            path, final_name = raw, f"{stem}{trk.ext}"  # Q7: ASS giu la .ass
        t = srt.cue_times(path)
        if not t:
            return None
        pending = (path, ex / final_name) if ex is not None else None
        desc = f"trích mới từ MKV: {trk.describe()}" + (" — đọc mốc từ PGS" if trk.is_pgs else "")
        self.job.dim(f"    Mốc gốc: {trk.describe()}, {len(t)} dòng")
        return RefInfo(t, path, desc, pending)

    def _find_in_folder(self, video: Path, ex: Path) -> RefInfo | None:
        """``Find-RefInFolder``: file mốc trong ``srt_goc`` khi tên KHÔNG trùng tên phim.
        Ưu tiên đúng số tập; không có số tập thì so độ giống tên (≥ 0,35). Nhận cả ``.sup``."""
        cands = [f for f in tools.walk_files(ex, False, (*REF_EXTS, ".sup"))]
        cands = [f for f in cands if not episodes.episode_conflict(video.stem, f.stem)]
        if not cands:
            return None
        my_ep = episodes.parse_episode(video.stem)
        if my_ep is not None:
            exact = [f for f in cands if episodes.same_episode(my_ep, episodes.parse_episode(f.stem))]
            if exact:
                cands = exact
        vt = naming.title_tokens(video.stem)
        best, bs = None, -1.0
        for c in cands:
            sc = naming.title_score(vt, naming.title_tokens(c.stem)) + (1.0 if my_ep else 0.0)
            if sc > bs:
                best, bs = c, sc
        if best is None or bs < 0.35:
            return None
        exname = self.cfg["ExtractFolder"]
        pending = None
        path = best
        if best.suffix.lower() == ".sup":
            conv = best.with_suffix(".srt")
            if conv.is_file():
                path = conv
            else:
                self.temp.mkdir(parents=True, exist_ok=True)
                tmp = self.temp / f"pgs_{uuid.uuid4().hex[:8]}.srt"
                if pgs.pgs_to_timing_srt(best, tmp, str(self.cfg["PgsTimingText"])) == 0:
                    return None
                self.job.info(f"    [pgs] {best.name} → {conv.name}")
                path, pending = tmp, (tmp, conv)
        t = srt.cue_times(path)
        if not t or len(t) <= 10:
            return None
        self.job.dim(f"    Mốc gốc: {exname}\\{best.name} ({len(t)} dòng)")
        return RefInfo(t, path, f"{exname}\\{best.name} (tìm theo tập/tên)", pending)


# ==========================================================================
#  PHIÊN LÀM VIỆC BƯỚC ①
# ==========================================================================


class FetchSession:
    def __init__(self, target_dir: str | os.PathLike[str], cfg: Mapping[str, Any], secrets: Mapping[str, str],
                 job: tools.Job | None = None, http: Any = None, sleep: Callable[[float], None] = time.sleep,
                 tool_paths: Mapping[str, str | None] | None = None,
                 video_probe: Callable[[Path], dict | None] | None = None,
                 languages: Sequence[str] | None = None, sources: Mapping[str, bool] | None = None):
        self.td = Path(target_dir)
        self.cfg = cfg
        self.job = job or tools.Job()
        self.sleep = sleep
        self.languages = [c.strip().lower() for c in (languages if languages is not None else cfg["Languages"]) if c.strip()]
        for c in self.languages:
            naming.register_language(c)
        src = {"subsource": True, "pool": bool(cfg["UsePool"]), "opensubtitles": bool(cfg["UseOpenSubtitles"])}
        src.update(sources or {})
        self.sources = src
        self.temp = Path(tempfile.mkdtemp(prefix="muxstudio_fetch_"))
        paths = dict(tool_paths) if tool_paths is not None else {
            "mkvmerge": tools.find_tool("mkvmerge", cfg, self.td), "mkvextract": tools.find_tool("mkvextract", cfg, self.td)}
        self.mkvmerge = paths.get("mkvmerge")
        self.mkvextract = paths.get("mkvextract")
        self.json_cache: dict = {}
        self._probe = video_probe or (lambda p: probe.mkv_json(p, self.mkvmerge, self.job, self.json_cache))
        self.refs = RefProvider(self.td, cfg, self.mkvmerge, self.mkvextract, self.temp / "refs", self.job, self.json_cache)
        self.memory = ChoiceMemory(self.job)

        key = secrets.get("SubSourceApiKey", "")
        self.ss: subsource.SubSourceClient | None = None
        if src["subsource"] and key:
            self.ss = subsource.SubSourceClient(key, cfg["SubSourceApiBase"], cfg["ApiPauseMs"], cfg["MaxPages"], http,
                                                sleep, job=self.job, cache_dir=self.temp / "packs")
        tkey = secrets.get("TmdbApiKey", "")
        self.tmdb: TmdbClient | None = TmdbClient(tkey, "en-US", cfg["TmdbRetry"], http, sleep, self.job) if tkey else None
        self.os: opensubs.OpenSubtitlesClient | None = None
        if src["opensubtitles"] and secrets.get("OsApiKey"):
            self.os = opensubs.OpenSubtitlesClient(
                secrets.get("OsApiKey", ""), secrets.get("OsUsername", ""), secrets.get("OsPassword", ""),
                cfg["OsUserAgent"], cfg["ApiPauseMs"], cfg["OsWarnRemaining"], cfg["OsStopRemaining"], http, sleep,
                self.job, self.temp / "os")
        self._missing_key_ss = src["subsource"] and not key
        self.pool_index: pool.PoolIndex | None = None
        self.items: list[FetchItem] = []
        self.videos: list[VideoInfo] = []
        self._connected = False

    # ------------------------------------------------------------------ chuan bi
    @property
    def sub_dir(self) -> Path:
        return self.td / self.cfg["SubFolderName"]

    def lang_dir(self, lang: str) -> Path:
        return self.sub_dir / lang if self.cfg["UseLangSubFolders"] else self.sub_dir

    def excluded_dirs(self) -> list[Path]:
        """Q6/TĐ-11: không tải phụ đề cho video trong Output, thư mục audio nguồn, kho, srt_goc, log."""
        c = self.cfg
        names = [c["SubFolderName"], "Output", c["DonorFolder"], c["PoolFolder"], c["ExtractFolder"],
                 c["SubFetchLogDir"], c["SubPrepLogDir"], c["MuxLogDir"], *c["RefExcludeFolders"]]
        return [self.td / n for n in names if n]

    def connect(self) -> None:
        if self._connected:
            return
        self._connected = True
        c = self.cfg
        if self._missing_key_ss:
            self.job.warn("[!] Chưa điền SubSource API key → bỏ qua nguồn SubSource (các nguồn khác vẫn chạy).")
        unknown = [lg for lg in self.languages if not (naming.get_lang(lg) and naming.get_lang(lg).subsource)]
        if unknown and self.ss:
            self.job.warn(f"[!] Chưa biết tên SubSource cho: {', '.join(unknown)} — sẽ chỉ tìm trên OpenSubtitles.")
        if not self.mkvmerge:
            self.job.warn("[!] Không tìm thấy mkvmerge → không đọc được framerate video.")
        if c["VerifyTiming"] and not self.mkvextract:
            self.job.warn("[!] Thiếu mkvextract → chỉ dùng được phụ đề gốc có sẵn trong srt_goc.")
        if c["StrictFpsMatch"]:
            self.job.dim("[i] Ràng buộc framerate: BẬT (" + ("cho phép bản không khai báo fps" if c["AllowUnknownFps"]
                                                            else "chỉ lấy bản khai báo đúng fps") + ").")
        if not self.tmdb:
            self.job.dim("[i] Chưa có TMDB key → tìm theo tên thay vì IMDb ID.")
        if self.os:
            self.os.login()
        if self.sources["pool"]:
            root = self.td / c["PoolFolder"]
            self.pool_index = pool.build_index(root, c["ExcludeForced"], c["ExcludeSdh"], c["PoolDefaultLang"], self.job)

    # ------------------------------------------------------------------ 1. quet
    def scan(self) -> list[FetchItem]:
        """Liệt kê phim × ngôn ngữ, đọc FPS, tìm phụ đề gốc, nhận file có sẵn. Không ghi gì."""
        self.connect()
        c = self.cfg
        vids = tools.walk_files(self.td, bool(c["ScanSubFolders"]), c["VideoExts"], self.excluded_dirs())
        self.items, self.videos = [], []
        if not vids:
            self.job.error("[!] Không tìm thấy file video nào.")
            return []
        self.job.info(f"Tìm thấy {len(vids)} file video. Ngôn ngữ cần tải: {', '.join(self.languages)}")
        for i, v in enumerate(vids, 1):
            self.job.check()
            self.job.progress(i, len(vids), v.name)
            info = self._video_info(v)
            self.videos.append(info)
            for lang in self.languages:
                it = FetchItem(info, lang, self.lang_dir(lang))
                self._check_existing(it)
                self.items.append(it)
        return self.items

    def _video_info(self, v: Path) -> VideoInfo:
        stem = v.stem
        ep = episodes.parse_episode(stem)
        if ep is not None:
            title = ep.series or naming.split_release_name(stem).title
            year = ep.year
        else:
            rn = naming.split_release_name(stem)
            title, year = rn.title, rn.year
        j = self._probe(v)
        info = VideoInfo(v, stem, ep, title, year, probe.video_fps(j), naming.title_tokens(stem))
        self.job.head(f"=== {v.name}")
        self.job.dim(f"    Nhận diện: '{title}'" + (f" ({year})" if year else "") + f" - {info.kind_text}"
                     + (f" - {info.fps} fps" if info.fps else ""))
        if self.cfg["VerifyTiming"]:
            info.ref = self.refs.get(v, j)
        return info

    def _check_existing(self, it: FetchItem) -> None:
        if self.cfg["SkipIfExists"]:
            for ext in SUB_EXTS:
                p = it.dest(ext)
                if p.is_file():
                    it.already = p
                    it.status = "đã có"
                    self.job.dim(f"    [=] {it.lang} : đã có sẵn, bỏ qua tải.")
                    return
        if self.cfg["UseExistingSubs"]:
            m = self.match_existing(it)
            if m:
                it.existing = m
                it.status = f"dùng file có sẵn ({m.reason})"

    def match_existing(self, it: FetchItem) -> ExistingMatch | None:
        """``Use-ExistingSub`` (phần chọn): đo được thời gian thì CHỈ tin phép đo (≥ ``MinVerifyVotes``);
        không đo được mới dựa vào tên (≥ ``ExistingMinScore``). Tên ghi số tập khác → loại ngay."""
        v, lang = it.video, it.lang
        cands: list[Path] = []
        if it.lang_dir.is_dir():
            cands += tools.walk_files(it.lang_dir, False, SUB_EXTS)
        root = self.sub_dir
        if root.is_dir() and os.path.normcase(os.path.abspath(root)) != os.path.normcase(os.path.abspath(it.lang_dir)):
            rx = re.compile(r"(?i)[\._\-]" + re.escape(lang) + r"$")
            cands += [f for f in tools.walk_files(root, False, SUB_EXTS) if rx.search(f.stem)]
        if not cands:
            return None
        before = len(cands)
        cands = [f for f in cands if not episodes.episode_conflict(v.stem, f.stem)]
        if before - len(cands):
            self.job.dim(f"    [i] {lang} : bỏ {before - len(cands)} file thuộc tập khác.")
        if not cands:
            return None
        ref = v.ref.times if v.ref else None
        best: tuple[float, ExistingMatch] | None = None
        for f in cands:
            ns = naming.title_score(v.tokens, naming.title_tokens(f.stem))
            votes, fps_bad = -1, False
            if ref:
                t = srt.cue_times(f)
                if t and len(t) >= 30:
                    rel = timing.timing_relation(ref, t, self.cfg["FpsSampleRef"], self.cfg["FpsSampleTgt"])
                    if rel:
                        votes, fps_bad = rel.votes, rel.fps_mismatch
            rank = votes * 100 + ns if votes >= 0 else ns
            if best is None or rank > best[0]:
                best = (rank, ExistingMatch(f, votes, ns, fps_bad))
        assert best is not None
        m = best[1]
        measured = m.votes >= 0
        ok_time = measured and m.votes >= int(self.cfg["MinVerifyVotes"])
        ok_name = (not measured) and m.name_score >= float(self.cfg["ExistingMinScore"])
        if not ok_time and not ok_name:
            self.job.dim(f"    [i] {lang} : có {len(cands)} file sẵn nhưng không file nào khớp phim này.")
            if measured:
                self.job.dim(f"        gần nhất: {m.file.name} - chỉ {m.votes} mốc khớp (cần >= {self.cfg['MinVerifyVotes']})")
            else:
                self.job.dim(f"        gần nhất: {m.file.name} - tên giống {round(m.name_score, 2)} (cần >= {self.cfg['ExistingMinScore']})")
            return None
        self.job.ok(f"    [=] {lang} : DÙNG FILE CÓ SẴN ({m.reason}) — {m.file.name}")
        if m.fps_bad:
            self.job.warn("        [!] Đo được LỆCH FPS - bước ② sẽ cần xử lý.")
        return m

    # ------------------------------------------------------------------ 2. gom ung vien
    def _ensure_entry(self, v: VideoInfo) -> None:
        if v.entry_checked:
            return
        v.entry_checked = True
        ep = v.ep
        is_tv = ep is not None
        season: int | None = None
        if self.tmdb and v.title:
            if is_tv and ep is not None:
                v.imdb = self.tmdb.imdb_id(v.title, v.year, True, ep.season if ep.has_season else None, ep.episode)
                if not ep.has_season:
                    show = self.tmdb.find_show(v.title, v.year, None, ep.episode)
                    if show:
                        v.mapped = self.tmdb.absolute_to_season(int(show.get("id")), ep.episode)
                        if v.mapped:
                            self.job.info(f"    [i] Số tập tuyệt đối {ep.ref_label} → S{v.mapped[0]:02d}E{v.mapped[1]:02d} (TMDB)")
            else:
                v.imdb = self.tmdb.imdb_id(v.title, v.year, False)
            if v.imdb:
                self.job.dim(f"    IMDb: {v.imdb}")
        if is_tv and ep is not None:
            season = ep.season if ep.has_season else (v.mapped[0] if v.mapped else None)
        if not self.ss:
            return
        if is_tv and season is None:
            self.job.warn("    [!] Số tập tuyệt đối, không xác định được season → bỏ qua SubSource cho tập này.")
            return
        v.entry = self.ss.find_entry(v.title, v.year, season, v.imdb)
        if v.entry:
            e = v.entry
            self.job.dim(f"    SubSource: {e.get('title')}" + (f" - season {e.get('season')}" if e.get("season") else "")
                         + f" (movieId={e.get('movieId')}, {e.get('subtitleCount')} phụ đề)")
        else:
            self.job.warn("    [-] Không tìm thấy trên SubSource.")

    def _os_episode(self, v: VideoInfo) -> tuple[int | None, int | None]:
        ep = v.ep
        if ep is None:
            return None, None
        if ep.has_season:
            return ep.season, ep.episode
        if v.mapped:
            return v.mapped
        return None, ep.episode

    def _pack_file(self, v: VideoInfo, d: Path | None) -> Path | None:
        c = self.cfg
        f = subsource.select_from_pack(d, v.ep, v.tokens, c["ExcludeForced"], c["ExcludeSdh"])
        if f is None and v.mapped and v.ep is not None:
            mep = episodes.EpisodeInfo(v.ep.series, v.ep.year, v.mapped[0], v.mapped[1], None, True, True)
            f = subsource.select_from_pack(d, mep, v.tokens, c["ExcludeForced"], c["ExcludeSdh"])
        return f

    def online_candidates(self, it: FetchItem) -> list[Candidate]:
        """``Get-OnlineCandidates``: SubSource rồi OpenSubtitles (chạm mạng; OpenSubtitles tìm không tốn quota)."""
        c = self.cfg
        v = it.video
        out: list[Candidate] = []
        ref = v.ref.times if v.ref else None
        lg = naming.get_lang(it.lang)
        if self.ss and v.entry and lg and lg.subsource:
            for s in self.ss.list_for_lang(int(v.entry.get("movieId")), it.lang):
                if c["ExcludeSdh"] and s.get("hearingImpaired") is True:
                    continue
                if c["ExcludeForced"] and s.get("foreignParts") is True:
                    continue
                rels = s.get("releaseInfo") or []
                rel = str(rels[0]) if isinstance(rels, list) and rels else str(rels or "")
                sc = naming.title_score(v.tokens, naming.title_tokens(rel))
                try:
                    nfiles = int(s.get("files") or 0)
                except (TypeError, ValueError):
                    nfiles = 0
                is_pack = nfiles > 1
                off: Offset | None = None
                sid = s.get("subtitleId")
                # Goi da tai ve -> DO THANG file cua dung tap nay (khong ton them luot tai)
                if ref and sid is not None and self.ss.is_downloaded(int(sid)):
                    f = self._pack_file(v, self.ss.pack_dir(int(sid)))
                    if f is not None:
                        tt = srt.cue_times(f)
                        if tt and len(tt) >= 30:
                            rr = timing.timing_relation(ref, tt, c["FpsSampleRef"], c["FpsSampleTgt"])
                            if rr and rr.votes >= int(c["MinVerifyVotes"]):
                                off = Offset(rr.offset, rr.votes, -1)
                # Chua tai -> uoc tinh tu preview (preview cua goi season chi thuoc MOT tap)
                if off is None and ref and s.get("preview"):
                    q = timing.quick_offset(ref, srt.preview_times(str(s.get("preview"))))
                    if q:
                        off = Offset(q.offset, q.votes, q.total)
                note = f"{s.get('files')} file, {s.get('downloads')} tải" + (" (gói season)" if is_pack else "")
                out.append(Candidate("SubSource", fps_tier(s.get("framerate"), v.fps), str(s.get("framerate") or ""),
                                     sc, rel, note, s, off, False, is_pack))
        if self.os and not self.os.disabled:
            season, epn = self._os_episode(v)
            for o in self.os.search(v.imdb, season, epn, it.lang, v.title, v.year):
                if not opensubs.usable(o, c["ExcludeSdh"], c["ExcludeForced"], c["OsExcludeMachine"]):
                    continue
                a = o.get("attributes") or {}
                rel = str(a.get("release") or "")
                mt = opensubs.is_machine(o)
                info = ("dịch máy, " if mt else "") + f"{a.get('download_count')} tải"
                out.append(Candidate("OpenSub", fps_tier(a.get("fps"), v.fps), str(a.get("fps") or ""),
                                     naming.title_score(v.tokens, naming.title_tokens(rel)), rel, info, o, None, mt))
        return out

    def gather(self, it: FetchItem) -> list[Candidate]:
        """Gom ứng viên từ MỌI nguồn rồi xếp hạng (phần chính của SubFetch cho một tập × ngôn ngữ)."""
        c = self.cfg
        it.gathered = True
        if it.already or it.existing or it.skip:
            return []
        v = it.video
        ref = v.ref.times if v.ref else None
        cands: list[Candidate] = []
        if self.sources["pool"]:
            cands = pool.pool_candidates(self.pool_index, it.lang, v.stem, v.tokens, ref, c, self.job)
        pool_hit = [x for x in cands if x.tier == 1 and x.off]
        if c["PoolFirst"] and pool_hit:
            self.job.info(f"    [i] {it.lang} : kho nội bộ đã có bản khớp → bỏ qua tìm trên mạng.")
            cands = pool_hit
        else:
            if cands:
                self.job.dim(f"    [i] {it.lang} : kho nội bộ có {len(cands)} ứng viên, tìm thêm trên mạng...")
            self._ensure_entry(v)
            cands += self.online_candidates(it)
        if not cands:
            it.status = "không nguồn nào có"
            self.job.warn(f"    [-] {it.lang} : không nguồn nào có phụ đề.")
            if self.ss and v.entry and not v.lang_reported:
                summ = subsource.lang_summary(self.ss.list_all(int(v.entry.get("movieId"))))
                self.job.dim(f"        (SubSource có: {', '.join(summ)})")
                v.lang_reported = True
            it.candidates = []
            return []
        before = len(cands)
        if c["StrictFpsMatch"]:
            cands = [x for x in cands if x.tier != 3]
        if not c["AllowUnknownFps"]:
            cands = [x for x in cands if x.tier != 2]
        if before - len(cands):
            self.job.dim(f"    [i] {it.lang} : loại {before - len(cands)} bản lệch framerate.")
        if not cands:
            it.status = "mọi bản đều lệch framerate"
            self.job.warn(f"    [-] {it.lang} : có {before} bản nhưng đều lệch framerate.")
        it.candidates = sort_candidates(cands)[: int(c["MaxShowCandidates"])]
        return it.candidates

    # ------------------------------------------------------------------ 3. chon
    def auto_select(self, it: FetchItem) -> Candidate | None:
        """Phần không cần hỏi của ``Select-Candidate``. ``None`` = cần bạn chọn trên bảng."""
        if not it.candidates or it.chosen or it.skip:
            return it.chosen
        c = self.cfg
        if c["RememberChoice"]:
            r = self.memory.recall(it.memory_key, it.video.stem, it.candidates)
            if r:
                it.chosen, it.choice_reason = r, "dùng lại lựa chọn trước"
                return r
        if not c["Interactive"]:
            it.chosen, it.choice_reason = it.candidates[0], "tự động (bản đứng đầu)"
            return it.chosen
        only = it.candidates[0] if len(it.candidates) == 1 else None
        if only is not None and only.fps == "đo: nghi ngờ":
            # TD-28: do duoc nhung chi 8-19 moc khop ("can kiem tra") -> khong tu lay, de ban xem
            self.job.warn("  [!] Ứng viên duy nhất chỉ khớp yếu (cần kiểm tra) → chờ bạn chọn.")
            return None
        if c["AutoPickWhenSingle"] and only is not None:
            self.job.info("  [i] Chỉ có 1 ứng viên → lấy luôn.")
            it.chosen, it.choice_reason = it.candidates[0], "chỉ có 1 ứng viên"
            if c["RememberChoice"]:
                self.memory.remember(it.memory_key, it.video.stem, it.chosen)
            return it.chosen
        return None

    def choose(self, it: FetchItem, cand: Candidate, remember: bool | None = None) -> None:
        """Bạn chọn một ứng viên (bấm đúp trên bảng). ``remember`` = ô "Dùng lại lựa chọn này…"."""
        it.chosen, it.skip, it.choice_reason = cand, False, "bạn chọn"
        if remember if remember is not None else self.cfg["RememberChoice"]:
            self.memory.remember(it.memory_key, it.video.stem, cand)

    def skip(self, it: FetchItem) -> None:
        """Nút "Bỏ qua ngôn ngữ này"."""
        it.skip, it.chosen, it.status = True, None, "bỏ qua"

    def prefetch(self, cand: Candidate) -> bool:
        """Tải trước gói SubSource (miễn phí) để đo thẳng file của từng tập trong gói."""
        if cand.src != "SubSource" or not self.ss:
            return False
        return self.ss.download_pack(int(cand.payload.get("subtitleId"))) is not None

    def remeasure(self, items: Sequence[FetchItem] | None = None) -> None:
        """Gom lại ứng viên (đo lại) cho các mục chưa chạy — dùng sau ``prefetch``."""
        for it in items if items is not None else self.items:
            if it.result is None and not it.already and not it.existing and not it.skip:
                keep = it.chosen
                self.gather(it)
                if keep is not None:
                    it.chosen = next((x for x in it.candidates if x.ident == keep.ident), keep)

    def plan_all(self, ask: Callable[[FetchItem], Candidate | None] | None = None) -> list[FetchItem]:
        """Lập trọn kế hoạch: quét → gom → tự chọn; ``ask`` được gọi cho mục cần bạn chọn
        (``None`` = bỏ qua ngôn ngữ đó). Dùng cho chế độ tự động và cho test."""
        if not self.items:
            self.scan()
        for it in self.items:
            self.job.check()
            if it.already or it.existing:
                continue
            self.gather(it)
            if not it.candidates:
                continue
            if self.auto_select(it) is None:
                pick = ask(it) if ask else None
                if pick is None:
                    self.skip(it)
                else:
                    self.choose(it, pick)
            if it.chosen and it.chosen.src == "SubSource" and it.chosen.is_pack and self.ss:
                if not self.ss.is_downloaded(int(it.chosen.payload.get("subtitleId"))):
                    self.prefetch(it.chosen)
        return self.items

    # ------------------------------------------------------------------ 4. thuc thi
    def _fetch(self, it: FetchItem, cd: Candidate) -> Path | None:
        """``Get-ChosenSubtitle``: lấy về MỘT file (chưa đặt vào đích)."""
        if cd.src == "SubSource":
            if not self.ss:
                return None
            d = self.ss.download_pack(int(cd.payload.get("subtitleId")))
            if d is None:
                return None
            f = self._pack_file(it.video, d)
            if f is None:
                self.job.warn("      (gói này không có tập cần tìm)")
            return f
        if cd.src == "Kho":
            return Path(cd.payload)
        if cd.src == "OpenSub":
            return self.os.download(cd.payload) if self.os else None
        return None

    def report_timing(self, path: Path, ref: RefInfo | None) -> tuple[str, str, int]:
        """``Report-Timing``: CHỈ BÁO CÁO, không sửa gì. Trả (mức, chữ, số mốc khớp; -1 = không đo)."""
        if not self.cfg["VerifyTiming"] or not ref:
            return "", "", -1
        t = srt.cue_times(path)
        if not t or len(t) < 30:
            return "", "", -1
        rel = timing.timing_relation(ref.times, t, self.cfg["FpsSampleRef"], self.cfg["FpsSampleTgt"])
        if not rel:
            return "", "", -1
        if rel.votes < int(self.cfg["MinVerifyVotes"]):
            return "warn", f"Kiểm chứng yếu: chỉ {rel.votes} mốc khớp - kiểm tra kỹ khi xem.", rel.votes
        if rel.fps_mismatch:
            return "warn", f"Đo được LỆCH FPS ({rel.name}) - bước ② sẽ cần sửa khi đồng bộ.", rel.votes
        sign = "+" if rel.offset >= 0 else ""
        return "ok", f"Khớp thời gian, lệch {sign}{round(rel.offset, 2)}s (bước ② sẽ đồng bộ)", rel.votes

    def execute(self, items: Sequence[FetchItem] | None = None) -> FetchReport:
        """Chạy đúng kế hoạch. Ghi nhật ký hoàn tác + báo cáo. Bước ① không sửa thời gian phụ đề."""
        items = list(items if items is not None else self.items)
        c = self.cfg
        rep = FetchReport()
        log = undo.RunLog(self.td, c["SubFetchLogDir"], "subfetch", c["KeepUndoRuns"])
        rows: list[dict[str, Any]] = []
        self._keep_refs({id(it.video): it.video for it in items}.values(), log)
        for n, it in enumerate(items, 1):
            if self.job.should_stop():
                rep.stopped = True
                break
            self.job.progress(n, len(items), f"{it.video.path.name} [{it.lang}]")
            if it.already:
                rep.already += 1
                continue
            if it.existing:
                self._apply_existing(it, log)
                rep.used_existing += 1
                continue
            if it.skip and it.candidates:
                rep.skipped += 1
                self.job.dim(f"    [>] Bỏ qua {it.lang}: {it.video.path.name}")
                continue
            if not it.chosen:
                rep.missing += 1
                continue
            if self._fetch_item(it, log, rows):
                rep.ok += 1
            else:
                rep.missing += 1
        rep.log_path = log.save()
        if rows:
            rep.report_path = self._save_report(rows)
        if self.os:
            rep.os_remaining, rep.os_allowed, rep.os_reset = self.os.remaining, self.os.allowed, self.os.reset_msg
        self.job.head(f"TỔNG KẾT: {rep.ok} phụ đề đã tải | {rep.used_existing} dùng file có sẵn | "
                      f"{rep.missing} không tìm được" + (f" | {rep.skipped} bỏ qua" if rep.skipped else ""))
        if self.os and rep.os_remaining >= 0:
            lvl = "warn" if rep.os_remaining <= c["OsWarnRemaining"] else "info"
            self.job.log(f"OpenSubtitles: còn {rep.os_remaining}/{rep.os_allowed} lượt tải hôm nay", lvl)
        if rep.log_path:
            self.job.info(f"Nhật ký thao tác (để hoàn tác): {c['SubFetchLogDir']}\\{rep.log_path.name}")
        return rep

    def _keep_refs(self, videos: Any, log: undo.RunLog) -> None:
        """Giữ phụ đề gốc vừa trích vào ``srt_goc`` cho bước ② (§6.11)."""
        for v in videos:
            if v.ref is None or v.ref.pending is None:
                continue
            tmp, final = v.ref.pending
            if final.exists() or not tmp.is_file():
                continue
            new_dir = not final.parent.exists()
            tools.copy_file(tmp, final)
            if new_dir:
                log.add(undo.MKDIR, "", final.parent)
            log.add(undo.CREATE, v.path, final, "phụ đề gốc")
            v.ref = RefInfo(v.ref.times, final, v.ref.source, None)
            self.job.dim(f"    Giữ phụ đề gốc: {self.cfg['ExtractFolder']}\\{final.name}")

    def _ensure_dir(self, d: Path, log: undo.RunLog) -> None:
        if not d.exists():
            missing = []
            p = d
            while not p.exists():
                missing.append(p)
                p = p.parent
            d.mkdir(parents=True, exist_ok=True)
            for m in reversed(missing):
                log.add(undo.MKDIR, "", m)

    def _apply_existing(self, it: FetchItem, log: undo.RunLog) -> None:
        m = it.existing
        assert m is not None
        dest = it.dest(m.file.suffix)
        if os.path.normcase(os.path.abspath(m.file)) == os.path.normcase(os.path.abspath(dest)):
            return
        self._ensure_dir(dest.parent, log)
        if self.cfg["ExistingRenameMode"] == "move":
            os.replace(m.file, dest)
            log.add(undo.MOVE, m.file, dest, m.reason)
        else:
            tools.copy_file(m.file, dest)  # CHEP, khong di chuyen (§6.11)
            log.add(undo.COPY, m.file, dest, m.reason)
        it.result, it.result_src = dest, "có sẵn"
        self.job.ok(f"    [=] {it.lang} : {m.file.name} → {dest.name}")

    def _fetch_item(self, it: FetchItem, log: undo.RunLog, rows: list[dict[str, Any]]) -> bool:
        c = self.cfg
        chosen = it.chosen
        assert chosen is not None
        try_list = [chosen] + [x for x in it.candidates if x.ident != chosen.ident]
        for cd in try_list:
            self.job.check()
            if cd is chosen:
                self.job.info(f"    [~] Đang lấy: [{cd.src}] {cd.release}")
            else:
                self.job.warn(f"    [~] Thử bản khác: [{cd.src}] {cd.release}")
            got = self._fetch(it, cd)
            if got is None:
                if c["RememberChoice"]:
                    self.memory.forget(it.memory_key, cd.release)
                continue
            lvl, note, votes = self.report_timing(got, it.video.ref)
            if (cd.src == "OpenSub" and not c["KeepPaidDownloads"] and lvl == "warn" and 0 <= votes < int(c["MinVerifyVotes"])):
                undo.trash(got, bool(c["DeleteToRecycleBin"]))
                self.job.warn(f"      [!] {note} → bỏ file (KeepPaidDownloads tắt), thử bản kế tiếp.")
                continue
            dest = it.dest(got.suffix if got.suffix.lower() in SUB_EXTS else ".srt")
            self._ensure_dir(dest.parent, log)
            tools.copy_file(got, dest)  # chep nguyen byte: KHONG doi thoi gian, KHONG doi ma
            log.add(undo.CREATE, cd.release, dest, cd.src)
            it.result, it.result_src, it.chosen, it.timing_note = dest, cd.src, cd, note
            rel_dest = os.path.relpath(dest, self.td)
            self.job.ok(f"      → ĐÃ LƯU: {rel_dest}")
            if note:
                self.job.log(f"      [{'v' if lvl == 'ok' else '!'}] {note}", "ok" if lvl == "ok" else "warn")
            rows.append({"Video": it.video.path.name, "Lang": it.lang, "Nguon": cd.src, "Release": cd.release,
                         "Score": round(cd.score, 2), "Fps": cd.fps, "Lech": cd.offset_label, "KiemChung": note})
            return True
        it.status = "lấy thất bại"
        self.job.warn(f"    [-] {it.lang} : lấy thất bại.")
        return False

    def _save_report(self, rows: list[dict[str, Any]]) -> Path:
        d = self.td / self.cfg["SubFetchLogDir"]
        d.mkdir(parents=True, exist_ok=True)
        p = d / f"fetch_{_dt.datetime.now().strftime('%Y%m%d_%H%M%S')}.csv"
        with open(p, "w", encoding="utf-8-sig", newline="") as fh:
            w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()), quoting=csv.QUOTE_ALL, lineterminator="\r\n")
            w.writeheader()
            w.writerows(rows)
        return p

    # ------------------------------------------------------------------ don dep
    def close(self) -> None:
        shutil.rmtree(self.temp, ignore_errors=True)

    def __enter__(self) -> "FetchSession":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()
