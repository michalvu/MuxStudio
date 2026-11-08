"""Kho phụ đề tự tải thủ công ``srt_pool`` (§6.11 nguồn 2).

Thay ``Initialize-Pool``, ``Get-PoolLang``, ``ConvertTo-PoolLangCode``, ``Get-PoolCandidates`` (SubFetch).

File đã nằm sẵn trên đĩa nên KHÔNG tốn quota → ĐO THẲNG từng ứng viên với phụ đề gốc:
dưới ``PoolMinVotes`` (8) mốc → bỏ (in tên ra); từ đó tới ``MinVerifyVotes`` (20) → "nghi ngờ,
cần kiểm tra"; ≥ 20 → khớp. Lọc theo SỐ TẬP trước rồi mới cắt bớt danh sách (§6.6).
"""
from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping, Sequence

from . import episodes, naming, srt, timing, tools

POOL_EXTS = (".srt", ".ass", ".ssa")


@dataclass
class PoolIndex:
    root: Path
    by_lang: dict[str, list[Path]] = field(default_factory=dict)
    total: int = 0
    unknown: int = 0

    def summary(self) -> str:
        parts = [f"{k}({len(v)})" for k, v in sorted(self.by_lang.items())]
        return ", ".join(parts)


def build_index(pool_root: str | os.PathLike[str], exclude_forced: bool = True, exclude_sdh: bool = True,
                default_lang: str = "", job: tools.Job | None = None) -> PoolIndex:
    """``Initialize-Pool``: quét kho MỘT lần, phân loại theo ngôn ngữ (thư mục con cấp 1 hoặc hậu tố tên file)."""
    job = job or tools.NULL_JOB
    root = Path(pool_root)
    idx = PoolIndex(root)
    if not root.is_dir():
        return idx
    files = tools.walk_files(root, True, POOL_EXTS)
    idx.total = len(files)
    for f in files:
        if exclude_forced and re.search(r"(?i)\bforced\b", f.stem):
            continue
        if exclude_sdh and re.search(r"(?i)\b(sdh|hearing|hi)\b", f.stem):
            continue
        lg = naming.lang_tag_of_file(f, root)
        if not lg:
            if default_lang:
                lg = default_lang
            else:
                idx.unknown += 1
                continue
        idx.by_lang.setdefault(lg, []).append(f)
    if files:
        job.info(f"[i] Kho phụ đề '{root.name}': {len(files)} file - {idx.summary()}")
        if idx.unknown:
            job.dim(f"    ({idx.unknown} file không rõ ngôn ngữ, đã bỏ qua - đặt vào thư mục con vi\\ cs\\ en\\ hoặc thêm hậu tố)")
    return idx


def pool_candidates(idx: PoolIndex | None, lang: str, video_name: str, video_tokens: list[str],
                    ref_times: Sequence[float] | None, cfg: Mapping[str, Any], job: tools.Job | None = None) -> list:
    """``Get-PoolCandidates``: chỉ gom từ KHO NỘI BỘ — miễn phí, không chạm mạng, đo được độ lệch thật."""
    from .subfetch import Candidate, Offset  # tranh vong import

    job = job or tools.NULL_JOB
    out: list[Candidate] = []
    if idx is None or lang not in idx.by_lang:
        job.dim(f"    [kho] {lang} : không có file nào trong kho.")
        return out
    all_files = idx.by_lang[lang]
    pool = [f for f in all_files if not episodes.episode_conflict(video_name, f.stem)]
    if not pool:
        job.dim(f"    [kho] {lang} : {len(all_files)} file, nhưng đều thuộc tập khác.")
        return out
    my_ep = episodes.parse_episode(video_name)
    if my_ep is not None:
        exact = [f for f in pool if episodes.same_episode(my_ep, episodes.parse_episode(f.stem))]
        if exact:
            pool = exact
    ranked = sorted(((f, naming.title_score(video_tokens, naming.title_tokens(f.stem))) for f in pool),
                    key=lambda x: -x[1])[: int(cfg["PoolTryLimit"])]
    job.dim(f"    [kho] {lang} : {len(all_files)} file, {len(pool)} file hợp tập, đo thử {len(ranked)} file")

    min_votes = int(cfg["MinVerifyVotes"])
    pool_min = int(cfg["PoolMinVotes"])
    dropped = 0
    for f, s in ranked:
        tier, fps_txt, off, note = 2, "?", None, "file nội bộ"
        if ref_times:
            t = srt.cue_times(f)
            if not t or len(t) < 30:
                note = "file quá ngắn, không đo được"
            else:
                rel = timing.timing_relation(ref_times, t, int(cfg["FpsSampleRef"]), int(cfg["FpsSampleTgt"]))
                if rel:
                    if rel.votes < pool_min:
                        job.dim(f"       bỏ: {f.name} (chỉ {rel.votes} mốc khớp)")
                        dropped += 1
                        continue
                    off = Offset(rel.offset, rel.votes, -1)
                    if rel.votes < min_votes:
                        tier, fps_txt, note = 2, "đo: nghi ngờ", f"chỉ {rel.votes} mốc khớp - cần kiểm tra"
                    elif rel.fps_mismatch:
                        tier, fps_txt, note = 3, "lệch", f"{rel.votes} mốc khớp, LỆCH FPS"
                    else:
                        tier, fps_txt, note = 1, "đo: khớp", f"{rel.votes} mốc khớp"
        out.append(Candidate("Kho", tier, fps_txt, s, f.name, note, f, off))
    if not out and dropped:
        job.dim(f"    [kho] {lang} : không file nào khớp với phim này.")
    return out
