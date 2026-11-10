"""Bước ③ — Mux (thay MuxTool_v17.bat). docs/SPEC.md mục 4.3, 4.4; §6.2, 6.7–6.10.

Mẫu "lập kế hoạch → thực thi" (§4):

* ``MuxSession.db_status()`` — tình trạng TMDB / TVDB (kiểm tra ngay khi mở thẻ).
* ``MuxSession.plan_all(db)`` — ``db`` BẮT BUỘC chọn: ``tmdb`` | ``tvdb`` | ``none`` (không có mặc định).
  Mỗi file một ``MuxPlan``: tên mới (``naming.resolve_output_name``), audio giữ/xoá + lý do, phụ đề ngoài/trong
  (``sub_auto_plan`` — nguồn quy tắc phụ đề DUY NHẤT), Default/Forced, thẻ codec, audio ghép (``donor``), trạng thái
  ``ok`` | ``need_choice`` ("Cần chọn") | ``skip`` | ``exists`` | ``fail``. KHÔNG ghi gì, KHÔNG tạo thư mục (TĐ-21).
* Sửa kế hoạch: ``set_audio_choice`` (bảng audio, "Áp dụng cho mọi file còn lại"), ``keep_all_audio``,
  ``set_sub_choice`` (bảng chọn phụ đề theo tập), ``choose_name`` / ``keep_original_name`` (dòng "Cần chọn"),
  ``set_donor_offset`` (độ lệch tay trong bảng) → gọi lại ``plan_file`` cho dòng đó.
* ``preview_commands(plan)`` — nút "Xem lệnh": đủ lệnh ffmpeg + mkvmerge, không ghi gì (TĐ-21).
* ``execute(plans)`` — chạy; mã thoát 0 = OK, 1 = OK có cảnh báo, ≥ 2 = lỗi → xoá file dở dang.
  Ghi ra tên tạm rồi mới đổi tên → bấm Dừng không để lại file dở. Nhật ký hoàn tác ``_muxtool_logs\\mux_*.csv``
  (``Create`` file đầu ra + ``Mkdir`` thư mục mới — TĐ-17).
"""
from __future__ import annotations

import os
import re
import shutil
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from . import donor as D
from . import encoding, naming, probe, tools, undo
from .tmdb import ConnStatus, TmdbClient
from .tvdb import TvdbClient

MUX_EXTS_DEFAULT = (".mkv", ".mp4", ".m4v", ".avi", ".ts")


def _q(a: str) -> str:
    return f'"{a}"' if (not a or re.search(r'[\s"&()\[\]{}^=;!\'+,`~]', a)) else a


# ==========================================================================
#  AUDIO
# ==========================================================================


@dataclass
class AudioTrack:
    id: int
    code: str
    codec: str
    channels: str
    bitrate: float
    bitrate_src: str
    track_name: str
    keep: bool = True
    reason: str = ""  # ly do XOA
    default: bool = False

    @property
    def label(self) -> str:
        """Tên track mới: ``English E-AC-3 5.1`` (§6.7)."""
        return " ".join(x for x in (naming.lang_name(self.code), self.codec, self.channels) if x)

    def describe(self) -> str:
        nm = f"  [{self.track_name}]" if self.track_name else ""
        return f"#{self.id}  {self.label}  {probe.format_kbps(self.bitrate)}{nm}"


@dataclass
class AudioRule:
    """``Select-AudioTracks`` — ghi nhớ = nhớ những gì GIỮ: ngôn ngữ giữ; ngôn ngữ vừa giữ vừa xoá thì nhớ thêm
    hệ kênh được giữ ("English 5.1"). Nhờ vậy ngôn ngữ lạ ở file sau tự bị loại."""

    keep_langs: list[str]
    keep_sig: dict[str, list[str]] | None = None

    @classmethod
    def from_choice(cls, kept: Sequence[AudioTrack], dropped: Sequence[AudioTrack]) -> "AudioRule":
        keep_langs = list(dict.fromkeys(a.code for a in kept))
        drop_langs = list(dict.fromkeys(a.code for a in dropped))
        mixed = [x for x in drop_langs if x in keep_langs]
        sig = None
        if mixed:
            sig = {m: list(dict.fromkeys(a.channels for a in kept if a.code == m)) for m in mixed}
        return cls(keep_langs, sig)

    def apply(self, tracks: Sequence[AudioTrack]) -> list[AudioTrack] | None:
        """Track được giữ theo quy tắc; quy tắc làm mất HẾT audio → ``None`` (bỏ qua quy tắc cho file đó)."""
        keep = []
        for a in tracks:
            ok = a.code in self.keep_langs
            if ok and self.keep_sig and a.code in self.keep_sig and a.channels not in self.keep_sig[a.code]:
                ok = False
            if ok:
                keep.append(a)
        return keep or None

    def describe(self) -> str:
        """``Get-AudioRuleText``: ``chỉ giữ English (5.1), Czech``."""
        parts = []
        for lg in self.keep_langs:
            n = naming.lang_name(lg) or lg
            parts.append(f"{n} ({'/'.join(self.keep_sig[lg])})" if self.keep_sig and lg in self.keep_sig else n)
        return "chỉ giữ " + ", ".join(parts)


@dataclass
class AudioInput:
    """Lựa chọn của bạn trong bảng audio của MỘT file."""

    drop_ids: set[int] = field(default_factory=set)
    keep_all_default_highest: bool = False  # "Giu ca hai, Default = bitrate cao nhat" (Q3)


# ==========================================================================
#  PHỤ ĐỀ — nguồn quy tắc DUY NHẤT (Get-SubAutoPlan + BƯỚC 3/4/4b/5 của MuxTool)
# ==========================================================================


@dataclass
class ExtSub:
    path: Path
    code: str
    rank: int

    @property
    def label(self) -> str:
        return naming.lang_name(self.code) or "Không rõ"


@dataclass
class IntSub:
    id: int
    code: str
    sdh: bool
    forced: bool
    image: bool
    commentary: bool
    codec_id: str
    track_name: str
    keep: bool = True
    reason: str = "giữ"
    default: bool = False

    @property
    def label(self) -> str:
        """Tên track: ``Ngôn ngữ [SDH] [Forced]``."""
        return " ".join(x for x in (naming.lang_name(self.code), "SDH" if self.sdh else "", "Forced" if self.forced else "") if x)


@dataclass
class SubItem:
    """Một ô của bảng chọn phụ đề theo tập (``Get-SubAutoPlan``)."""

    key: str  # cot: "E|vi" (ngoai) / "I|English SDH" (trong)
    kind: str  # ext | int
    id: int  # -1 voi sub ngoai
    code: str
    auto_keep: bool
    header: str
    tip: str
    order: int


@dataclass
class SubChoice:
    """Kết quả bảng chọn phụ đề cho MỘT file: mã ngôn ngữ sub ngoài giữ + ID sub trong MKV giữ."""

    ext: set[str] = field(default_factory=set)
    int_ids: set[int] = field(default_factory=set)


