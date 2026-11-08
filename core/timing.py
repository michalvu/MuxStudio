"""So khớp thời gian giữa hai bộ mốc phụ đề — lõi của mọi phép đo ở cả ba bước.

Port CHÍNH XÁC từ các hàm PowerShell (giống hệt nhau ở SubFetch/SubPrep/MuxTool):
``ConvertTo-Double``, ``Format-Inv``, ``Get-EvenSample``, ``Get-OffsetVote``,
``Get-TimingRelation``, ``Get-QuickOffset`` và bảng ``$script:FpsRatios``.

Những điểm phải giữ để ra cùng kết quả với .bat:

* ``[int]x`` và ``[math]::Round(x)`` trong PowerShell làm tròn về số CHẴN
  (2.5 → 2, 7.5 → 8). Python ``round()`` cũng vậy → dùng ``round()``,
  không dùng ``int()``.
* Khi hai độ lệch bằng phiếu, .bat lấy khoá được thêm vào TRƯỚC (Dictionary .NET
  giữ thứ tự chèn). ``dict`` của Python cũng giữ thứ tự chèn → duyệt cùng thứ tự.
* Thứ tự cộng dồn số thực giống .bat (vòng ngoài = mốc cần đo, vòng trong = mốc gốc
  tăng dần) nên tổng ra từng bit như nhau.

Khác .bat duy nhất: ``offset_vote`` chỉ duyệt các mốc gốc nằm trong cửa sổ quanh
mỗi mốc cần đo (tìm nhị phân) thay vì duyệt hết. Các cặp bị bỏ qua đều là cặp mà
.bat cũng ``continue``, nên kết quả giống hệt nhưng nhanh hơn nhiều.
"""
from __future__ import annotations

import math
from bisect import bisect_left, bisect_right
from dataclasses import dataclass
from typing import Iterable, Sequence

# --------------------------------------------------------------------------
#  Số thập phân không phụ thuộc vùng miền
# --------------------------------------------------------------------------


def to_double(v: object) -> float:
    """``ConvertTo-Double``: đọc số thập phân, không phụ thuộc vùng miền Windows.

    "23.976" luôn ra 23.976 kể cả khi Windows dùng dấu phẩy; "23,976" cũng ra 23.976.
    Không đọc được → 0.0 (giống .bat).
    """
    if v is None:
        return 0.0
    if isinstance(v, bool):  # PowerShell: [string]$true = "True" -> khong doc duoc
        return 0.0
    if isinstance(v, (int, float)):
        f = float(v)
        return f
    s = str(v).strip()
    if not s:
        return 0.0
    for cand in (s, s.replace(",", ".")):
        if "_" in cand:  # Python chap nhan "1_000", .NET thi khong
            continue
        try:
            return float(cand)
        except ValueError:
            continue
    return 0.0


def fmt_inv(x: float, decimals: int = 6) -> str:
    """``Format-Inv``: số thập phân luôn dấu CHẤM, bỏ số 0 thừa (như mẫu ``0.######``).

    Dùng cho mọi số đưa vào dòng lệnh ffmpeg/mkvmerge.
    """
    s = f"{float(x):.{decimals}f}"
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    if s in ("-0", ""):
        s = "0"
    return s


# --------------------------------------------------------------------------
#  Bảng tỉ lệ FPS
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class FpsRatio:
    ratio: float
    name: str


#: Cùng 7 tỉ lệ, cùng thứ tự như ``$script:FpsRatios`` (tên mô tả lấy bản dài của SubPrep/MuxTool).
FPS_RATIOS: tuple[FpsRatio, ...] = (
    FpsRatio(1.0, "không co giãn"),
    FpsRatio(24 / 23.976, "24 → 23.976 (hoặc 30 → 29.97)"),
    FpsRatio(23.976 / 24, "23.976 → 24 (hoặc 29.97 → 30)"),
    FpsRatio(25 / 23.976, "PAL 25 → NTSC 23.976"),
    FpsRatio(23.976 / 25, "NTSC 23.976 → PAL 25"),
    FpsRatio(25 / 24, "25 → 24"),
    FpsRatio(24 / 25, "24 → 25"),
)

#: Ngưỡng coi là lệch FPS: ``[math]::Abs($best.Scale - 1.0) -gt 0.0002``
FPS_MISMATCH_EPS = 0.0002

#: Số mốc tối thiểu mỗi bên để đo: ``Count -lt 30 -> $null``
MIN_CUES_FOR_RELATION = 30


# --------------------------------------------------------------------------
#  Lấy mẫu đều + bỏ phiếu độ lệch
# --------------------------------------------------------------------------


def even_sample(arr: Sequence[float], n: int) -> list[float]:
    """``Get-EvenSample``: lấy ``n`` phần tử rải đều. Chỉ số = ``[int](i*step)`` (làm tròn về số chẵn)."""
    if len(arr) <= n:
        return list(arr)
    step = len(arr) / n
    return [arr[int(round(i * step))] for i in range(n)]


@dataclass(frozen=True)
class OffsetVote:
    offset: float
    votes: int


def _is_sorted(a: Sequence[float]) -> bool:
    return all(a[i] <= a[i + 1] for i in range(len(a) - 1))


