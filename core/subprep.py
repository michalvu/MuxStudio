"""Bước ② — Chuẩn bị phụ đề (thay SubPrep_v20.bat). docs/SPEC.md mục 4.2, §6.5.

Mẫu "lập kế hoạch → thực thi" (§4) như bước ①:

* ``PrepSession.plan_extract_refs`` → ``extract_refs``: trích phụ đề gốc (không SDH, theo ``RefSubLangs``;
  chỉ có PGS thì đọc mốc thời gian) từ MKV vào ``srt_goc``.
* ``PrepSession.plan_rename`` → ``execute_rename``: đổi tên phụ đề trong ``srt_can_sync`` cho khớp tên phim.
* ``PrepSession.plan_sync`` → ``execute_sync``: mỗi file làm trên BẢN SAO tạm (TĐ-12): chuẩn hoá UTF-8 →
  cắt recap (chỉ SRT, TĐ-14) → alass (mốc audio: LUÔN trích WAV, thứ tự thử TĐ-19) → sửa khối 00:00:00 →
  kiểm tra sức khoẻ (luôn chạy, TĐ-20) → chép kết quả cạnh file phim.
* ``PrepSession.cleanup_targets`` → ``execute_cleanup``: dọn file nguồn (Thùng rác).

Hàm ``plan_*`` không ghi gì vào thư mục phim (chỉ thư mục tạm). Mọi thao tác ghi vào CÙNG một nhật ký
hoàn tác ``_subprep_logs\\run_*.csv`` cho cả lượt (đổi tên + đồng bộ + dọn dẹp), lưu sau mỗi bước.
Phụ đề ASS giữ nguyên là ASS (Q7): thao tác chỉ làm được với SRT thì bỏ qua và ghi rõ lý do.
"""
from __future__ import annotations

import csv
import datetime as _dt
import os
import shutil
import tempfile
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from . import encoding, episodes, naming, pgs, probe, srt, timing, tools, undo

LEVEL_TEXT = {0: "TỐT", 1: "CẦN XEM", 2: "CÓ VẤN ĐỀ"}
LEVEL_LOG = {0: "ok", 1: "warn", 2: "error"}


def _num(x: float, d: int = 2) -> str:
    """Số như PowerShell in ra (``[math]::Round(x, d)``): dấu chấm, bỏ số 0 thừa."""
    return timing.fmt_inv(round(x, d), d)


def _signed(x: float, d: int = 2) -> str:
    s = _num(x, d)
    return s if s.startswith("-") else "+" + s


def _same_path(a: str | os.PathLike[str], b: str | os.PathLike[str]) -> bool:
    return os.path.normcase(os.path.abspath(os.fspath(a))) == os.path.normcase(os.path.abspath(os.fspath(b)))


def _quote(a: str) -> str:
    return f'"{a}"' if (not a or any(ch in a for ch in ' \t"&()[]{}^=;!\'+,`~')) else a


# ==========================================================================
#  CẮT RECAP, SỬA KHỐI 00:00:00 (thao tác trên khối SRT — không ghi file)
# ==========================================================================


def remove_leading_extra(blocks: Sequence[srt.Block] | None, ref_times: Sequence[float] | None,
                         min_votes: int = 20, n_ref: int = 400, n_tgt: int = 100) -> tuple[list[srt.Block] | None, int]:
    """``Remove-LeadingExtra``: cắt khối "tóm tắt tập trước" ở đầu file mà phim không có.

    Cần ≥ 40 dòng. Đo quan hệ thời gian trên 70% CUỐI (≥ ``min_votes`` mốc khớp); tìm dòng đầu tiên mà
    5 dòng liên tiếp đều có mốc gốc trong ~±1,5 s (bảng băm ô 1 giây); chỉ tìm trong 30% đầu; huỷ nếu
    phần giữ lại < 60%. Trả (khối giữ lại, số dòng cắt) hoặc (``None``, 0) nếu không cắt.
    """
    if not ref_times or not blocks or len(blocks) < 40:
        return None, 0
    n = len(blocks)
    skip = int(round(n * 0.30))  # [int] cua PowerShell: lam tron ve so chan
    tail = [b.start for b in blocks[skip:]]
    rel = timing.timing_relation(ref_times, tail, n_ref, n_tgt)
    if rel is None or rel.votes < min_votes:
        return None, 0
    bins = timing.RefBins(ref_times)
    first_good = -1
    limit = min(n - 5, int(round(n * 0.30)))
    for i in range(limit + 1):
        run = 0
        for j in range(i, min(i + 5, n)):
            if bins.near(blocks[j].start * rel.scale + rel.offset):
                run += 1
            else:
                break
        if run >= 5:
            first_good = i
            break
    if first_good <= 0:
        return None, 0
    keep = list(blocks[first_good:])
    if len(keep) < n * 0.6:  # cat qua nhieu -> nghi ngo
        return None, 0
    return keep, first_good


def repair_collapsed_cues(blocks: Sequence[srt.Block] | None, max_zero: int = 2) -> tuple[list[srt.Block] | None, int]:
    """``Repair-CollapsedCues``: > ``max_zero`` dòng bắt đầu < 0,05 s (alass đẩy khối về trước mốc 0) →
    bỏ các dòng đó, nếu còn ≥ 10 dòng. Trả (khối giữ lại, số dòng bỏ) hoặc (``None``, 0)."""
    if not blocks:
        return None, 0
    zero = sum(1 for b in blocks if b.start < 0.05)
    if zero <= max_zero:
        return None, 0
    keep = [b for b in blocks if b.start >= 0.05]
    if len(keep) < 10:
        return None, 0
    return keep, zero


# ==========================================================================
#  SỨC KHOẺ PHỤ ĐỀ SAU KHI ĐỒNG BỘ (Test-SubHealth)
# ==========================================================================


@dataclass
class Health:
    level: int  # 0 TOT, 1 CAN XEM, 2 CO VAN DE
    flags: list[str]
    codes: list[str]  # ma dau hieu (cho GUI / test): zero overlap lost overrun weak fps offset stretch badchar few unreadable
    cues: int
    shift_first: float | None = None  # alass dich o dau phim (giay)
    shift_last: float | None = None  # ... o cuoi phim
    res_offset: float | None = None  # con lech so voi phu de goc
    res_votes: int = -1  # so moc khop voi phu de goc (-1 = khong do)
    res_fps: str = ""  # con lech FPS (ten ty le)
    fmt: str = ""

    @property
    def text(self) -> str:
        return LEVEL_TEXT[self.level]

    @property
    def spread(self) -> float | None:
        if self.shift_first is None or self.shift_last is None:
            return None
        return self.shift_last - self.shift_first


def sub_health(path: str | os.PathLike[str], ref_times: Sequence[float] | None, video_dur: float, cues_before: int,
               src_times: Sequence[float] | None, min_votes: int = 20, max_zero: int = 2,
               n_ref: int = 400, n_tgt: int = 100) -> Health:
    """``Test-SubHealth``: 7 dấu hiệu, mức 0/1/2.

    Khác .bat (TĐ-14): đọc mốc của SRT, ASS/SSA, VTT (bản cũ chỉ đọc SRT nên mọi kết quả ASS bị báo
    "CÓ VẤN ĐỀ: không đọc được nội dung"). Với ASS bỏ dấu hiệu "chồng lấn" vì ASS hiện nhiều dòng cùng
    lúc là bình thường (biển hiệu, lời bài hát, dòng trên/dưới).
    """
    sp = srt.cue_spans(path)
    if not sp or not sp[1]:
        return Health(2, ["không đọc được nội dung"], ["unreadable"], 0)
    fmt, spans = sp
    if fmt == "ass":
        spans = sorted(spans)
    n = len(spans)
    out_times = [s for s, _ in spans]  # SRT: thu tu trong file (nhu .bat)
    flags: list[str] = []
    codes: list[str] = []
    level = 0

    def add(code: str, msg: str, lv: int) -> None:
        nonlocal level
        codes.append(code)
        flags.append(msg)
        level = max(level, lv)

    h = Health(0, flags, codes, n, fmt=fmt)
    if src_times and len(src_times) > 1 and n > 1:
        m = min(len(src_times), n)
        h.shift_first = out_times[0] - src_times[0]
        h.shift_last = out_times[m - 1] - src_times[m - 1]

    # 1. Dong bi don ve 00:00:00
    zero = sum(1 for s in out_times if s < 0.05)
    if zero > max_zero:
        add("zero", f"{zero} dòng dính ở 00:00:00", 2)
    # 2. Chong lan
    if fmt != "ass":
        ov = sum(1 for i in range(1, n) if spans[i][0] < spans[i - 1][1] - 0.05)
        if ov > max(5, n * 0.05):
            add("overlap", f"{ov} chỗ chồng lấn", 1)
    # 3. Mat dong so voi truoc khi sync
    if cues_before > 0 and n < cues_before * 0.95:
        add("lost", f"mất {cues_before - n}/{cues_before} dòng", 1)
    # 4. Vuot thoi luong phim
    last_end = spans[n - 1][1] if fmt == "srt" else max(e for _, e in spans)
    if video_dur > 0 and n > 0 and last_end > video_dur + 60:
        add("overrun", f"dòng cuối {_num(last_end / 60, 1)}p > phim {_num(video_dur / 60, 1)}p", 2)
    # 5. Doi chieu voi moc goc
    if ref_times:
        rel = timing.timing_relation(ref_times, out_times, n_ref, n_tgt)
        if rel:
            h.res_offset, h.res_votes = rel.offset, rel.votes
            if rel.fps_mismatch:
                h.res_fps = rel.name
            if rel.votes < min_votes:
                add("weak", f"chỉ {rel.votes} mốc khớp", 2)
            elif rel.fps_mismatch:
                add("fps", f"còn lệch FPS ({rel.name})", 1)
            elif abs(rel.offset) > 1.0:
                add("offset", f"vẫn lệch {_num(rel.offset)}s", 1)
    # 6. Co gian bat thuong
    if h.spread is not None and abs(h.spread) > 5.0:
        add("stretch", f"co giãn {_num(h.spread, 1)}s giữa đầu và cuối", 1)
    # 7. Hong ma hoa / qua it dong
    try:
        if "�" in encoding.read_text(path):
            add("badchar", "ký tự hỏng (sai bảng mã)", 1)
    except OSError:
        pass
    if n < 50:
        add("few", f"chỉ có {n} dòng", 1)
    h.level = level
    return h