def external_subs(video: Path, cfg: Mapping[str, Any]) -> list[ExtSub]:
    """BƯỚC 3: ``<tên phim>.<mã>.srt|ass|ssa|sup|vtt`` cạnh phim, mỗi ngôn ngữ chọn theo ``SubExtPriority``;
    không hậu tố = ``DefaultExternalLang``. Thứ tự: theo ``SubDefaultPriority`` rồi các ngôn ngữ khác.
    TĐ-25: hậu tố đọc theo bảng ngôn ngữ chung (bản cũ chỉ vi/cs/en)."""
    prio = [str(x).lower() for x in cfg["SubExtPriority"]]
    keep_langs = [str(x).lower() for x in cfg["SubKeepLangs"]]
    base = video.stem
    by_lang: dict[str, ExtSub] = {}
    try:
        entries = sorted(os.scandir(video.parent), key=lambda e: e.name.lower())
    except OSError:
        return []
    for e in entries:
        if not e.is_file():
            continue
        p = Path(e.path)
        ext = p.suffix.lower()
        if ext not in prio or len(p.stem) < len(base) or p.stem[: len(base)].lower() != base.lower():
            continue
        suffix = p.stem[len(base):]
        if re.search(r"(?i)forced", suffix) and cfg["RemoveForcedSubs"]:
            continue
        code = str(cfg["DefaultExternalLang"]).lower() if not suffix.strip() else naming.lang_of_suffix(suffix)
        if not code or code not in keep_langs:
            continue
        rank = prio.index(ext)
        if code not in by_lang or rank < by_lang[code].rank:
            by_lang[code] = ExtSub(p, code, rank)
    pri = [str(x).lower() for x in cfg["SubDefaultPriority"]]
    return [by_lang[c] for c in pri if c in by_lang] + [v for c, v in by_lang.items() if c not in pri]


def internal_subs(j: dict | None, cfg: Mapping[str, Any], ext_langs: Sequence[str]) -> list[IntSub]:
    """BƯỚC 4: giữ ngôn ngữ trong ``SubKeepLangs``; có sub ngoài ngôn ngữ X → xoá sub trong X; xoá Forced, phụ đề
    dạng ảnh, commentary, English SDH nếu đã có English thường."""
    keep_langs = [str(x).lower() for x in cfg["SubKeepLangs"]]
    out: list[IntSub] = []
    for t in probe.tracks(j, "subtitles"):
        p = t.get("properties") or {}
        out.append(IntSub(int(t.get("id", 0)), probe.track_lang(t), probe.is_sdh(p), probe.is_forced(p),
                          probe.is_image_sub(p.get("codec_id")), probe.is_commentary(p), str(p.get("codec_id") or ""),
                          str(p.get("track_name") or "")))
    has_clean_eng = any(s.code == "en" and not s.sdh and not s.forced for s in out)
    for s in out:
        if s.code not in keep_langs:
            s.keep, s.reason = False, "ngoài danh sách ngôn ngữ"
        elif s.code in ext_langs:
            s.keep, s.reason = False, "đã có sub ngoài thay thế"
        elif s.forced and cfg["RemoveForcedSubs"]:
            s.keep, s.reason = False, "Forced"
        elif cfg["RemoveImageSubs"] and s.image:
            s.keep, s.reason = False, "phụ đề dạng ảnh"
        elif cfg["RemoveCommentarySubs"] and s.commentary:
            s.keep, s.reason = False, "Commentary"
        elif s.code == "en" and s.sdh and has_clean_eng:
            s.keep, s.reason = False, "SDH (đã có English thường)"
        elif s.sdh and not has_clean_eng:
            s.reason = "vớt vát (không có bản thường)"
    return out


def sub_auto_plan(video: Path, j: dict | None, cfg: Mapping[str, Any]) -> list[SubItem]:
    """``Get-SubAutoPlan``: các ô của bảng chọn phụ đề cho MỘT file + quyết định tự động."""
    items: list[SubItem] = []
    pri = [str(x).lower() for x in cfg["SubDefaultPriority"]]
    exts = external_subs(video, cfg)
    for e in exts:
        items.append(SubItem(f"E|{e.code}", "ext", -1, e.code, True, f"NGOÀI\n{e.label} ({e.path.suffix.lstrip('.').upper()})",
                             f"File ngoài: {e.path.name}", pri.index(e.code) if e.code in pri else 50))
    ext_langs = [e.code for e in exts] if cfg["ExternalSubOverridesInternal"] else []
    seen: dict[str, int] = {}
    for s in internal_subs(j, cfg, ext_langs):
        label = naming.lang_name(s.code) or "Không rõ"
        label += " SDH" if s.sdh else ""
        label += " Forced" if s.forced else ""
        label += " [ảnh]" if s.image else ""
        label += " [bình luận]" if s.commentary else ""
        k = f"I|{label}"
        if k in seen:  # hai track cung nhan trong mot file -> cot rieng
            seen[k] += 1
            k, label = f"{k} #{seen[k]}", f"{label} #{seen[k]}"
        else:
            seen[k] = 1
        nm = f"  '{s.track_name}'" if s.track_name else ""
        items.append(SubItem(k, "int", s.id, s.code, s.keep, f"TRONG MKV\n{label}",
                             f"Track #{s.id}  {s.codec_id}{nm}  → tự động: {s.reason}", 100))
    return items


def pick_default_sub(exts: Sequence[ExtSub], ints: Sequence[IntSub], priority: Sequence[str]) -> tuple[str, int]:
    """BƯỚC 5: Default theo ``SubDefaultPriority`` (vi > en > cs): sub ngoài trước, rồi sub trong không SDH, rồi
    sub trong bất kỳ. Trả (``ext``, chỉ số) | (``int``, ID) | (``''``, -1)."""
    kept = [s for s in ints if s.keep]
    for code in [str(x).lower() for x in priority]:
        for i, e in enumerate(exts):
            if e.code == code:
                return "ext", i
        cand = [s for s in kept if s.code == code and not s.sdh] or [s for s in kept if s.code == code]
        if cand:
            return "int", cand[0].id
    return "", -1


# ==========================================================================
#  THẺ CODEC (chỉ vào TÊN FILE)
# ==========================================================================

_TAG_AUDIO = {"Dolby Digital": "AC3", "E-AC-3": "EAC3", "DTS-HD MA": "DTS-HD", "DTS-HD HRA": "DTS-HD"}