def offset_vote(
    ref: Sequence[float],
    tgt: Sequence[float],
    max_off: float = 400.0,
    bin_size: float = 0.15,
) -> OffsetVote | None:
    """``Get-OffsetVote``: bỏ phiếu độ lệch cố định ``ref - tgt`` theo ô ``bin_size`` trong ±``max_off``.

    Trả về độ lệch = trung bình các hiệu nằm trong ±0,35 s quanh ô thắng, và số phiếu của ô thắng.
    """
    ref = list(ref)
    votes: dict[int, int] = {}
    use_window = _is_sorted(ref)
    margin = 1.0  # cua so rong hon mot chut; dieu kien chinh xac van kiem tung cap

    for t in tgt:
        if use_window:
            lo = bisect_left(ref, t - max_off - margin)
            hi = bisect_right(ref, t + max_off + margin)
            rng: Iterable[float] = (ref[j] for j in range(lo, hi))
        else:
            rng = ref
        for r in rng:
            d = r - t
            if d < -max_off or d > max_off:
                continue
            k = round(d / bin_size)
            votes[k] = votes.get(k, 0) + 1

    if not votes:
        return None
    bk, bv = 0, -1
    for k, c in votes.items():  # bang phieu -> giu khoa gap truoc (nhu .NET Dictionary)
        if c > bv:
            bv, bk = c, k
    center = bk * bin_size

    total = 0.0
    cnt = 0
    for t in tgt:
        if use_window:
            lo = bisect_left(ref, t + center - 0.35 - margin)
            hi = bisect_right(ref, t + center + 0.35 + margin)
            rng = (ref[j] for j in range(lo, hi))
        else:
            rng = ref
        for r in rng:
            d = r - t
            if abs(d - center) <= 0.35:
                total += d
                cnt += 1
    off = total / cnt if cnt > 0 else center
    return OffsetVote(off, bv)


# --------------------------------------------------------------------------
#  Quan hệ thời gian: co giãn (tỉ lệ FPS) + độ lệch
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class TimingRelation:
    """Quan hệ ``mốc_gốc ≈ mốc_cần_đo × scale + offset``."""

    scale: float
    name: str
    offset: float
    votes: int
    drift: float  # do troi tich luy o cuoi phim neu bo qua co gian (giay)
    fps_mismatch: bool


def timing_relation(
    ref_times: Sequence[float] | None,
    tgt_times: Sequence[float] | None,
    n_ref: int = 400,
    n_tgt: int = 100,
) -> TimingRelation | None:
    """``Get-TimingRelation``: thử từng tỉ lệ FPS, chọn tỉ lệ nhiều phiếu nhất.

    Cần ≥ 30 mốc mỗi bên. Bằng phiếu → giữ tỉ lệ đứng trước trong ``FPS_RATIOS``.
    ``n_ref``/``n_tgt`` = ``$FpsSampleRef``/``$FpsSampleTgt`` (400/100).
    """
    if not ref_times or not tgt_times:
        return None
    if len(ref_times) < MIN_CUES_FOR_RELATION or len(tgt_times) < MIN_CUES_FOR_RELATION:
        return None
    r_s = even_sample(ref_times, n_ref)
    t_s = even_sample(tgt_times, n_tgt)

    best: tuple[FpsRatio, OffsetVote] | None = None
    for fr in FPS_RATIOS:
        scaled = [x * fr.ratio for x in t_s]
        v = offset_vote(r_s, scaled)
        if v is None:
            continue
        if best is None or v.votes > best[1].votes:
            best = (fr, v)
    if best is None:
        return None
    fr, v = best
    last_t = tgt_times[len(tgt_times) - 1]
    return TimingRelation(
        scale=fr.ratio,
        name=fr.name,
        offset=v.offset,
        votes=v.votes,
        drift=abs((fr.ratio - 1.0) * last_t),
        fps_mismatch=abs(fr.ratio - 1.0) > FPS_MISMATCH_EPS,
    )


# --------------------------------------------------------------------------
#  Ước tính nhanh từ "preview" của SubSource
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class QuickOffset:
    offset: float
    votes: int
    total: int  # so moc preview (> 0 nghia la "uoc tinh", hien dau ~)


def quick_offset(ref_times: Sequence[float] | None, pv_times: Sequence[float]) -> QuickOffset | None:
    """``Get-QuickOffset``: chỉ kết luận khi TẤT CẢ mốc preview cùng đồng ý một độ lệch.

    Ô 0,25 s, phạm vi ±180 s, cần ≥ 3 mốc preview. (Theo .bat: bản đúng nhận 60/60,
    bản sai lọt 0/60.)
    """
    if not ref_times or len(pv_times) < 3:
        return None
    votes: dict[int, int] = {}
    for t in pv_times:
        for r in ref_times:
            d = r - t
            if d < -180 or d > 180:
                continue
            k = round(d / 0.25)
            votes[k] = votes.get(k, 0) + 1
    if not votes:
        return None
    bk, bv = 0, -1
    for k, c in votes.items():
        if c > bv:
            bv, bk = c, k
    if bv < len(pv_times):
        return None
    return QuickOffset(offset=bk * 0.25, votes=bv, total=len(pv_times))


# --------------------------------------------------------------------------
#  Bảng băm theo ô 1 giây: "mốc này có trong phim không" — O(1)
# --------------------------------------------------------------------------


class RefBins:
    """Tập ô ``floor(giây)`` của các mốc gốc (từ ``Remove-LeadingExtra``).

    ``near(x)`` = có mốc gốc trong khoảng ~±1,5 s quanh ``x``: kiểm 4 ô
    ``c, c-1, c+1, c-2`` với ``c = floor(x)`` — đúng như .bat.
    (Bản cũ duyệt tuyến tính, chậm tới mức trông như treo.)
    """

    __slots__ = ("_bins",)

    def __init__(self, ref_times: Iterable[float]):
        self._bins = {math.floor(r) for r in ref_times}

    def near(self, x: float) -> bool:
        c = math.floor(x)
        b = self._bins
        return c in b or (c - 1) in b or (c + 1) in b or (c - 2) in b

    def __len__(self) -> int:
        return len(self._bins)