# ==========================================================================
#  CHỈ MỤC MỐC + GHÉP PHỤ ĐỀ VỚI MỐC (Get-RefIndex, Select-RefVariant, Resolve-Ref)
# ==========================================================================


@dataclass
class RefItem:
    """Một file mốc: file phim (mốc audio) hoặc phụ đề gốc."""

    file: Path
    info: episodes.EpisodeInfo | None  # ca phep doan "khong chac" (confident=False)
    is_video: bool
    canon: str  # ten da bo hau to ngon ngu: '.en'/'.vi'/'' cua cung 1 phim gom lam mot
    has_tag: bool
    tokens: list[str]
    year: int | None
    timing_path: Path | None = None  # file doc moc (khac `file` khi la .sup: ban SRT tam)
    pending: tuple[Path, Path] | None = None  # (.srt tam, .srt canh .sup) - ghi luc thuc thi
    _times: list[float] | None = field(default=None, repr=False, compare=False)
    _times_read: bool = field(default=False, repr=False, compare=False)

    @property
    def key(self) -> str:
        return self.info.label if self.info else "?"

    @property
    def confident(self) -> bool:
        return bool(self.info and self.info.confident)

    @property
    def path_for_timing(self) -> Path:
        return self.timing_path or self.file

    def times(self) -> list[float] | None:
        if not self._times_read:
            self._times = None if self.is_video else srt.cue_times(self.path_for_timing)
            self._times_read = True
        return self._times


def select_ref_variant(cands: Sequence[RefItem]) -> RefItem | None:
    """``Select-RefVariant``: nhiều file cùng một phim → video > phụ đề không hậu tố > còn lại."""
    if not cands:
        return None
    if len(cands) == 1:
        return cands[0]
    for r in cands:
        if r.is_video:
            return r
    for r in cands:
        if not r.has_tag:
            return r
    return cands[0]


class RefIndex:
    """``Get-RefIndex``: chỉ mục các file mốc theo tập, theo số tập, theo tên."""

    def __init__(self, kind: str, items: list[RefItem], videos: list[RefItem], root: Path):
        self.kind = kind  # 'sub' | 'video'
        self.items = items
        self.videos = videos
        self.root = root
        self.by_key: dict[str, list[RefItem]] = {}
        self.by_ep: dict[int, list[RefItem]] = {}
        self.by_name: dict[str, RefItem] = {}
        self.video_by_name: dict[str, Path] = {}
        for r in items:
            if r.info:
                self.by_key.setdefault(r.key, []).append(r)
        for lst in self.by_key.values():
            for r in lst:
                assert r.info is not None
                self.by_ep.setdefault(r.info.episode, []).append(r)
        for r in items:
            for nm in (r.file.stem.lower(), r.canon.lower()):
                cur = self.by_name.get(nm)
                if cur is None or (not r.has_tag and cur.has_tag):
                    self.by_name[nm] = r
        for v in videos:
            self.video_by_name.setdefault(v.canon.lower(), v.file)

    @classmethod
    def build(cls, files: Iterable[Path], kind: str, root: Path, video_exts: Iterable[str],
              sup_fn: Any = None) -> "RefIndex":
        """Dựng chỉ mục từ danh sách file đã quét. ``sup_fn(path) -> (srt_tạm, đích) | None`` đọc mốc từ
        ``.sup`` bạn tự bỏ vào (``Convert-LooseSupFiles``) — chỉ khi chưa có ``.srt`` cùng tên."""
        vexts = {e.lower() for e in video_exts}
        items: list[RefItem] = []
        videos: list[RefItem] = []
        for f in files:
            ext = f.suffix.lower()
            is_vid = ext in vexts
            timing_path: Path | None = None
            pending = None
            if ext == ".sup":
                if kind == "video" or sup_fn is None or f.with_suffix(".srt").is_file():
                    continue
                got = sup_fn(f)
                if got is None:
                    continue
                timing_path, pending = got[0], got
            stem = f.stem
            base, tag = naming.split_lang_tag(stem)
            canon = stem if is_vid else base
            it = RefItem(f, episodes.parse_episode(stem, allow_guess=True), is_vid, canon, bool(tag),
                         naming.title_tokens(canon), naming.title_year(canon), timing_path, pending)
            if is_vid:
                videos.append(it)
            if (kind == "video" and not is_vid) or (kind == "sub" and is_vid):
                continue
            items.append(it)
        return cls(kind, items, videos, root)

    def summary(self) -> str:
        what = "phụ đề gốc" if self.kind == "sub" else "file phim"
        return f"[i] Mốc: {len(self.items)} {what} - {len(self.by_key)} tập."

    def source_dirs(self, top: int = 4) -> list[tuple[str, int]]:
        """Mốc lấy từ những thư mục nào (để phát hiện quét nhầm)."""
        counts: dict[str, int] = {}
        for r in self.items:
            d = os.fspath(r.file.parent)
            if tools.is_under(d, self.root):
                d = os.path.relpath(d, self.root)
                d = "" if d == "." else d
            counts[d or "(thư mục gốc)"] = counts.get(d or "(thư mục gốc)", 0) + 1
        order = {k: i for i, k in enumerate(counts)}
        return sorted(counts.items(), key=lambda kv: (-kv[1], order[kv[0]]))[:top]

    def source_text(self) -> str:
        return ", ".join(f"{d} ({n})" for d, n in self.source_dirs())

    def canons(self) -> list[str]:
        return list(dict.fromkeys(r.canon for r in self.items))


@dataclass
class RefMatch:
    ref: RefItem | None
    how: str = ""  # cach ghep duoc
    reason: str = ""  # ly do KHONG ghep duoc (cot "Ly do")
    details: list[str] = field(default_factory=list)


def _infos_conflict(a: episodes.EpisodeInfo | None, b: episodes.EpisodeInfo | None) -> bool:
    """Như ``episodes.episode_conflict`` nhưng trên kết quả đã nhận diện (chỉ xét khi cả hai CHẮC CHẮN)."""
    if a is None or b is None or not a.confident or not b.confident:
        return False
    if a.has_season and b.has_season:
        return a.season != b.season or a.episode != b.episode
    return a.episode != b.episode