def codec_tag(j: dict | None, video_stream: dict | None, orig_name: str, def_audio: dict | None,
              parts: Sequence[str]) -> str:
    """``Get-CodecTag``: ``2160p DV HDR HEVC TrueHD7.1 Atmos``. Thẻ CHỈ gắn vào tên file sau `` - ``, không bao giờ
    vào tên thư mục (Jellyfin dùng phần sau `` - `` làm nhãn phiên bản)."""
    out: list[str] = []
    vt = next(iter(probe.tracks(j, "video")), None)
    if vt is not None:
        vp = vt.get("properties") or {}
        if "res" in parts:
            r = probe.resolution_name(vp.get("pixel_dimensions"))
            if r:
                out.append(r)
        if "hdr" in parts:
            d = probe.dynamic_range(video_stream, orig_name)
            if d:
                out.append(d)
        if "vcodec" in parts:
            vc = probe.video_codec_name(vp.get("codec_id"), vt.get("codec") or vp.get("codec"))
            if vc:
                out.append(vc)
    if "acodec" in parts and def_audio is not None:
        ap = def_audio.get("properties") or {}
        ac = probe.audio_codec_name(def_audio)
        ac = _TAG_AUDIO.get(ac, ac)
        ch = probe.channel_name(ap.get("audio_channels"))
        atmos = ""
        if re.search(r"(?i)atmos", f"{ap.get('track_name') or ''} {def_audio.get('codec') or ''}") or \
                re.search(r"(?i)\batmos\b", orig_name):
            atmos = " Atmos"
        if ac:
            out.append(f"{ac}{ch}{atmos}".strip())
    return naming.safe_filename(" ".join(out)) if out else ""


# ==========================================================================
#  KẾ HOẠCH
# ==========================================================================


@dataclass
class MuxResult:
    code: int = -1
    ok: bool = False
    warning: bool = False
    output: list[str] = field(default_factory=list)
    out: Path | None = None


@dataclass
class UsedFile:
    """File nguồn đã dùng cho file mux THÀNH CÔNG — đề xuất đưa vào Thùng rác sau khi mux."""

    path: Path
    kind: str  # video | sub | donor
    used_for: list[str] = field(default_factory=list)  # ten file ket qua trong Output

    @property
    def kind_text(self) -> str:
        return {"video": "Phim gốc", "sub": "Phụ đề ngoài", "donor": "Nguồn audio ghép"}.get(self.kind, self.kind)


@dataclass
class TrashReport:
    done: list[Path] = field(default_factory=list)
    failed: list[Path] = field(default_factory=list)


@dataclass
class MuxPlan:
    src: Path
    rel: str
    name: naming.OutputName
    json: dict | None = None
    status: str = "ok"  # ok | need_choice | skip | exists | fail
    reasons: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    out_base: str = ""
    out_path: Path | None = None
    codec_tag: str = ""
    video_ids: list[int] = field(default_factory=list)
    video_names: dict[int, str] = field(default_factory=dict)
    audio: list[AudioTrack] = field(default_factory=list)
    lang_override: dict[int, str] = field(default_factory=dict)
    default_audio: int = -1
    ext_subs: list[ExtSub] = field(default_factory=list)
    int_subs: list[IntSub] = field(default_factory=list)
    default_sub: tuple[str, int] = ("", -1)
    sub_items: list[SubItem] = field(default_factory=list)
    donor: D.DonorPlan = field(default_factory=D.DonorPlan)
    needs_audio_table: bool = False
    rule_applied: str = ""
    selected: bool = True
    result: MuxResult | None = None

    @property
    def keep_audio(self) -> list[int]:
        return [a.id for a in self.audio if a.keep]

    @property
    def keep_subs(self) -> list[IntSub]:
        return [s for s in self.int_subs if s.keep]

    @property
    def status_text(self) -> str:
        return {"ok": "sẵn sàng", "need_choice": "Cần chọn", "skip": "bỏ qua", "exists": "đã có trong Output",
                "fail": "lỗi"}.get(self.status, self.status)


def used_sources(plans: Sequence[MuxPlan], out_dir: str | os.PathLike[str] | None = None) -> list[UsedFile]:
    """File nguồn của các file đã mux THÀNH CÔNG (kết quả có trên đĩa): MKV gốc, phụ đề ngoài, file nguồn audio ghép.

    KHÔNG đề xuất: file vẫn cần cho dòng chưa mux / mux lỗi (vd dùng chung), file nằm trong ``Output``, chính file kết
    quả, file không còn trên đĩa. Thứ tự: phim gốc → phụ đề → nguồn audio ghép."""

    def key(p: str | os.PathLike[str]) -> str:
        return os.path.normcase(os.path.abspath(os.fspath(p)))

    pending: set[str] = set()
    outs: set[str] = set()
    used: dict[str, UsedFile] = {}
    for p in plans:
        files = [(p.src, "video"), *[(e.path, "sub") for e in p.ext_subs], *[(t.source, "donor") for t in p.donor.tracks]]
        r = p.result
        if r is not None and r.ok and r.out is not None and Path(r.out).is_file():
            outs.add(key(r.out))
            for f, kind in files:
                u = used.setdefault(key(f), UsedFile(Path(f), kind))
                if Path(r.out).name not in u.used_for:
                    u.used_for.append(Path(r.out).name)
        else:
            pending.update(key(f) for f, _k in files)
    order = {"video": 0, "sub": 1, "donor": 2}
    out = [u for k, u in used.items() if k not in pending and k not in outs and u.path.is_file()
           and not (out_dir and tools.is_under(u.path, out_dir))]
    out.sort(key=lambda u: (order.get(u.kind, 9), os.fspath(u.path).lower()))
    return out


@dataclass
class MuxReport:
    ok: int = 0
    warning: int = 0
    skipped: int = 0
    failed: int = 0
    pending: int = 0  # con "Can chon"
    stopped: bool = False
    log_path: Path | None = None