def resolve_ref(idx: RefIndex, sub_base_name: str, sub_file_count: int,
                min_score: float = 0.5, min_margin: float = 0.15) -> RefMatch:
    """``Resolve-Ref``: tìm file mốc cho một phụ đề — đúng 4 tầng của .bat, trả kèm LÝ DO khi không ghép.

    1. Trùng tên tuyệt đối (sau khi đã đổi tên).
    2. Theo TÊN PHIM: điểm từ khoá, lọc năm; ≥ ``min_score``, một phim duy nhất, hơn hạng nhì ≥ ``min_margin``.
    3. Theo season + số tập — chỉ khi số tập là thật (không phải phép đoán).
    4. Cả hai bên chỉ có đúng một file → ghép thẳng.

    Sửa lỗi .bat (TĐ-31): mốc ghi số tập KHÁC số tập của phụ đề bị loại trước tầng 2 và tầng 4 (bản cũ:
    anime ``- 0643`` luôn "nhập nhằng"; thiếu phim tập 5 thì phụ đề tập 5 bị ghép vào tập 6); tầng 3 báo
    "nhập nhằng" thật khi nhiều phim khác nhau cùng khớp (bản cũ luôn lấy file đầu tiên).
    """
    base, _ = naming.split_lang_tag(sub_base_name)
    # 1. Khop tuyet doi theo ten
    hit = idx.by_name.get(base.lower())
    if hit is not None:
        return RefMatch(hit, "trùng tên")

    my_info = episodes.parse_episode(base)
    pool = [r for r in idx.items if not _infos_conflict(my_info, r.info)]
    n_conflict = len(idx.items) - len(pool)

    # 2. Theo TEN PHIM (danh cho phim le)
    my_tok = naming.title_tokens(base)
    my_year = naming.title_year(base)
    if my_tok:
        scored: list[tuple[RefItem, float]] = []
        for r in pool:
            if my_year and r.year and my_year != r.year:
                continue  # nam khac nhau ro rang -> chac chan khong phai cung phim
            sc = naming.title_score(my_tok, r.tokens)
            if sc > 0:
                scored.append((r, sc))
        if scored:
            scored.sort(key=lambda x: -x[1])
            best = scored[0][1]
            top = [r for r, s in scored if s == best]
            top_canon = list(dict.fromkeys(r.canon for r in top))
            lower = [s for _, s in scored if s < best]
            second = lower[0] if lower else 0.0
            if best >= min_score and len(top_canon) == 1 and (best - second) >= min_margin:
                return RefMatch(select_ref_variant(top), f"theo tên phim (điểm {_num(best)})")
            if best >= min_score and len(top_canon) > 1:
                return RefMatch(None, reason=f"nhập nhằng theo tên phim ({len(top_canon)} mốc cùng điểm {_num(best)})",
                                details=top_canon[:4])

    # 3. Theo season + so tap - CHI khi so tap la that
    info = episodes.parse_episode(base, allow_guess=True)
    if info is None:
        return RefMatch(None, reason="không nhận diện được số tập")
    if not info.confident:
        det = ["Từ khoá đọc ra: [" + ", ".join(my_tok) + "]" + (f" năm {my_year}" if my_year else "")]
        near = sorted(((naming.title_score(my_tok, r.tokens), r) for r in idx.items), key=lambda x: -x[0])[:3]
        for s, r in near:
            det.append(f"gần nhất: {_num(s)}  {r.canon}" + (f" ({r.year})" if r.year else ""))
        det.append(f"→ Cần điểm ≥ {_num(min_score)}. Đổi tên phụ đề cho giống tên phim hơn.")
        return RefMatch(None, reason="không tìm được phim tương ứng", details=det)
    if info.has_season:
        cands = list(idx.by_key.get(info.label, []))
        if not cands:
            cands = [r for r in idx.by_ep.get(info.episode, []) if not (r.info and r.info.has_season)]
    else:
        cands = list(idx.by_ep.get(info.episode, []))
    cands = [r for r in cands if r.confident]

    if not cands:
        # 4. Chi ghep thang khi CA HAI ben deu chi co dung mot file -> khong the nham
        canons = idx.canons()
        if len(canons) == 1 and sub_file_count == 1 and pool:
            return RefMatch(select_ref_variant(idx.items), "ghép thẳng (chỉ có 1 phụ đề và 1 mốc)")
        det = [f"đọc ra: {info.label}, tên phim: '{naming.title_key(base)}'",
               "Các mốc đang có: " + " | ".join(canons[:4])]
        if n_conflict:
            det.append(f"(đã loại {n_conflict} mốc ghi số tập khác)")
        return RefMatch(None, reason="không tìm được mốc", details=det)

    distinct = list(dict.fromkeys(r.canon for r in cands))
    if len(distinct) > 1:
        return RefMatch(None, reason=f"nhập nhằng tập {info.label} ({len(distinct)} file mốc khớp)",
                        details=[*distinct, "→ Đặt tên phụ đề kèm season (vd S02E05) rồi chạy lại."])
    return RefMatch(select_ref_variant(cands), f"theo tập {info.label}")


# ==========================================================================
#  KẾ HOẠCH (dataclass hiện trên GUI)
# ==========================================================================


@dataclass
class ExtractRow:
    """Một file MKV cần trích phụ đề gốc (``Invoke-ExtractRefSubs``)."""

    video: Path
    track: probe.RefTrack | None
    final: Path | None
    reason: str = ""  # '' = se trich
    selected: bool = True
    status: str = ""

    @property
    def ok(self) -> bool:
        return not self.reason


@dataclass
class RenameRow:
    """Một dòng của bảng xem trước đổi tên (``Invoke-Rename``)."""

    src: Path
    lang: str  # ma ngon ngu se gan ('' = khong ro)
    detected: str  # ma doan tu thu muc con / hau to
    new_path: Path | None = None
    ref: RefItem | None = None
    how: str = ""
    reason: str = ""  # '' = se doi ten
    details: list[str] = field(default_factory=list)
    warn: str = ""  # canh bao DO: file nam trong thu muc ngon ngu khac lua chon
    ep_label: str = "?"
    selected: bool = True
    status: str = ""

    @property
    def ok(self) -> bool:
        return not self.reason


@dataclass(frozen=True)
class AudioChoice:
    """Audio track làm mốc: ``auto`` (track Default, không có thì track đầu), ``index`` (số thứ tự từ 0),
    ``lang`` (theo ngôn ngữ)."""

    mode: str = "auto"
    value: int | str | None = None

    @classmethod
    def from_config(cls, cfg: Mapping[str, Any]) -> "AudioChoice":
        m = cfg["SyncAudioMode"]
        if m == "index":
            return cls("index", int(cfg["SyncAudioIndex"]))
        if m == "lang":
            return cls("lang", str(cfg["SyncAudioLang"]))
        return cls("auto")  # 'ask' = GUI hien bang de chon; loi khong co lua chon -> tu dong


def pick_audio_stream(streams: Sequence[probe.AudioStream], choice: AudioChoice) -> tuple[int, str, bool]:
    """Audio track làm mốc của MỘT file theo lựa chọn → ``(số thứ tự, mô tả, không_có)``. ``không_có`` = số thứ tự /
    ngôn ngữ đã chọn không có trong file này nên phải dùng track Default (không có thì track đầu)."""

    def auto(prefix: str = "") -> tuple[int, str]:
        for st in streams:
            if st.default:
                return st.index, prefix + st.describe()
        return 0, prefix + (streams[0].describe() if streams else "track đầu")

    if not streams:
        return 0, "không đọc được danh sách audio → thử track đầu", False
    if choice.mode == "index":
        i = int(choice.value or 0)
        if 0 <= i < len(streams):
            return i, streams[i].describe(), False
        return (*auto(f"không có #{i} → "), True)
    if choice.mode == "lang":
        want = str(choice.value or "")
        hit = next((st for st in streams if naming.lang_match(st.lang, want)), None)
        if hit is not None:
            return hit.index, hit.describe(), False
        return (*auto(f"không có '{want}' → "), True)
    return (*auto(), False)


@dataclass
class VideoAudio:
    """Audio track của một file phim (bảng audio ở thẻ ② — TĐ-35: liệt kê MỌI file, không chỉ file mẫu)."""

    video: Path
    ep_label: str  # 'S01E05' / 'E643' / '' (phim le)
    streams: list[probe.AudioStream]

    @property
    def layout(self) -> tuple[tuple[str, str, int], ...]:
        """Bố cục audio để so giữa các file: (ngôn ngữ, codec, số kênh) theo thứ tự track."""
        return tuple((a.lang.lower(), a.codec.lower(), a.channels) for a in self.streams)


def audio_layout_groups(files: Sequence[VideoAudio]) -> list[list[VideoAudio]]:
    """Gom các file theo bố cục audio, nhóm nhiều file nhất đứng trước (bằng nhau: nhóm gặp trước)."""
    groups: dict[Any, list[VideoAudio]] = {}
    for f in files:
        groups.setdefault(f.layout, []).append(f)
    return sorted(groups.values(), key=lambda g: -len(g))


    def describe(self) -> str:
        if self.mode == "index":
            return f"theo số thứ tự = {self.value}"
        if self.mode == "lang":
            return f"theo ngôn ngữ = {self.value}"
        return "tự động (audio Default của file)"


@dataclass(frozen=True)
class Attempt:
    use_wav: bool  # mốc = WAV trích ra (True) hay file phim/phụ đề gốc (False)
    with_flags: bool
    note: str = ""


@dataclass
class SyncResult:
    ok: bool = False
    code: int = 1
    health: Health | None = None
    cut: int = 0
    fixed: int = 0
    shift: float | None = None
    rejected: bool = False
    out: Path | None = None
    attempt: int = -1  # lan thu nao thanh cong (dem tu 0)
    notes: list[str] = field(default_factory=list)
    output_tail: list[str] = field(default_factory=list)

    @property
    def level(self) -> int | None:
        return self.health.level if self.health else None


@dataclass
class SyncPair:
    """Một dòng của bảng cặp đồng bộ (``Invoke-Sync``)."""

    sub: Path
    lang: str
    mode: str  # 'sub' (moc = phu de goc) | 'audio' (moc = audio cua file phim)
    ref: RefItem | None = None
    how: str = ""
    reason: str = ""  # '' = se dong bo
    details: list[str] = field(default_factory=list)
    out_path: Path | None = None
    video: Path | None = None  # file phim (thoi luong; mốc audio)
    relation: timing.TimingRelation | None = None  # du bao truoc khi sync
    relation_src: str = ""
    audio_index: int | None = None  # 0:a:N de trich WAV (None = khong trich duoc WAV)
    audio_desc: str = ""
    flags: list[str] = field(default_factory=list)
    extra: list[str] = field(default_factory=list)
    attempts: list[Attempt] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    selected: bool = True
    status: str = ""
    result: SyncResult | None = None

    @property
    def ok(self) -> bool:
        return not self.reason

    @property
    def ref_kind_text(self) -> str:
        if self.mode == "sub":
            return "phụ đề gốc"
        return f"audio #{self.audio_index}" if self.audio_index is not None else "video"

    def forecast(self) -> list[str]:
        """Dòng "dự báo" của bảng cặp (độ lệch, hệ số, cảnh báo lệch FPS)."""
        r = self.relation
        if r is None:
            return []
        out = [f"dự báo: lệch {_signed(r.offset)}s, hệ số {_num(r.scale, 4)} ({r.votes} mốc)"]
        if r.fps_mismatch:
            out.append(f"LỆCH FPS: {r.name} — nếu chỉ dịch thời gian, cuối phim sẽ sai khoảng {_num(r.drift, 1)}s.")
        return out

    def alass_args(self, at: Attempt, ref_path: str | os.PathLike[str], sub_path: str | os.PathLike[str],
                   out_path: str | os.PathLike[str]) -> list[str]:
        a: list[str] = []
        if at.with_flags:
            a += self.flags
        a += self.extra
        return a + [os.fspath(ref_path), os.fspath(sub_path), os.fspath(out_path)]


@dataclass
class Processed:
    sub: Path
    ref: Path
    out: Path


@dataclass
class PrepCounts:
    videos: int
    subs: int
    refs: int


@dataclass
class ExtractReport:
    ok: int = 0
    skipped: int = 0
    errors: int = 0
    stopped: bool = False
    log_path: Path | None = None


@dataclass
class RenameReport:
    ok: int = 0
    skipped: int = 0
    errors: int = 0
    log_path: Path | None = None


@dataclass
class SyncReport:
    ok: int = 0
    skipped: int = 0
    rejected: int = 0
    errors: int = 0
    good: int = 0
    check: int = 0
    bad: int = 0
    stopped: bool = False
    log_path: Path | None = None
    health_path: Path | None = None


@dataclass
class CleanupPlan:
    """Hộp thoại dọn dẹp (``Invoke-Cleanup``): chỉ file nguồn của cặp đã có kết quả > 32 byte cạnh phim."""

    done: int
    sub_files: list[Path]
    ref_files: list[Path]
    to_recycle_bin: bool

    @property
    def where(self) -> str:
        return "Thùng rác (khôi phục được)" if self.to_recycle_bin else "XOÁ HẲN (không khôi phục được)"

    def targets(self, which: str) -> list[Path]:
        """``which``: ``both`` (cả hai) | ``subs`` (chỉ srt_can_sync) | ``refs`` (chỉ srt_goc) | ``none``."""
        out: list[Path] = []
        if which in ("both", "subs"):
            out += self.sub_files
        if which in ("both", "refs"):
            out += self.ref_files
        return out


@dataclass
class CleanupReport:
    deleted: int = 0
    failed: list[Path] = field(default_factory=list)
    left: dict[str, int] = field(default_factory=dict)  # ten thu muc -> so file con lai


def cleanup_targets(processed: Sequence[Processed], ex_dir: Path | None, to_recycle_bin: bool = True) -> CleanupPlan | None:
    safe = [p for p in processed if p.out.is_file() and p.out.stat().st_size > 32]
    if not safe:
        return None
    subs = [Path(x) for x in dict.fromkeys(os.fspath(p.sub) for p in safe) if os.path.isfile(x)]
    refs = [Path(x) for x in dict.fromkeys(os.fspath(p.ref) for p in safe)
            if os.path.isfile(x) and ex_dir is not None and tools.is_under(x, ex_dir)]
    if not subs and not refs:
        return None
    return CleanupPlan(len(safe), subs, refs, to_recycle_bin)


# ==========================================================================
#  PHIÊN LÀM VIỆC BƯỚC ②
# ==========================================================================


def _as_cmd(v: str | Sequence[str] | None) -> list[str] | None:
    if not v:
        return None
    return [v] if isinstance(v, (str, os.PathLike)) else [os.fspath(x) for x in v]