class MuxSession:
    """Bước ③. ``tool_paths`` (cho test): mkvmerge / mkvextract / ffmpeg / ffprobe; bỏ trống = tự tìm."""

    def __init__(self, target_dir: str | os.PathLike[str], cfg: Mapping[str, Any], secrets: Mapping[str, str],
                 job: tools.Job | None = None, http: Any = None, sleep: Callable[[float], None] = time.sleep,
                 tool_paths: Mapping[str, str | None] | None = None):
        self.td = Path(target_dir)
        self.cfg = cfg
        self.job = job or tools.Job()
        paths = dict(tool_paths) if tool_paths is not None else tools.find_all_tools(cfg, self.td)
        self.mkvmerge = paths.get("mkvmerge")
        self.mkvextract = paths.get("mkvextract")
        self.ffmpeg = paths.get("ffmpeg")
        self.ffprobe = paths.get("ffprobe")
        self.temp = Path(tempfile.mkdtemp(prefix="muxstudio_mux_"))
        self.json_cache: dict = {}
        self.ff_cache: dict = {}
        self.name_cache: dict = {}
        self.ver = tools.mkvmerge_version(self.mkvmerge, self.job) if self.mkvmerge else tools.ToolVersion(False, 0, "")
        self.f_def, self.f_for = tools.mkvmerge_flag_names(self.ver.major)
        self._ui_lang: list[str] | None = None
        tk = secrets.get("TmdbApiKey", "")
        self.tmdb = TmdbClient(tk, cfg["TmdbLang"], cfg["TmdbRetry"], http, sleep, self.job) if tk else None
        vk = secrets.get("TvdbApiKey", "")
        self.tvdb = TvdbClient(vk, secrets.get("TvdbPin", ""), cfg["TvdbLang"], cfg["TvdbSeasonType"], cfg["TmdbRetry"],
                               http, sleep, self.job) if vk else None
        self.db = ""  # database da chon cho luot nay
        self.audio_rule: AudioRule | None = None
        self.audio_inputs: dict[str, AudioInput] = {}
        self.sub_choices: dict[str, SubChoice] = {}
        self.donor_offsets: dict[str, float] = {}
        self.name_overrides: dict[str, naming.OutputName] = {}
        self.multi_en_auto = False
        self.donor_pool = D.donor_files(self.td, cfg["DonorFolder"]) if cfg["UseDonorAudio"] else []
        self.track_times = D.TrackTimes(self.mkvextract, self.temp / "subs", self.job, str(cfg["PgsTimingText"]))
        self.log = undo.RunLog(self.td, cfg["MuxLogDir"], "mux", cfg["KeepUndoRuns"])

    def set_job(self, job: tools.Job) -> None:
        """Giao diện: phiên sống qua nhiều lần bấm nút, mỗi lần một ``Job`` mới (log + nút Dừng của lần đó)."""
        self.job = job
        self.track_times.job = job
        for cl in (self.tmdb, self.tvdb):
            if cl is not None:
                cl.job = job

    # ------------------------------------------------------------------ chung
    @property
    def out_dir(self) -> Path:
        return self.td / "Output"

    @property
    def dbs(self) -> dict[str, Any]:
        return {"tmdb": self.tmdb, "tvdb": self.tvdb}

    def _key(self, p: Path) -> str:
        return os.path.normcase(os.path.abspath(p))

    def excluded_dirs(self) -> list[Path]:
        c = self.cfg
        names = ["Output", c["DonorFolder"], tools.LEGACY_POOL_FOLDER, c["SubFolderName"], c["ExtractFolder"],
                 c["SubFetchLogDir"], c["SubPrepLogDir"], c["MuxLogDir"]]
        return [self.td / n for n in names if n]

    def scan(self) -> list[Path]:
        """Quét file phim (cả thư mục con nếu bật); bỏ qua ``Output`` và thư mục audio nguồn (§5.3)."""
        exts = [e for e in self.cfg["VideoExts"]] or list(MUX_EXTS_DEFAULT)
        files = tools.walk_files(self.td, bool(self.cfg["ScanSubFolders"]), exts, self.excluded_dirs())
        if self.donor_pool:
            self.job.info(f"Ghép audio: {len(self.donor_pool)} file nguồn trong '{self.cfg['DonorFolder']}' → lấy "
                          + ", ".join(naming.lang_name(x) for x in self.cfg["DonorLangs"]))
            if not self.mkvextract:
                self.job.warn("  [!] Thiếu mkvextract → không đo được độ lệch bằng phụ đề.")
            if not self.ffmpeg:
                self.job.warn("  [!] Thiếu ffmpeg → file nguồn khác FPS sẽ bị bỏ qua.")
        return files

    def db_status(self) -> dict[str, ConnStatus]:
        """Cạnh mỗi lựa chọn database: *sẵn sàng* / *chưa điền key* / *key bị từ chối* / *đăng nhập lỗi*."""
        st = {"tmdb": self.tmdb.check() if self.tmdb else ConnStatus(None, "TMDB: chưa điền key"),
              "tvdb": self.tvdb.check() if self.tvdb else ConnStatus(None, "TVDB: chưa điền key"),
              "none": ConnStatus(True, "Không đổi tên — giữ tên file gốc")}
        return st

    def ui_lang_args(self) -> list[str]:
        """``--ui-language en_US`` nếu bản mkvmerge có (log tiếng Anh dễ đọc; kết quả vẫn quyết định theo mã thoát)."""
        if self._ui_lang is None:
            self._ui_lang = []
            if self.mkvmerge:
                r = tools.run([self.mkvmerge, "--ui-language", "list"], self.job, timeout=30)
                if r.code == 0 and re.search(r"(?m)^\s*en_US\b", r.output):
                    self._ui_lang = ["--ui-language", "en_US"]
        return list(self._ui_lang)

    def _json(self, p: Path) -> dict | None:
        return probe.mkv_json(p, self.mkvmerge, self.job, self.json_cache)

    # ------------------------------------------------------------------ lap ke hoach
    def plan_all(self, db: str | None, files: Sequence[Path] | None = None) -> list[MuxPlan]:
        if db not in ("tmdb", "tvdb", "none"):
            raise ValueError("Phải chọn database đặt tên: tmdb, tvdb hoặc none (không có mặc định).")
        self.db = db
        files = list(files) if files is not None else self.scan()
        if not files:
            self.job.error(f"[!] Không tìm thấy file video nào trong: {self.td}")
            return []
        if not self.mkvmerge:
            self.job.error("[LỖI] Không tìm thấy mkvmerge. Chọn thư mục MKVToolNix trong Cài đặt.")
            return []
        if not self.ver.ok:
            self.job.error(f"[LỖI] Tìm thấy mkvmerge nhưng KHÔNG chạy được: {self.mkvmerge} — thường do thiếu DLL hoặc "
                           "Windows chặn file tải từ Internet (chuột phải › Properties › Unblock).")
            return []
        if not self.ffprobe:
            self.job.warn("[!] Không tìm thấy ffprobe → bitrate chỉ là ƯỚC LƯỢNG theo codec/số kênh.")
        self.job.info(f"Tìm thấy {len(files)} file. Đặt tên theo: {db.upper() if db != 'none' else 'giữ tên gốc'}")
        plans = []
        for i, f in enumerate(files, 1):
            self.job.check()
            self.job.progress(i, len(files), f.name)
            plans.append(self.plan_file(f))
        n = {k: sum(1 for p in plans if p.status == k) for k in ("ok", "need_choice", "skip", "exists", "fail")}
        self.job.head(f"KẾ HOẠCH: {n['ok']} sẵn sàng | {n['need_choice']} cần chọn | "
                      f"{n['skip'] + n['exists']} bỏ qua | {n['fail']} lỗi")
        return plans

    def plan_file(self, f: Path) -> MuxPlan:
        c = self.cfg
        rel = os.path.relpath(f, self.td) if tools.is_under(f, self.td) else f.name
        k = self._key(f)
        name = self.name_overrides.get(k) or naming.resolve_output_name(f.stem, self.db or "none", self.dbs, c,
                                                                        self.name_cache)
        plan = MuxPlan(f, rel, name)
        self.job.head(f"=== {rel}")
        if name.note and (name.status != "keep" or self.db != "none"):
            self.job.log(f"  [T] {name.note}", "ok" if name.status == "ok" else "warn")
        if name.episode_note:
            self.job.info(f"  [i] {name.episode_note}")
        if name.status == "need_choice":
            plan.status = "need_choice"
            plan.reasons.append(f"chọn tên: {name.note}")
        plan.out_base = name.base
        out_dir = self.out_dir / name.rel_dir if name.rel_dir else self.out_dir
        plan.out_path = out_dir / f"{name.base}.mkv"
        if c["SkipIfOutputExists"]:
            hit = self._already(plan, f)
            if hit is not None:
                plan.status = "exists"
                plan.reasons.append(f"đã có trong Output: {hit.name}")
                self.job.dim(f"  [>] Đã có trong Output: {hit.name} → bỏ qua.")
                return plan
        j = self._json(f)
        plan.json = j
        if j is None:
            plan.status = "fail"
            plan.reasons.append("không đọc được cấu trúc file (hỏng hoặc không hỗ trợ)")
            self.job.error("  [!] Không đọc được cấu trúc file (hỏng hoặc không hỗ trợ).")
            return plan
        # BUOC 1: video
        for t in probe.tracks(j, "video"):
            plan.video_ids.append(int(t.get("id", 0)))
            nm = naming.lang_name(probe.track_lang(t))
            if nm:
                plan.video_names[int(t.get("id", 0))] = nm
        # BUOC 2: audio
        self._plan_audio(plan, f, j)
        if plan.status == "skip":
            return plan
        # BUOC 3-5: phu de
        self._plan_subs(plan, f, j)
        # BUOC 5b: the codec
        if c["AddCodecTag"]:
            if name.is_episode and not c["CodecTagForTv"]:
                plan.notes.append("phim bộ → không gắn thẻ codec")
            else:
                vs = probe.ffprobe_streams(f, self.ffprobe, "v:0", self.job, self.ff_cache)
                defa = next((t for t in probe.tracks(j, "audio") if int(t.get("id", -1)) == plan.default_audio), None)
                tag = codec_tag(j, vs[0] if vs else None, f.stem, defa, list(c["CodecTagParts"]))
                if tag:
                    plan.codec_tag = tag
                    plan.out_base = f"{name.base} - {tag}"
                    plan.out_path = out_dir / f"{plan.out_base}.mkv"
                    self.job.info(f"  [#] Thẻ codec: {tag}")
        # BUOC 5c: audio ghep
        have = [plan.lang_override.get(a.id, a.code) for a in plan.audio if a.keep]
        plan.donor = D.plan_donor(f, j, have, self.donor_pool, c, self._json, self.track_times, bool(self.ffmpeg),
                                  self.donor_offsets.get(k), self.job)
        if plan.donor.tracks:
            tm = plan.donor.timing
            self.job.info(f"  [~] Ghép audio từ: {plan.donor.donor.name if plan.donor.donor else ''}")
            if tm:
                self.job.log(f"      Lệch: {plan.donor.tracks[0].offset_ms:+d} ms | {tm.how}", "dim" if tm.sure else "warn")
                if plan.donor.tracks[0].stretch:
                    self.job.warn(f"      Khác FPS: {tm.name} → sẽ đổi tốc độ audio (encode lại)")
        elif plan.donor.note:
            self.job.dim(f"  [-] Ghép audio: {plan.donor.note}")
        return plan

    def _already(self, plan: MuxPlan, f: Path) -> Path | None:
        """``SkipIfOutputExists``: đã có (tên mới, tên gốc của lần trước, hoặc bản có thẻ codec ``<tên> - …``)."""
        assert plan.out_path is not None
        if plan.out_path.exists():
            return plan.out_path
        legacy = self.out_dir / f"{f.stem}.mkv"
        if legacy.exists():
            return legacy
        d = plan.out_path.parent
        if self.cfg["AddCodecTag"] and d.is_dir():
            pre = f"{plan.out_base} - ".lower()
            for e in sorted(os.scandir(d), key=lambda e: e.name.lower()):
                if e.is_file() and e.name.lower().endswith(".mkv") and e.name.lower().startswith(pre):
                    return Path(e.path)
        return None

    def _plan_audio(self, plan: MuxPlan, f: Path, j: dict) -> None:
        c = self.cfg
        tr = probe.tracks(j, "audio")
        if not tr:
            plan.notes.append("không có audio track nào")
            self.job.warn("  [!] File không có audio track nào.")
            return
        ff = probe.ffprobe_bitrates(probe.ffprobe_streams(f, self.ffprobe, "a", self.job, self.ff_cache))
        allt: list[AudioTrack] = []
        for t in tr:
            p = t.get("properties") or {}
            br, src = probe.audio_bitrate(t, ff)
            a = AudioTrack(int(t.get("id", 0)), probe.track_lang(t), probe.audio_codec_name(t),
                           probe.channel_name(p.get("audio_channels")), br, src, str(p.get("track_name") or ""))
            if c["RemoveCommentaryAudio"] and probe.is_commentary(p):
                a.keep, a.reason = False, "Commentary"
            elif c["RemoveDescriptiveAudio"] and probe.is_descriptive(p):
                a.keep, a.reason = False, "Audio Description"
            allt.append(a)
        plan.audio = allt
        info = [a for a in allt if a.keep]
        if not info:
            self.job.warn("  [!] Tất cả audio đều bị nhận diện là bình luận → giữ nguyên toàn bộ cho an toàn.")
            for a in allt:
                a.keep, a.reason = True, ""
            info = list(allt)
        for a in allt:
            if not a.keep:
                self.job.dim(f"  [-] Audio  #{a.id}: XOÁ - {a.reason}" + (f" ('{a.track_name}')" if a.track_name else ""))
        plan.needs_audio_table = bool(c["AskWhichAudio"]) and len(info) >= int(c["AskAudioMinTracks"])
        inp = self.audio_inputs.get(self._key(f))
        if inp is not None:
            keep = [a for a in info if a.id not in inp.drop_ids]
            if not keep:
                self.job.warn("  [!] Không thể xoá HẾT audio → giữ nguyên tất cả.")
                keep = info
            for a in info:
                if a not in keep:
                    a.keep, a.reason = False, "theo lựa chọn của bạn"
            info = keep
        elif self.audio_rule is not None:
            keep2 = self.audio_rule.apply(info)
            if keep2 is None:
                self.job.warn("  [!] Quy tắc đã nhớ sẽ xoá hết audio của file này → bỏ qua quy tắc, giữ tất cả.")
            else:
                plan.rule_applied = self.audio_rule.describe()
                self.job.dim(f"  [i] Áp dụng quy tắc đã nhớ: {plan.rule_applied}")
                for a in info:
                    if a not in keep2:
                        a.keep, a.reason = False, "theo quy tắc"
                info = keep2
        # und
        und = [a for a in info if a.code == "und"]
        if und:
            if len(info) == 1:
                info[0].code = "en"
                plan.lang_override[info[0].id] = "en"
                self.job.warn(f"  [*] Audio #{info[0].id}: ngôn ngữ 'und' + chỉ có 1 track → đặt thành 'en'")
            elif c["SkipOnAmbiguousUnd"]:
                plan.status = "skip"
                plan.reasons.append(f"có {len(info)} audio, trong đó {len(und)} track không rõ ngôn ngữ ('und') — "
                                    "hãy gán ngôn ngữ bằng MKVToolNix GUI rồi chạy lại")
                self.job.warn(f"  [!] {plan.reasons[-1]} → BỎ QUA file này.")
                return
        # >= 2 audio tieng Anh (Q3 / TD-23)
        en = [a for a in info if a.code == "en"]
        keep_all = (inp is not None and inp.keep_all_default_highest) or self.multi_en_auto
        user_choice = inp is not None and (inp.drop_ids or inp.keep_all_default_highest)
        if len(en) > 1 and c["SkipOnMultipleEnAudio"] and not (keep_all or user_choice):
            plan.status = "need_choice"
            plan.reasons.append(f"có {len(en)} audio tiếng Anh (#" + ", #".join(str(a.id) for a in en) + ") — tick "
                                "track muốn xoá trong bảng audio, hoặc bấm 'Giữ cả hai, Default = bitrate cao nhất'")
            self.job.warn(f"  [!] {plan.reasons[-1]}")
        for a in info:
            self.job.ok(f"  [+] Audio  #{a.id}: GIỮ - {a.label}  {probe.format_kbps(a.bitrate)}")
        if len(en) == 1:
            plan.default_audio = en[0].id
        elif len(en) > 1:
            plan.default_audio = sorted(en, key=lambda a: -a.bitrate)[0].id
        elif info:
            plan.default_audio = info[0].id
        for a in allt:
            a.default = a.id == plan.default_audio

    def _plan_subs(self, plan: MuxPlan, f: Path, j: dict) -> None:
        c = self.cfg
        exts = external_subs(f, c)
        ext_langs = [e.code for e in exts] if c["ExternalSubOverridesInternal"] else []
        if ext_langs:
            self.job.info("  [~] Có sub ngoài (" + ", ".join(naming.lang_name(x) for x in ext_langs)
                          + ") → thay thế toàn bộ sub trong MKV cùng ngôn ngữ.")
        ints = internal_subs(j, c, ext_langs)
        plan.sub_items = sub_auto_plan(f, j, c)
        ch = self.sub_choices.get(self._key(f))
        if ch is not None:  # bang chon phu de theo tap
            exts = [e for e in exts if e.code in ch.ext]
            for s in ints:
                s.keep = s.id in ch.int_ids
                s.reason = "theo bảng chọn" if s.keep else "bỏ theo bảng chọn"
            shown = [f"{e.label} (ngoài)" for e in exts] + [f"{s.label} (#{s.id})" for s in ints if s.keep]
            self.job.info("  [#] THEO BẢNG CHỌN → giữ: " + (", ".join(shown) if shown else "không giữ phụ đề nào"))
        for s in ints:
            if s.keep:
                self.job.ok(f"  [+] Sub    #{s.id} : GIỮ - {s.label}" + (f" ({s.reason})" if s.reason not in ("giữ", "theo bảng chọn") else ""))
            else:
                self.job.dim(f"  [-] Sub    #{s.id} : XOÁ - {naming.lang_name(s.code) or s.code} ({s.reason})")
        plan.ext_subs, plan.int_subs = exts, ints
        plan.default_sub = pick_default_sub(exts, ints, c["SubDefaultPriority"])
        kind, val = plan.default_sub
        for s in ints:
            s.default = kind == "int" and s.id == val
        for i, e in enumerate(exts):
            self.job.info(f"  [+] Sub ngoài: {e.label} ({e.path.suffix.lstrip('.').upper()})"
                          + (" [*Default]" if kind == "ext" and i == val else ""))

    # ------------------------------------------------------------------ sua ke hoach
    def set_audio_choice(self, plan: MuxPlan, drop_ids: Sequence[int], apply_to_rest: bool | None = None) -> AudioRule | None:
        """Bảng audio của một file: tick track muốn XOÁ. ``apply_to_rest`` (ô "Áp dụng cho mọi file còn lại",
        mặc định theo ``RememberAudioChoice``) → dựng quy tắc "nhớ cái GIỮ" cho các file sau."""
        self.audio_inputs[self._key(plan.src)] = AudioInput(set(int(x) for x in drop_ids))
        mode = str(self.cfg["RememberAudioChoice"])
        if apply_to_rest is None:
            apply_to_rest = mode in ("ask", "always")
        if mode == "never":
            apply_to_rest = False
        rule = None
        if apply_to_rest and drop_ids:
            info = [a for a in plan.audio if a.reason not in ("Commentary", "Audio Description")]
            kept = [a for a in info if a.id not in set(drop_ids)]
            dropped = [a for a in info if a.id in set(drop_ids)]
            if kept:
                rule = AudioRule.from_choice(kept, dropped)
                self.audio_rule = rule
                self.job.info(f"  [i] Đã nhớ: {rule.describe()}")
        return rule

    def keep_all_audio(self, plan: MuxPlan | None = None) -> None:
        """Q3: "Giữ cả hai, Default = bitrate cao nhất" cho một file (``plan``) hoặc cho mọi file (``None``)."""
        if plan is None:
            self.multi_en_auto = True
        else:
            self.audio_inputs[self._key(plan.src)] = AudioInput(set(), True)

    def set_sub_choice(self, plan: MuxPlan, ext_codes: Sequence[str], int_ids: Sequence[int]) -> None:
        self.sub_choices[self._key(plan.src)] = SubChoice(set(ext_codes), set(int(x) for x in int_ids))

    def reset_sub_choices(self) -> None:
        """Nút "Trả về mặc định" của bảng chọn phụ đề."""
        self.sub_choices.clear()

    def set_donor_offset(self, plan: MuxPlan, ms: float | None) -> None:
        k = self._key(plan.src)
        if ms is None:
            self.donor_offsets.pop(k, None)
        else:
            self.donor_offsets[k] = float(ms)

    def choose_name(self, plan: MuxPlan, cand_id: int) -> None:
        """Dòng "Cần chọn": bạn chọn một kết quả database. Mọi file cùng khoá (cùng series) dùng luôn lựa chọn."""
        db = self.dbs.get(plan.name.db)
        if db is None:
            return
        out = naming.choose_candidate(db, plan.src.stem, cand_id, plan.name, self.cfg, self.name_cache)
        self.name_overrides.pop(self._key(plan.src), None)
        if out.status != "ok":
            self.name_overrides[self._key(plan.src)] = out

    def search_name(self, plan: MuxPlan, title: str, year: int | None = None) -> naming.OutputName | None:
        """"Tìm lại với tên…": tìm lại trên database đã chọn cho lượt này; dòng thành "Cần chọn" với kết quả mới
        (gọi ``plan_file`` lại để thấy). Chưa chọn database / database không dùng được → ``None``."""
        db = self.dbs.get(self.db)
        if not naming.db_usable(db):
            return None
        out = naming.search_candidates(db, plan.src.stem, plan.name, title, year)
        self.name_overrides[self._key(plan.src)] = out
        return out

    def keep_original_name(self, plan: MuxPlan) -> None:
        self.name_overrides[self._key(plan.src)] = naming.keep_name(plan.src.stem, "Giữ tên file gốc (bạn chọn).",
                                                                    plan.name.kind)

    def keep_original_names(self, plans: Sequence[MuxPlan]) -> list[MuxPlan]:
        """Bấm "Bắt đầu" khi còn dòng "Cần chọn" TÊN → (bạn đồng ý) giữ tên gốc cho các dòng đó; trả kế hoạch mới."""
        out = []
        for p in plans:
            if p.name.status == "need_choice":
                self.keep_original_name(p)
                p = self.plan_file(p.src)
            out.append(p)
        return out

    def set_name(self, plan: MuxPlan, new_base: str) -> None:
        """Bạn sửa tay tên mới trên bảng tổng quan."""
        b = naming.safe_filename(new_base)
        if b:
            self.name_overrides[self._key(plan.src)] = naming.OutputName(b, plan.name.rel_dir, plan.name.db, "ok",
                                                                         "Tên do bạn sửa.", plan.name.kind)

    def replan(self, plans: Sequence[MuxPlan]) -> list[MuxPlan]:
        return [self.plan_file(p.src) for p in plans]

    # ------------------------------------------------------------------ dung lenh mkvmerge (SPEC 4.4)
    def build_args(self, plan: MuxPlan, out_path: Path | None = None) -> list[str]:
        """Danh sách tham số mkvmerge (không gồm đường dẫn mkvmerge) — đúng 8 quy tắc ở SPEC 4.4."""
        c = self.cfg
        j = plan.json
        out = os.fspath(out_path or plan.out_path)
        f_def, f_for = self.f_def, self.f_for
        audio_all = probe.tracks(j, "audio")
        sub_all = probe.tracks(j, "subtitles")
        keep_audio = plan.keep_audio
        keep_subs = plan.keep_subs
        donor_tracks = plan.donor.tracks
        donor_default = bool(c["DonorSetDefault"]) and bool(donor_tracks)
        cmd = ["-o", out]
        if c["SetTitleFromName"]:
            cmd += ["--title", plan.out_base]
        if audio_all and keep_audio and len(keep_audio) < len(audio_all):
            cmd += ["--audio-tracks", ",".join(str(x) for x in keep_audio)]
        if sub_all:
            if not keep_subs:
                cmd.append("--no-subtitles")
            elif len(keep_subs) < len(sub_all):
                cmd += ["--subtitle-tracks", ",".join(str(s.id) for s in keep_subs)]
        for tid, code in plan.lang_override.items():
            cmd += ["--language", f"{tid}:{naming.lang_for_mux(code, self.ver.major)}"]
        names: dict[int, str] = dict(plan.video_names)
        names.update({a.id: a.label for a in plan.audio if a.keep and a.label})
        names.update({s.id: s.label for s in keep_subs if s.label})
        for tid in [*plan.video_ids, *keep_audio, *(s.id for s in keep_subs)]:
            if names.get(tid):
                cmd += ["--track-name", f"{tid}:{names[tid]}"]
        for tid in keep_audio:
            cmd += [f_def, f"{tid}:{1 if tid == plan.default_audio and not donor_default else 0}"]
        dk, dv = plan.default_sub
        for s in keep_subs:
            cmd += [f_def, f"{s.id}:{1 if dk == 'int' and s.id == dv else 0}", f_for, f"{s.id}:{1 if s.forced else 0}"]
        cmd.append(os.fspath(plan.src))
        for i, e in enumerate(plan.ext_subs):
            cs = encoding.mkvmerge_charset(e.path, e.code)
            if cs:
                cmd += ["--sub-charset", f"0:{cs}"]
            cmd += ["--language", f"0:{naming.lang_for_mux(e.code, self.ver.major)}", "--track-name", f"0:{e.label}",
                    f_def, f"0:{1 if dk == 'ext' and i == dv else 0}", f_for, "0:0", os.fspath(e.path)]
        donor_base = 1 + len(plan.ext_subs)
        for k, dt in enumerate(donor_tracks):
            tid = dt.input_tid
            cmd += ["--no-video", "--no-subtitles", "--no-chapters", "--no-attachments", "--no-global-tags",
                    "--audio-tracks", str(tid), "--language", f"{tid}:{naming.lang_for_mux(dt.code, self.ver.major)}",
                    "--track-name", f"{tid}:{dt.name}", f_def, f"{tid}:{1 if donor_default and k == 0 else 0}",
                    f_for, f"{tid}:0"]
            if dt.offset_ms != 0:
                cmd += ["--sync", f"{tid}:{dt.offset_ms}"]
            cmd.append(os.fspath(dt.input_path))
        if c["UseTrackOrder"]:
            order = [f"0:{v}" for v in plan.video_ids]
            donor_order = [f"{donor_base + k}:{dt.input_tid}" for k, dt in enumerate(donor_tracks)]
            if donor_default:
                order += donor_order
            if plan.default_audio >= 0 and plan.default_audio in keep_audio:
                order.append(f"0:{plan.default_audio}")
            order += [f"0:{a}" for a in keep_audio if a != plan.default_audio]
            if not donor_default:
                order += donor_order
            if dk == "ext":
                order.append(f"{dv + 1}:0")
            elif dk == "int":
                order.append(f"0:{dv}")
            for code in [str(x).lower() for x in c["SubDefaultPriority"]]:
                for i, e in enumerate(plan.ext_subs):
                    if e.code == code and not (dk == "ext" and i == dv):
                        order.append(f"{i + 1}:0")
                for s in keep_subs:
                    if s.code == code and not (dk == "int" and s.id == dv):
                        order.append(f"0:{s.id}")
            for s in keep_subs:
                if f"0:{s.id}" not in order:
                    order.append(f"0:{s.id}")
            if order:
                cmd = cmd[:2] + ["--track-order", ",".join(order)] + cmd[2:]
        return cmd

    def preview_commands(self, plan: MuxPlan) -> list[str]:
        """Nút "Xem lệnh": ffmpeg (đổi tốc độ audio ghép) + mkvmerge — đường dẫn tạm ghi rõ, không ghi file nào."""
        if plan.status not in ("ok", "need_choice") or plan.json is None:
            return []
        lines = []
        tmpdir = os.path.join("<thư mục tạm>", "")
        saved = [(dt, dt.prepared) for dt in plan.donor.tracks]
        try:
            for n, dt in enumerate(plan.donor.tracks):
                if dt.stretch:
                    fake = Path(f"{tmpdir}a_{n}.mka")
                    lines.append(" ".join(_q(a) for a in dt.ffmpeg_args(self.ffmpeg or "ffmpeg", fake)))
                    dt.prepared = fake
            argv = [self.mkvmerge or "mkvmerge", *self.ui_lang_args(), *self.build_args(plan)]
            lines.append(" ".join(_q(a) for a in argv))
        finally:
            for dt, p in saved:
                dt.prepared = p
        return lines

    # ------------------------------------------------------------------ thuc thi
    def _ensure_dir(self, d: Path) -> None:
        if d.exists():
            return
        missing = []
        p = d
        while not p.exists():
            missing.append(p)
            p = p.parent
        d.mkdir(parents=True, exist_ok=True)
        for m in reversed(missing):
            self.log.add(undo.MKDIR, "", m)

    def execute(self, plans: Sequence[MuxPlan]) -> MuxReport:
        rep = MuxReport()
        try:
            for n, p in enumerate(plans, 1):
                if self.job.should_stop():
                    rep.stopped = True
                    break
                self.job.progress(n, len(plans), p.src.name)
                if not p.selected:
                    rep.skipped += 1
                    continue
                if p.status == "need_choice":
                    rep.pending += 1
                    self.log.add("Skip", p.src, "", "; ".join(p.reasons), "Cần chọn")
                    continue
                if p.status != "ok":
                    rep.skipped += 1 if p.status != "fail" else 0
                    rep.failed += 1 if p.status == "fail" else 0
                    self.log.add("Skip" if p.status != "fail" else "Fail", p.src, "", "; ".join(p.reasons), p.status)
                    continue
                res = self._mux_one(p)
                p.result = res
                if res.ok:
                    rep.ok += 1
                    rep.warning += res.warning
                else:
                    rep.failed += 1
        except tools.Cancelled:
            rep.stopped = True
            self.job.warn("[!] Đã dừng ngay — file đang mux dở đã bị xoá.")
        finally:
            shutil.rmtree(self.temp / "donor", ignore_errors=True)
            rep.log_path = self.log.save()
        self.job.head(f"TỔNG KẾT: Thành công: {rep.ok}" + (f" ({rep.warning} có cảnh báo)" if rep.warning else "")
                      + f" | Bỏ qua: {rep.skipped} | Cần chọn: {rep.pending} | Lỗi: {rep.failed}")
        if rep.log_path:
            self.job.info(f"Nhật ký (để hoàn tác): {self.cfg['MuxLogDir']}\\{rep.log_path.name}")
        return rep

    def trash_sources(self, files: Sequence[UsedFile | str | os.PathLike[str]]) -> TrashReport:
        """Đưa file nguồn đã dùng vào Thùng rác (tắt ``DeleteToRecycleBin`` → xoá hẳn), ghi ``Delete`` vào nhật ký
        của lượt mux này (màn hình Hoàn tác ghi rõ: khôi phục bằng tay từ Thùng rác)."""
        rb = bool(self.cfg["DeleteToRecycleBin"])
        rep = TrashReport()
        for f in files:
            path = Path(f.path if isinstance(f, UsedFile) else f)
            if not path.is_file():
                continue
            if undo.trash(path, rb):
                rep.done.append(path)
                self.log.add(undo.DELETE, path, "", "file nguồn đã mux xong" + (" → Thùng rác" if rb else " → XOÁ HẲN"))
                self.job.ok(f"  [🗑] {path.name}")
            else:
                rep.failed.append(path)
                self.job.error(f"  [!] Không xoá được: {path}")
        if rep.done:
            self.log.save()
        self.job.head(f"DỌN FILE NGUỒN: {len(rep.done)} file " + ("vào Thùng rác" if rb else "đã XOÁ HẲN")
                      + (f" | {len(rep.failed)} lỗi" if rep.failed else ""))
        return rep

    def _mux_one(self, p: MuxPlan) -> MuxResult:
        assert p.out_path is not None
        res = MuxResult()
        self.job.head(f"=== {p.rel}")
        # 1. audio ghep khac FPS -> ffmpeg ra thu muc tam
        keep_tracks = []
        for dt in p.donor.tracks:
            if dt.stretch and not D.prepare(dt, self.ffmpeg or "ffmpeg", self.temp / "donor", self.job):
                continue
            keep_tracks.append(dt)
        p.donor.tracks = keep_tracks
        # 2. thu muc dich (tao luc thuc thi, co nhat ky)
        self._ensure_dir(p.out_path.parent)
        tmp = p.out_path.with_name(p.out_path.stem + ".muxstudio-tmp.mkv")
        tmp.unlink(missing_ok=True)
        argv = [self.mkvmerge or "mkvmerge", *self.ui_lang_args(), *self.build_args(p, tmp)]
        try:
            r = tools.run(argv, self.job)
        except tools.Cancelled:
            tmp.unlink(missing_ok=True)
            raise
        res.code = r.code
        res.output = r.output.splitlines()
        if r.code in (0, 1) and tmp.is_file():
            if p.out_path.exists():
                if undo.trash(p.out_path, bool(self.cfg["DeleteToRecycleBin"])):
                    self.log.add(undo.DELETE, p.out_path, "", "thay bằng kết quả mux mới")
                else:
                    tmp.unlink(missing_ok=True)
                    self.job.error(f"  [!] Không dọn được file cũ: {p.out_path.name}")
                    self.log.add("Fail", p.src, p.out_path, "không dọn được file cũ", "FAIL")
                    return res
            os.replace(tmp, p.out_path)
            res.ok, res.out, res.warning = True, p.out_path, r.code == 1
            dinfo = (" ghép=" + ",".join(f"{d.code}@{d.offset_ms}ms" for d in p.donor.tracks)) if p.donor.tracks else ""
            detail = f"audio giữ={','.join(map(str, p.keep_audio))} sub giữ={','.join(str(s.id) for s in p.keep_subs)}{dinfo}"
            if r.code == 1:
                self.job.warn(f"  → XONG (có cảnh báo): {p.out_path}")
                for ln in [x for x in res.output if re.search(r"(?i)warning", x)][:5]:
                    self.job.dim(f"     {ln}")
            else:
                self.job.ok(f"  → THÀNH CÔNG. Đã lưu: {p.out_path}")
            self.log.add(undo.CREATE, p.src, p.out_path, detail, "OK-Warning" if r.code == 1 else "OK")
        else:
            tmp.unlink(missing_ok=True)  # >= 2: xoa file dau ra do dang
            self.job.error(f"  [!] THẤT BẠI (mã thoát {r.code}):")
            for ln in res.output[-8:]:
                self.job.dim(f"     {ln}")
            self.log.add("Fail", p.src, p.out_path, " | ".join(res.output[-3:]), "FAIL")
        for dt in p.donor.tracks:
            if dt.prepared is not None:
                dt.prepared.unlink(missing_ok=True)
                dt.prepared = None
        return res

    # ------------------------------------------------------------------ dong
    def close(self) -> None:
        shutil.rmtree(self.temp, ignore_errors=True)

    def __enter__(self) -> "MuxSession":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()