class PrepSession:
    """Bước ②. ``tool_paths`` (cho test): ``{"mkvmerge":…, "mkvextract":…, "ffmpeg":…, "ffprobe":…,
    "alass": đường dẫn hoặc danh sách lệnh}``; bỏ trống = tự tìm (``tools.find_all_tools``)."""

    def __init__(self, target_dir: str | os.PathLike[str], cfg: Mapping[str, Any], job: tools.Job | None = None,
                 tool_paths: Mapping[str, Any] | None = None):
        self.td = Path(target_dir)
        self.cfg = cfg
        self.job = job or tools.Job()
        self.temp = Path(tempfile.mkdtemp(prefix="muxstudio_prep_"))
        paths = dict(tool_paths) if tool_paths is not None else tools.find_all_tools(cfg, self.td)
        self.mkvmerge: str | None = paths.get("mkvmerge")
        self.mkvextract: str | None = paths.get("mkvextract")
        self.ffmpeg: str | None = paths.get("ffmpeg")
        self.ffprobe: str | None = paths.get("ffprobe")
        self.alass_cmd = _as_cmd(paths.get("alass"))
        self.json_cache: dict = {}
        self.ffprobe_cache: dict = {}
        self.log = undo.RunLog(self.td, cfg["SubPrepLogDir"], "subprep", cfg["KeepUndoRuns"])
        self.processed: list[Processed] = []
        self.health_rows: list[dict[str, Any]] = []
        self._flag_cache: dict[tuple[str, ...], tools.AlassFlags] = {}
        self._audio_cache: dict[str, tuple[int | None, str]] = {}
        self._goc: dict[str, tuple[Path, str]] | None = None

    def set_job(self, job: tools.Job) -> None:
        """Giao diện: phiên sống qua nhiều lần bấm nút, mỗi lần một ``Job`` mới (log + nút Dừng của lần đó)."""
        self.job = job

    # ------------------------------------------------------------------ thu muc
    @property
    def sub_dir(self) -> Path:
        return self.td / self.cfg["SubFolderName"]

    @property
    def ex_dir(self) -> Path | None:
        return self.td / self.cfg["ExtractFolder"] if self.cfg["ExtractFolder"] else None

    @property
    def ref_dir(self) -> Path:
        return self.td / self.cfg["RefFolderName"] if self.cfg["RefFolderName"] else self.td

    @property
    def n_ref(self) -> int:
        return int(self.cfg["FpsSampleRef"])

    @property
    def n_tgt(self) -> int:
        return int(self.cfg["FpsSampleTgt"])

    def excluded_dirs(self) -> list[Path]:
        """Thư mục KHÔNG quét làm mốc (``Test-ExcludedPath``). TĐ-13: luôn cộng thêm thư mục audio nguồn,
        kho ``srt_pool``, ``srt_can_sync`` (và các thư mục nhật ký) — thiếu ``audio_nguon`` từng làm mỗi tập
        có 2 video cùng số tập ⇒ "nhập nhằng" hàng loạt."""
        c = self.cfg
        names = [*c["RefExcludeFolders"], c["SubFolderName"], c["DonorFolder"], tools.LEGACY_POOL_FOLDER,
                 c["SubPrepLogDir"], c["SubFetchLogDir"], c["MuxLogDir"]]
        keep_scan = {str(c["ExtractFolder"]).lower(), str(c["RefFolderName"]).lower()} - {""}
        out: list[Path] = []
        seen: set[str] = set()
        for n in names:
            if n and str(n).lower() not in keep_scan and str(n).lower() not in seen:
                seen.add(str(n).lower())
                out.append(self.td / n)
        return out

    def is_excluded(self, path: str | os.PathLike[str]) -> bool:
        return any(tools.is_under(path, d) for d in self.excluded_dirs())

    def list_sub_files(self) -> list[Path]:
        return tools.walk_files(self.sub_dir, bool(self.cfg["ScanSubFolders"]), self.cfg["SubExts"])

    def list_videos(self) -> list[Path]:
        skip = self.excluded_dirs() + ([self.ex_dir] if self.ex_dir else [])
        return tools.walk_files(self.td, bool(self.cfg["ScanSubFolders"]), self.cfg["VideoExts"], skip)

    def counts(self) -> PrepCounts:
        """§5.2 mục 1: số phim, số phụ đề trong ``srt_can_sync``, số phụ đề gốc trong ``srt_goc``."""
        ex = self.ex_dir
        refs = 0
        if ex is not None and ex.is_dir():
            refs = len(tools.walk_files(ex, bool(self.cfg["ScanSubFolders"]), [*self.cfg["SubExts"], ".sup"]))
        return PrepCounts(len(self.list_videos()), len(self.list_sub_files()), refs)

    def sample_audio_streams(self) -> tuple[Path | None, list[probe.AudioStream]]:
        """Audio track của file mẫu (file phim đầu tiên) — bảng chọn audio làm mốc."""
        vids = self.list_videos()
        if not vids:
            return None, []
        return vids[0], probe.audio_streams(probe.ffprobe_streams(vids[0], self.ffprobe, "a", self.job, self.ffprobe_cache))

    def all_audio_streams(self) -> list[VideoAudio]:
        """Audio track của MỌI file phim (TĐ-35) — để thấy tập nào có audio nào trước khi chọn mốc."""
        vids = self.list_videos()
        out: list[VideoAudio] = []
        for i, v in enumerate(vids, 1):
            self.job.check()
            self.job.progress(i, len(vids), v.name)
            info = episodes.parse_episode(v.stem)
            streams = probe.audio_streams(probe.ffprobe_streams(v, self.ffprobe, "a", self.job, self.ffprobe_cache))
            out.append(VideoAudio(v, info.label if info else "", streams))
        return out

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

    def _replace_existing(self, dst: Path, why: str) -> bool:
        """Đích đã có file (khi tắt "bỏ qua") → đưa file cũ vào Thùng rác trước, không ghi đè mất (§7)."""
        if not dst.exists():
            return True
        if undo.trash(dst, bool(self.cfg["DeleteToRecycleBin"])):
            self.log.add(undo.DELETE, dst, "", why)
            return True
        self.job.error(f"  [!] Không dọn được file cũ: {dst.name}")
        return False

    def save_log(self) -> Path | None:
        return self.log.save()

    # ------------------------------------------------------------------ chi muc moc
    def _sup_timing(self, sup: Path) -> tuple[Path, Path] | None:
        if not self.cfg["PgsTimingFallback"]:
            return None
        d = self.temp / "sup"
        d.mkdir(parents=True, exist_ok=True)
        tmp = d / f"{uuid.uuid4().hex[:8]}_{sup.stem}.srt"
        n = pgs.pgs_to_timing_srt(sup, tmp, str(self.cfg["PgsTimingText"]))
        if n <= 0:
            self.job.warn(f"  [!] Không đọc được PGS: {sup.name}")
            return None
        self.job.dim(f"  [pgs] {sup.name} → {sup.stem}.srt ({n} mốc thời gian)")
        return tmp, sup.with_suffix(".srt")

    def build_index(self, mode: str) -> RefIndex | None:
        """``mode``: ``sub`` (mốc = phụ đề gốc) | ``audio`` (mốc = file phim). Hiện "Mốc lấy từ: …"."""
        c = self.cfg
        kind = "sub" if mode == "sub" else "video"
        dirs: list[Path] = []
        for d in (self.ref_dir, self.ex_dir):
            if d is not None and d.is_dir() and not any(_same_path(d, x) for x in dirs):
                dirs.append(d)
        if not dirs:
            self.job.error(f"[LỖI] Không tìm thấy thư mục mốc: {self.ref_dir}")
            return None
        exts = [*c["VideoExts"], *c["SubExts"]] + ([".sup"] if c["PgsTimingFallback"] and kind == "sub" else [])
        skip = self.excluded_dirs()
        files: list[Path] = []
        seen: set[str] = set()
        for d in dirs:
            for f in tools.walk_files(d, bool(c["ScanSubFolders"]), exts, skip):
                k = os.path.normcase(os.path.abspath(f))
                if k not in seen:
                    seen.add(k)
                    files.append(f)
        idx = RefIndex.build(files, kind, self.td, c["VideoExts"], self._sup_timing)
        if not idx.items:
            if kind == "sub":
                self.job.error(f"[LỖI] Không có phụ đề gốc nào trong '{c['ExtractFolder']}'.")
            else:
                self.job.error("[LỖI] Không tìm thấy file phim nào.")
            return None
        self.job.info(idx.summary())
        self.job.dim(f"    Mốc lấy từ: {idx.source_text()}")
        return idx

    def _resolve(self, idx: RefIndex, sub: Path, n_subs: int) -> RefMatch:
        return resolve_ref(idx, sub.stem, n_subs, float(self.cfg["TitleMinScore"]), float(self.cfg["TitleMinMargin"]))

    @staticmethod
    def _norm_lang(lang: str | None) -> str:
        x = (lang or "").strip().lower().lstrip(".")
        if x in ("", "auto"):
            return "auto"
        code = naming.lang_from_word(x) or x  # TD-15: 'cz' -> 'cs'
        naming.register_language(code)
        return code

    # ------------------------------------------------------------------ 0. trich phu de goc
    def plan_extract_refs(self) -> list[ExtractRow]:
        """Lập kế hoạch trích phụ đề gốc (chỉ đọc ``mkvmerge -J``, không ghi gì)."""
        c = self.cfg
        if not self.mkvmerge or not self.mkvextract:
            self.job.error("[LỖI] Không tìm thấy mkvmerge / mkvextract. Chọn thư mục MKVToolNix trong Cài đặt.")
            return []
        ex = self.ex_dir
        if ex is None:
            self.job.error("[LỖI] 'Thư mục phụ đề gốc' (ExtractFolder) không được để trống.")
            return []
        videos = [v for v in self.list_videos() if v.suffix.lower() == ".mkv"]
        if not videos:
            self.job.warn("[!] Không tìm thấy file .mkv nào để trích (chỉ file MKV mới trích được).")
            return []
        self.job.info(f"[i] Tìm thấy {len(videos)} file MKV.")
        rows: list[ExtractRow] = []
        for i, v in enumerate(videos, 1):
            self.job.progress(i, len(videos), v.name)
            j = probe.mkv_json(v, self.mkvmerge, self.job, self.json_cache)
            if j is None:
                rows.append(ExtractRow(v, None, None, "không đọc được file"))
                continue
            trk = probe.pick_ref_sub_track(j, list(c["RefSubLangs"]), bool(c["PgsTimingFallback"]))
            if trk is None:
                rows.append(ExtractRow(v, None, None, "không có phụ đề văn bản dùng được (có thể chỉ có PGS/VobSub)"))
                continue
            final = ex / (v.stem + (".srt" if trk.is_pgs else trk.ext))
            if c["SkipExistingRef"] and final.exists():
                rows.append(ExtractRow(v, trk, final, "đã có mốc"))
                continue
            rows.append(ExtractRow(v, trk, final))
        return rows

    def extract_refs(self, rows: Sequence[ExtractRow]) -> ExtractReport:
        """Trích theo kế hoạch vào ``srt_goc`` (qua thư mục tạm; ghi nhật ký hoàn tác)."""
        rep = ExtractReport(skipped=sum(1 for r in rows if not (r.ok and r.selected)))
        todo = [r for r in rows if r.ok and r.selected]
        tmpd = self.temp / "extract"
        tmpd.mkdir(parents=True, exist_ok=True)
        try:
            for n, row in enumerate(todo, 1):
                if self.job.should_stop():
                    rep.stopped = True
                    break
                self.job.progress(n, len(todo), row.video.name)
                trk, final = row.track, row.final
                assert trk is not None and final is not None
                raw = tmpd / f"{uuid.uuid4().hex[:8]}{trk.ext}"
                r = tools.run([self.mkvextract or "mkvextract", "tracks", os.fspath(row.video), f"{trk.id}:{os.fspath(raw)}"],
                              self.job)
                if not (r.code <= 1 and raw.is_file() and raw.stat().st_size > 32):
                    self.job.error(f"  [!] Trích thất bại: {row.video.name}")
                    for ln in r.output.splitlines()[-3:]:
                        self.job.dim(f"      {ln}")
                    row.status = "trích thất bại"
                    rep.errors += 1
                    continue
                src = raw
                extra = ""
                if trk.is_pgs:
                    src = raw.with_suffix(".srt")
                    cnt = pgs.pgs_to_timing_srt(raw, src, str(self.cfg["PgsTimingText"]))
                    raw.unlink(missing_ok=True)
                    if cnt <= 0:
                        self.job.error(f"  [!] Không đọc được mốc thời gian từ PGS: {row.video.name}")
                        row.status = "PGS hỏng"
                        rep.errors += 1
                        continue
                    extra = f", đọc {cnt} mốc thời gian"
                self._ensure_dir(final.parent)
                if not self._replace_existing(final, "thay bằng phụ đề gốc trích lại"):
                    rep.errors += 1
                    continue
                tools.copy_file(src, final)
                self.log.add(undo.CREATE, row.video, final, f"phụ đề gốc: {trk.describe()}")
                row.status = "đã trích"
                self.job.ok(f"  [+] {final.name}  ← {trk.describe()}{extra}")
                rep.ok += 1
        except tools.Cancelled:
            rep.stopped = True
            self.job.warn("[!] Đã dừng ngay.")
        finally:
            rep.log_path = self.save_log()
        self.job.head(f"TRÍCH: {rep.ok} thành công | {rep.skipped} bỏ qua | {rep.errors} lỗi")
        return rep

    # ------------------------------------------------------------------ 1. doi ten
    def plan_rename(self, mode: str = "sub", lang: str | None = "auto", idx: RefIndex | None = None) -> list[RenameRow]:
        """Bảng xem trước đổi tên (``Invoke-Rename``, phần lập kế hoạch). ``lang``: ``auto`` (tự nhận diện
        theo thư mục con cấp 1 / hậu tố) hoặc mã ép cố định."""
        lang = self._norm_lang(lang)
        idx = idx or self.build_index(mode)
        files = self.list_sub_files()
        if not files:
            self.job.warn(f"[!] Không có phụ đề nào trong '{self.cfg['SubFolderName']}'.")
            return []
        if idx is None:
            return [RenameRow(f, "", "", reason="không có mốc") for f in files]
        rows: list[RenameRow] = []
        seen: dict[str, str] = {}
        conflicts = 0
        for f in files:
            detected = naming.lang_tag_of_file(f, self.sub_dir)
            row = RenameRow(f, lang if lang != "auto" else detected, detected)
            rows.append(row)
            if lang == "auto":
                if not detected:
                    row.reason = "không đoán được ngôn ngữ"
                    row.details = [f"→ Đặt vào thư mục con '{self.cfg['SubFolderName']}\\vi\\' (hoặc cs/en), "
                                   "hoặc thêm hậu tố .vi vào tên file."]
                    continue
            elif detected and detected != lang:
                row.warn = f"file nằm trong thư mục '{detected}' nhưng bạn chọn '{lang}'"
                conflicts += 1
            m = self._resolve(idx, f, len(files))
            row.ref, row.how = m.ref, m.how
            if m.ref is None:
                row.reason, row.details = m.reason, m.details
                continue
            new_name = m.ref.canon + "." + row.lang + f.suffix.lower()
            new_path = f.parent / new_name
            row.new_path = new_path
            info = episodes.parse_episode(m.ref.file.stem, allow_guess=True)
            row.ep_label = info.label if info else "?"
            k = os.path.normcase(os.path.abspath(new_path))
            if f.name == new_name:
                row.reason = "đã đúng tên"
            elif k in seen:
                row.reason = f"trùng đích với '{seen[k]}'"
            elif new_path.exists() and not _same_path(new_path, f):
                row.reason = "đích đã tồn tại"
            else:
                seen[k] = f.name
        ok = [r for r in rows if r.ok]
        for r in rows:
            if r.ok:
                self.job.info(f"  [{r.ep_label}] {r.src.name}")
                self.job.ok(f"        → {r.new_path.name if r.new_path else ''}")
                if r.warn:
                    self.job.error(f"        [!] {r.warn} — kiểm tra kỹ trước khi đổi tên!")
            elif r.reason == "đã đúng tên":
                self.job.dim(f"  [=] Đã đúng tên: {r.src.name}")
            else:
                self.job.warn(f"  [-] KHÔNG ĐỔI TÊN: {r.src.name} — {r.reason}")
                for d in r.details:
                    self.job.dim(f"        {d}")
        if conflicts:
            self.job.error(f"[!] {conflicts} file nằm trong thư mục ngôn ngữ KHÁC với lựa chọn của bạn: "
                           f"sẽ gắn nhãn '{lang}' theo lựa chọn, KHÔNG theo thư mục.")
        self.job.head(f"KẾ HOẠCH ĐỔI TÊN: {len(ok)} file (bỏ qua {len(rows) - len(ok)})")
        return rows

    def execute_rename(self, rows: Sequence[RenameRow]) -> RenameReport:
        rep = RenameReport(skipped=sum(1 for r in rows if not (r.ok and r.selected)))
        for r in rows:
            if not (r.ok and r.selected) or r.new_path is None:
                continue
            if r.new_path.exists() and not _same_path(r.new_path, r.src):
                r.status = "đích đã tồn tại"
                self.job.error(f"  [-] File đích đã tồn tại: {r.new_path.name}")
                rep.errors += 1
                continue
            try:
                os.rename(r.src, r.new_path)
            except OSError as e:
                self.log.add("RenameError", r.src, r.new_path, str(e))
                r.status = f"lỗi: {e}"
                self.job.error(f"  [!] Lỗi: {e}")
                rep.errors += 1
                continue
            self.log.add(undo.RENAME, r.src, r.new_path, r.ep_label)
            r.status = "đã đổi tên"
            self.job.ok(f"  [OK] {r.ep_label}: {r.new_path.name}")
            rep.ok += 1
        rep.log_path = self.save_log()
        self.job.head(f"ĐỔI TÊN: {rep.ok} file | {rep.skipped} bỏ qua | {rep.errors} lỗi")
        return rep

    # ------------------------------------------------------------------ 2. dong bo
    def alass_flags(self, wanted: Sequence[str]) -> list[str]:
        key = tuple(str(w) for w in wanted)
        if key not in self._flag_cache:
            if not self.alass_cmd:
                return list(key)
            fl = tools.alass_supported_flags(self.alass_cmd, key, self.job)
            self._flag_cache[key] = fl
            if not fl.help_read and key:
                self.job.warn("  [!] Không đọc được 'alass --help' → cứ thử dùng đầy đủ tham số.")
            if fl.unsupported:
                self.job.warn(f"  [!] Bản alass này KHÔNG hỗ trợ: {', '.join(fl.unsupported)} — lớp chống cắt đoạn "
                              "bị giảm hiệu lực, hãy tin vào ngưỡng 'dịch tối đa'.")
        return list(self._flag_cache[key].ok)

    def _goc_ref(self, canon: str) -> tuple[Path, list[float]] | None:
        """Phụ đề gốc của phim trong ``srt_goc`` (TĐ-18: dùng để đo lệch FPS khi mốc là audio)."""
        if self._goc is None:
            self._goc = {}
            ex = self.ex_dir
            if ex is not None and ex.is_dir():
                for f in tools.walk_files(ex, bool(self.cfg["ScanSubFolders"]), self.cfg["SubExts"]):
                    base, tag = naming.split_lang_tag(f.stem)
                    k = base.lower()
                    if k not in self._goc or (not tag and self._goc[k][1]):
                        self._goc[k] = (f, tag)
        hit = self._goc.get(canon.lower())
        if not hit:
            return None
        t = srt.cue_times(hit[0])
        return (hit[0], t) if t else None

    def _pick_audio(self, video: Path, choice: AudioChoice) -> tuple[int | None, str]:
        """Chọn audio track làm mốc. §6.5: LUÔN trích WAV — số thứ tự/ngôn ngữ không có thì dùng track
        Default (không có thì track đầu), không đưa thẳng file phim cho alass."""
        key = os.path.normcase(os.path.abspath(video))
        if key in self._audio_cache:
            return self._audio_cache[key]
        streams = probe.audio_streams(probe.ffprobe_streams(video, self.ffprobe, "a", self.job, self.ffprobe_cache))
        idx, desc, miss = pick_audio_stream(streams, choice)
        if not streams:
            self.job.warn(f"  [!] Không đọc được audio: {video.name} → thử track đầu.")
        elif miss and choice.mode == "index":
            self.job.warn(f"  [!] {video.name} chỉ có {len(streams)} audio → dùng audio Default.")
        elif miss:
            self.job.warn(f"  [!] {video.name}: không có audio '{choice.value or ''}' → dùng audio Default.")
        res: tuple[int | None, str] = (idx, desc)
        self._audio_cache[key] = res
        return res

    def plan_sync(self, mode: str = "sub", audio: AudioChoice | None = None, lang: str | None = "auto",
                  idx: RefIndex | None = None) -> list[SyncPair]:
        """Bảng cặp đồng bộ (``Invoke-Sync``, phần lập cặp). Không ghi gì vào thư mục phim."""
        c = self.cfg
        lang = self._norm_lang(lang)
        if not self.alass_cmd:
            self.job.error("[LỖI] Không tìm thấy alass. Chọn đường dẫn alass trong Cài đặt.")
            return []
        idx = idx or self.build_index(mode)
        files = self.list_sub_files()
        if not files:
            self.job.warn(f"[!] Không có phụ đề nào trong '{c['SubFolderName']}'.")
            return []
        if idx is None:
            return []
        audio = audio or AudioChoice.from_config(c)
        use_wav = mode == "audio" and bool(self.ffmpeg and self.ffprobe)
        if mode == "audio" and not use_wav:
            self.job.warn("[!] Thiếu ffmpeg/ffprobe → phải đưa thẳng file phim cho alass. MKV có font đính kèm "
                          "(thường gặp ở anime) sẽ lỗi 'missing field codec_long_name'.")
        flags = self.alass_flags(c["AlassArgsSubMode"] if mode == "sub" else c["AlassArgsAudioMode"])
        extra = [str(x) for x in c["AlassExtraArgs"]]
        out_override = self.td / c["OutFolderName"] if c["OutFolderName"] else None
        pairs: list[SyncPair] = []
        seen_out: dict[str, str] = {}
        for i, f in enumerate(files, 1):
            self.job.progress(i, len(files), f.name)
            tag = naming.split_lang_tag(f.stem)[1]
            if not tag:  # TD-32: ten chua co hau to (chua doi ten) -> lay theo lua chon / thu muc con
                tag = lang if lang != "auto" else naming.lang_tag_of_file(f, self.sub_dir)
            p = SyncPair(f, tag, mode)
            pairs.append(p)
            m = self._resolve(idx, f, len(files))
            p.ref, p.how = m.ref, m.how
            if m.ref is None:
                p.reason, p.details = m.reason, m.details
                continue
            ref = m.ref
            out_name = ref.canon + (f".{tag}" if tag else "") + f.suffix.lower()
            vid = ref.file if ref.is_video else idx.video_by_name.get(ref.canon.lower())
            p.video = vid
            out_dir = out_override or (vid.parent if vid else ref.file.parent)
            p.out_path = out_dir / out_name
            k = os.path.normcase(os.path.abspath(p.out_path))
            if _same_path(p.out_path, f):
                p.reason = "nguồn trùng đích"
            elif c["SkipExistingOut"] and p.out_path.exists():
                p.reason = "đã có kết quả"
            elif k in seen_out:
                p.reason = f"trùng đích với '{seen_out[k]}'"
            if p.reason:
                continue
            seen_out[k] = f.name
            # Phan tich quan he thoi gian (du bao + phat hien lech FPS)
            if c["AnalyzeTiming"]:
                if mode == "sub":
                    p.relation = timing.timing_relation(ref.times(), srt.cue_times(f), self.n_ref, self.n_tgt)
                    p.relation_src = "phụ đề gốc"
                else:
                    goc = self._goc_ref(ref.canon)  # TD-18
                    if goc is not None:
                        p.relation = timing.timing_relation(goc[1], srt.cue_times(f), self.n_ref, self.n_tgt)
                        p.relation_src = f"{c['ExtractFolder']}\\{goc[0].name}"
            p.flags = list(flags)
            p.extra = extra
            if p.relation and p.relation.fps_mismatch and c["FpsAutoAllowGuess"]:
                if "--disable-fps-guessing" in p.flags:
                    p.flags.remove("--disable-fps-guessing")
                p.notes.append("Lệch FPS → cho phép alass tự sửa framerate cho file này.")
            if mode == "audio":
                if use_wav:
                    p.audio_index, p.audio_desc = self._pick_audio(ref.file, audio)
                p.attempts = []
                if p.audio_index is not None:
                    p.attempts.append(Attempt(True, True))
                    if p.flags:
                        p.attempts.append(Attempt(True, False, "thử lại WAV không kèm tham số"))
                p.attempts.append(Attempt(False, True, "thử lại với file phim" if p.attempts else ""))
                if p.flags:
                    p.attempts.append(Attempt(False, False, "thử lại file phim không kèm tham số"))
            else:
                p.attempts = [Attempt(False, True)]
                if p.flags:
                    p.attempts.append(Attempt(False, False, "thử lại không kèm tham số"))
        ok = [p for p in pairs if p.ok]
        for p in pairs:
            if not p.ok:
                lvl = "dim" if p.reason in ("đã có kết quả",) else "warn"
                self.job.log(f"  [-] {p.sub.name}: {p.reason}", lvl)
                continue
            assert p.ref is not None and p.out_path is not None
            self.job.info(f"  {p.sub.name}")
            self.job.dim(f"     mốc ({p.ref_kind_text}): {p.ref.file.name}" + (f" — {p.audio_desc}" if p.audio_desc else ""))
            self.job.ok(f"     kết quả    : {p.out_path}")
            for ln in p.forecast():
                self.job.log(f"     {ln}", "error" if ln.startswith("LỆCH") else "dim")
        self.job.head(f"CÁC CẶP SẼ ĐỒNG BỘ: {len(ok)} (bỏ qua {len(pairs) - len(ok)})")
        return pairs

    def preview_commands(self, p: SyncPair) -> list[str]:
        """Nút "Xem lệnh": đúng các lệnh sẽ chạy (đường dẫn tạm ghi rõ), không ghi file nào."""
        if not p.ok or p.ref is None or p.out_path is None:
            return []
        tmp = os.path.join("<thư mục tạm>", "")
        wav = tmp + "ref.wav"
        work = tmp + p.sub.name
        out = tmp + p.out_path.name
        lines: list[str] = []
        if p.mode == "audio" and p.audio_index is not None and p.video:
            lines.append(" ".join(_quote(a) for a in [self.ffmpeg or "ffmpeg", "-y", "-v", "error", "-i", os.fspath(p.video),
                                                      "-map", f"0:a:{p.audio_index}", "-vn", "-ac", "1", "-ar", "16000",
                                                      "-c:a", "pcm_s16le", wav]))
        for at in p.attempts:
            ref = wav if at.use_wav else os.fspath(p.video if p.mode == "audio" and p.video else p.ref.path_for_timing)
            cmd = [*(self.alass_cmd or ["alass"]), *p.alass_args(at, ref, work, out)]
            lines.append(("# " + at.note + "\n" if at.note else "") + " ".join(_quote(a) for a in cmd))
        lines.append(f"# rồi chép {out} → {p.out_path}")
        return lines

    def _write_pending_ref(self, ref: RefItem) -> Path:
        """Phụ đề gốc đọc từ ``.sup`` bạn tự bỏ vào → ghi ``.srt`` cạnh nó (lúc thực thi, có nhật ký)."""
        if ref.pending is None:
            return ref.file
        tmp, dst = ref.pending
        if not dst.exists() and tmp.is_file():
            tools.copy_file(tmp, dst)
            self.log.add(undo.CREATE, ref.file, dst, "mốc thời gian đọc từ PGS")
            self.job.dim(f"  [pgs] Đã ghi {dst.name}")
        return dst

    def _wav(self, p: SyncPair, cache: dict[str, Path | None]) -> Path | None:
        assert p.video is not None and p.audio_index is not None
        key = f"{os.path.normcase(os.path.abspath(p.video))}|{p.audio_index}"
        if key in cache:
            return cache[key]
        d = self.temp / "wav"
        d.mkdir(parents=True, exist_ok=True)
        wav = d / f"ref_{uuid.uuid4().hex[:8]}.wav"
        r = tools.run([self.ffmpeg or "ffmpeg", "-y", "-v", "error", "-i", os.fspath(p.video), "-map", f"0:a:{p.audio_index}",
                       "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", os.fspath(wav)], self.job)
        if r.code != 0 or not wav.is_file() or wav.stat().st_size == 0:
            self.job.warn(f"  [!] ffmpeg không trích được audio {p.audio_index} → thử đưa thẳng file phim cho alass.")
            cache[key] = None
        else:
            cache[key] = wav
        return cache[key]

    def execute_sync(self, pairs: Sequence[SyncPair]) -> SyncReport:
        """Chạy đồng bộ theo kế hoạch. Kết quả chỉ được chép cạnh file phim khi đã xử lý xong
        (dừng giữa chừng không để lại file dở dang)."""
        todo = [p for p in pairs if p.ok and p.selected]
        rep = SyncReport(skipped=len(pairs) - len(todo))
        wav_cache: dict[str, Path | None] = {}
        start_rows = len(self.health_rows)
        try:
            for n, p in enumerate(todo, 1):
                if self.job.should_stop():
                    rep.stopped = True
                    break
                self.job.progress(n, len(todo), p.sub.name)
                res = self._sync_one(p, n, len(todo), wav_cache)
                p.result = res
                if res.ok:
                    rep.ok += 1
                    lv = res.level or 0
                    rep.good += lv == 0
                    rep.check += lv == 1
                    rep.bad += lv == 2
                elif res.rejected:
                    rep.rejected += 1
                else:
                    rep.errors += 1
        except tools.Cancelled:
            rep.stopped = True
            self.job.warn("[!] Đã dừng ngay — file đang làm dở không được ghi.")
        finally:
            shutil.rmtree(self.temp / "wav", ignore_errors=True)
            rep.log_path = self.save_log()
        self.job.head(f"ĐỒNG BỘ: {rep.ok} thành công | {rep.skipped + rep.rejected} bỏ qua | {rep.errors} lỗi")
        rows = self.health_rows[start_rows:]
        if rows:
            self.job.head(f"KIỂM TRA: {rep.good} tốt | {rep.check} cần xem | {rep.bad} có vấn đề")
            bad = sorted((r for r in rows if r["Level"] >= 1), key=lambda r: -r["Level"])
            if bad:
                self.job.warn("CÁC FILE CẦN KIỂM TRA TAY:")
                for b in bad:
                    lv = LEVEL_LOG[b["Level"]]
                    d = f"      {b['Cues']} dòng"
                    if b["DichDau"] != "":
                        d += f" | alass dịch {b['DichDau']}s → {b['DichCuoi']}s"
                    if b["ConLech"] != "":
                        d += f" | còn lệch {b['ConLech']}s ({b['MocKhop']} mốc)"
                    if b["CatDau"]:
                        d += f" | đã cắt {b['CatDau']} dòng đầu"
                    self.job.log(f"   {b['File']}", lv)
                    self.job.log(d, lv)
                    if b["Flags"]:
                        self.job.log(f"      {b['Flags']}", lv)
            if self.cfg["HealthReport"]:
                rep.health_path = self._save_health(rows)
                self.job.info(f"Báo cáo chi tiết: {self.cfg['SubPrepLogDir']}\\{rep.health_path.name}")
        if rep.log_path:
            self.job.info(f"Nhật ký lần chạy (để hoàn tác): {self.cfg['SubPrepLogDir']}\\{rep.log_path.name}")
        return rep

    def _sync_one(self, p: SyncPair, n: int, total: int, wav_cache: dict[str, Path | None]) -> SyncResult:
        c = self.cfg
        assert p.ref is not None and p.out_path is not None
        res = SyncResult()
        ref_file = self._write_pending_ref(p.ref)
        work_dir = self.temp / f"p{n}_{uuid.uuid4().hex[:6]}"
        (work_dir / "out").mkdir(parents=True, exist_ok=True)
        work = work_dir / p.sub.name  # TD-12: BAN SAO - file nguon trong srt_can_sync khong bi sua
        if c["NormalizeEncoding"]:
            how = encoding.to_utf8(p.sub, work, p.lang)
            if how == "error":
                self.job.warn(f"  [!] Không đọc được để chuyển mã: {p.sub.name}")
                shutil.copyfile(p.sub, work)
            elif how not in ("utf8", "empty"):
                res.notes.append(f"{how} → UTF-8")
                self.job.dim(f"  [enc] {p.sub.name}: {how} → UTF-8 (trên bản sao)")
        else:
            shutil.copyfile(p.sub, work)

        self.job.info(f"  [{n}/{total}] Đồng bộ ({p.ref_kind_text}): {p.sub.name}")
        is_srt = srt.is_srt_path(work)
        blocks0 = srt.read_blocks(work) if is_srt else None
        cues_before = len(blocks0) if blocks0 else len(srt.cue_times(work) or [])
        ref_times = p.ref.times() if p.mode == "sub" else None
        if c["TrimLeadingExtra"] and ref_times:
            if is_srt:
                keep, cut = remove_leading_extra(blocks0, ref_times, int(c["MinVerifyVotes"]), self.n_ref, self.n_tgt)
                if keep is not None and srt.write_blocks(work, keep):
                    res.cut = cut
                    cues_before -= cut
                    self.job.warn(f"      [cut] Đã cắt {cut} dòng đầu không có trong phim (recap/giới thiệu)")
            else:
                res.notes.append(f"không cắt recap ({work.suffix.lstrip('.').upper()})")  # Q7: khong doi dinh dang
        vid_dur = probe.duration(probe.mkv_json(p.video, self.mkvmerge, self.job, self.json_cache)) if p.video else 0.0
        src_times = srt.cue_times(work)
        for ln in p.notes:
            self.job.warn(f"      [i] {ln}")

        out_tmp = work_dir / "out" / p.out_path.name
        assert self.alass_cmd is not None
        code, output = 1, ""
        tried = 0
        for i, at in enumerate(p.attempts):
            ref_path: str | os.PathLike[str]
            if at.use_wav:
                w = self._wav(p, wav_cache)
                if w is None:
                    continue
                ref_path = w
            elif p.mode == "audio":
                ref_path = p.video or p.ref.file
            else:
                ref_path = p.ref.path_for_timing
            if tried and at.note:
                self.job.warn(f"      [!] {at.note}...")
            tried += 1
            out_tmp.unlink(missing_ok=True)
            r = tools.run([*self.alass_cmd, *p.alass_args(at, ref_path, work, out_tmp)], self.job)
            code, output = r.code, r.output
            if code == 0:
                res.attempt = i
                break
        res.code = code
        good = code == 0 and out_tmp.is_file() and out_tmp.stat().st_size >= 32
        if not good:
            self.job.error(f"      [!] THẤT BẠI (mã {code})")
            res.output_tail = output.splitlines()[-4:]
            for ln in res.output_tail:
                self.job.dim(f"         {ln}")
            p.status = f"thất bại (mã {code})"
            return res

        new_times = srt.cue_times(out_tmp)
        res.shift = srt.max_shift(src_times, new_times)
        max_shift = float(c["MaxShiftSeconds"])
        if res.shift is not None and max_shift > 0 and res.shift > max_shift:
            if p.mode == "sub":
                # Moc la phu de -> dang tin cay: do lech lon van co the dung (ban dung khac nhau, intro dai ngan)
                self.job.warn(f"      [!] Lưu ý: dịch tới {_num(res.shift, 1)}s - lớn hơn bình thường. "
                              "Vẫn giữ kết quả vì mốc là phụ đề gốc. Kiểm tra lại khi xem.")
                res.notes.append(f"dịch {_num(res.shift, 1)}s (lớn)")
            else:
                self.job.error(f"      [!] TỪ CHỐI: alass dịch {_num(res.shift, 1)}s (ngưỡng {_num(max_shift, 1)} s). "
                               "Mốc là audio nên độ tin cậy thấp. Giữ nguyên file gốc.")
                res.rejected = True
                p.status = f"từ chối (dịch {_num(res.shift, 1)}s)"
                return res
        # TD-20: LUON sua khoi 00:00:00 + kiem tra suc khoe (ke ca khi tat nguong dich toi da)
        max_zero = int(c["MaxZeroStartCues"])
        if srt.is_srt_path(out_tmp):
            keep, fixed = repair_collapsed_cues(srt.read_blocks(out_tmp), max_zero)
            if keep is not None and srt.write_blocks(out_tmp, keep):
                res.fixed = fixed
                self.job.warn(f"      [fix] Đã bỏ {fixed} dòng bị dồn về 00:00:00")
        elif sum(1 for t in (new_times or []) if t < 0.05) > max_zero:
            res.notes.append(f"không sửa khối 00:00:00 ({out_tmp.suffix.lstrip('.').upper()})")
        if c["NormalizeEncoding"]:
            encoding.to_utf8(out_tmp, out_tmp, p.lang)
        h = sub_health(out_tmp, ref_times, vid_dur, cues_before, src_times, int(c["MinVerifyVotes"]), max_zero,
                       self.n_ref, self.n_tgt)
        res.health = h

        # Ghi ket qua canh file phim
        self._ensure_dir(p.out_path.parent)
        if p.out_path.exists():
            if c["SkipExistingOut"]:
                self.job.dim(f"      [=] Đã có kết quả: {p.out_path.name}")
                p.status = "đã có kết quả"
                res.rejected = True
                return res
            if not self._replace_existing(p.out_path, "thay bằng kết quả đồng bộ mới"):
                p.status = "không ghi được"
                return res
        tools.copy_file(out_tmp, p.out_path)
        res.ok, res.out = True, p.out_path
        p.status = h.text
        lv = LEVEL_LOG[h.level]
        self.job.log(f"      → {h.text} : {p.out_path.name} ({h.cues} dòng)", lv)
        if h.shift_first is not None and h.shift_last is not None:
            sp = h.shift_last - h.shift_first
            self.job.dim(f"         alass dịch: đầu phim {h.shift_first:+.2f}s | cuối phim {h.shift_last:+.2f}s")
            sp_txt = ("(dịch đều, không co giãn)" if abs(sp) <= 0.5 else "(co giãn nhẹ)" if abs(sp) <= 5 else "(CO GIÃN MẠNH)")
            self.job.log(f"         chênh đầu-cuối: {sp:+.2f}s {sp_txt}", "dim" if abs(sp) <= 0.5 else "warn" if abs(sp) <= 5 else "error")
        if h.res_votes >= 0 and h.res_offset is not None:
            q = abs(h.res_offset)
            q_txt = "KHỚP" if q <= 0.3 else "gần khớp" if q <= 1.0 else "CÒN LỆCH"
            ex = f" | LỆCH FPS: {h.res_fps}" if h.res_fps else ""
            self.job.log(f"         so với sub gốc: {q_txt} {_signed(h.res_offset)}s ({h.res_votes} mốc){ex}",
                         "ok" if q <= 1.0 else "warn")
        for fl in h.flags:
            self.job.log(f"         - {fl}", lv)
        for nt in res.notes:
            self.job.dim(f"         ({nt})")

        detail = []
        if h.shift_first is not None and h.shift_last is not None:
            detail.append(f"đầu={_num(h.shift_first)}s cuối={_num(h.shift_last)}s")
        if h.res_votes >= 0 and h.res_offset is not None:
            detail.append(f"cònlệch={_num(h.res_offset)}s mốc={h.res_votes}")
        if res.cut:
            detail.append(f"cắt={res.cut} dòng")
        if res.fixed:
            detail.append(f"bỏ00={res.fixed} dòng")
        txt = " ".join(detail)
        if h.flags:
            txt += " | " + "; ".join(h.flags)
        if res.notes:
            txt += " | " + "; ".join(res.notes)
        self.log.add(undo.CREATE, p.sub, p.out_path, txt.strip(" |"))
        self.health_rows.append({
            "File": p.out_path.name, "Level": h.level, "Cues": h.cues,
            "DichDau": _num(h.shift_first) if h.shift_first is not None else "",
            "DichCuoi": _num(h.shift_last) if h.shift_last is not None else "",
            "ConLech": _num(h.res_offset) if h.res_votes >= 0 and h.res_offset is not None else "",
            "MocKhop": h.res_votes if h.res_votes >= 0 else "",
            "CatDau": res.cut, "Flags": " | ".join(h.flags),
        })
        self.processed.append(Processed(p.sub, ref_file, p.out_path))
        return res

    def _save_health(self, rows: list[dict[str, Any]]) -> Path:
        d = self.td / self.cfg["SubPrepLogDir"]
        d.mkdir(parents=True, exist_ok=True)
        stamp = _dt.datetime.now().strftime("%Y%m%d_%H%M%S")
        path = d / f"health_{stamp}.csv"
        k = 2
        while path.exists():
            path = d / f"health_{stamp}_{k}.csv"
            k += 1
        with open(path, "w", encoding="utf-8-sig", newline="") as fh:
            w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()), quoting=csv.QUOTE_ALL, lineterminator="\r\n")
            w.writeheader()
            w.writerows(rows)
        return path

    # ------------------------------------------------------------------ 3. don dep
    def cleanup_targets(self) -> CleanupPlan | None:
        return cleanup_targets(self.processed, self.ex_dir, bool(self.cfg["DeleteToRecycleBin"]))

    def execute_cleanup(self, plan: CleanupPlan, which: str) -> CleanupReport:
        rep = CleanupReport()
        targets = plan.targets(which)
        for t in targets:
            if undo.trash(t, plan.to_recycle_bin):
                self.log.add(undo.DELETE, t, "", plan.where)
                rep.deleted += 1
            else:
                rep.failed.append(t)
                self.job.error(f"  [!] Không xoá được: {t.name}")
        if targets:
            self.job.ok(f"Đã xoá {rep.deleted} file ({plan.where}).")
        for d in (self.sub_dir, self.ex_dir):
            if d is not None and d.is_dir():
                left = len(tools.walk_files(d, True))
                rep.left[d.name] = left
                self.job.dim(f"   ('{d.name}' giờ đã trống)" if left == 0 else f"   ('{d.name}' còn {left} file chưa xử lý)")
        self.save_log()
        return rep

    # ------------------------------------------------------------------ dong
    def close(self) -> None:
        shutil.rmtree(self.temp, ignore_errors=True)

    def __enter__(self) -> "PrepSession":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()
