"""Nhận diện tập phim bộ — bản gộp dùng chung ba bước (docs/SPEC.md mục 3.4).

Thay ``Get-EpisodeInfo`` (SubFetch, SubPrep), ``Split-EpisodeName`` + ``Format-EpRef`` (MuxTool),
``Get-EpLabel`` (SubPrep), ``Test-EpisodeConflict`` (SubFetch), quy tắc ghép tập của
``Find-DonorFile`` (MuxTool).

Khung lấy từ ``Split-EpisodeName`` (trả tên series + năm, tập đôi, chặn năm), cộng các mẫu
bổ sung của SubPrep (bỏ mã CRC, ``Season N Episode M``, ``Ep/E/Tap N``) và cờ "chắc chắn"
của SubPrep (phép đoán cuối cùng chỉ để báo lỗi, không bao giờ dùng để ghép).
"""
from __future__ import annotations

import re
from dataclasses import dataclass

from . import naming

_CRC = re.compile(r"\[[0-9A-Fa-f]{8}\]")
_LEAD_GROUP = re.compile(r"^\[[^\]]+\]\s*")

# 1. S01E05, S21E1085, S01E05-E06, S01E05E06
_P_SXE = re.compile(r"(?i)\bS(\d{1,2})\s?E(\d{1,4})(?:\s?-\s?|\s?)?(?:E(\d{1,4}))?\b")
# 2. 1x05
_P_NX = re.compile(r"(?<!\d)(\d{1,2})x(\d{2})(?!\d)")
# 3. Season 2 Episode 5 / Season 2 Ep 5 / Season 2 Tap 5 / Season 2 E05
_P_SEASON_WORD = re.compile(r"(?i)\bSeason\s?(\d{1,2})\b.*?\b(?:Episode|Ep|E|Tap)\s?(\d{1,4})\b")
# 4. So tuyet doi sau " - ": "One Piece - 0643", "- 05v2", "- 1085[1080p]"
_P_ABS_DASH = re.compile(r"(?i)\s-\s(?:E|EP|Episode\s?)?(\d{1,4})(?:v\d)?(?=\s|$|\[|\()")
# 5. "Naruto EP 055", "Ep 5", "E05", "Tap 12"
_P_EP_WORD = re.compile(r"(?i)\b(?:Episode|Ep|E|Tap)\s?(\d{1,4})(?:v\d)?\b")


@dataclass(frozen=True)
class EpisodeInfo:
    series: str
    year: int | None
    season: int  # 0 = ten khong ghi season (so tuyet doi)
    episode: int
    episode_end: int | None
    has_season: bool
    confident: bool

    @property
    def absolute(self) -> bool:
        """Đánh số tuyệt đối, không có season (anime)."""
        return not self.has_season

    @property
    def label(self) -> str:
        """Nhãn kiểu SubPrep (``Get-EpLabel``): ``S01E05`` / ``E643``."""
        if self.has_season:
            return f"S{self.season:02d}E{self.episode:02d}"
        return f"E{self.episode:02d}"

    @property
    def ref_label(self) -> str:
        """Nhãn kiểu MuxTool (``Format-EpRef``): ``S01E05`` / ``tap #643`` — khoá của ``DonorOffsetMs``."""
        if self.absolute:
            return f"tap #{self.episode}"
        return f"S{self.season:02d}E{self.episode:02d}"


def _prep(name: str) -> str:
    t = _CRC.sub(" ", name)
    t = _LEAD_GROUP.sub("", t)
    t = re.sub(r"[\._]+", " ", t)
    return re.sub(r"\s+", " ", t)


def _is_year(digits: str) -> bool:
    """"Phim - 1917" là NĂM, không phải số tập: 4 chữ số trong 1900–2099."""
    return len(digits) == 4 and 1900 <= int(digits) <= 2099


def _series_year(head: str) -> tuple[str, int | None]:
    title, year = naming.pick_year(head)
    if title is None:
        title = head
    title = re.sub(r"[\(\)\[\]\{\}]", " ", title)
    title = re.sub(r"\s+", " ", title)
    return title.strip(" -_"), year


def _guess_number(t: str) -> int | None:
    """Phép đoán cuối của SubPrep: bỏ năm, độ phân giải, codec… rồi lấy số còn lại cuối cùng."""
    c = t
    c = re.sub(r"(?i)\b(19|20)\d{2}\b", " ", c)
    c = re.sub(r"(?i)\b(4320|2160|1440|1080|720|576|480|360)p?\b", " ", c)
    c = re.sub(r"(?i)\b(x|h)\s?26[45]\b", " ", c)
    c = re.sub(r"(?i)\b(10|8)\s?bit\b", " ", c)
    c = re.sub(r"\b\d\s?\.\s?\d\b", " ", c)
    c = re.sub(r"(?i)\b(aac|ac3|eac3|dts|flac|opus|ddp?\d?|truehd|atmos|mp3)\d*\b", " ", c)
    c = re.sub(r"(?i)\b(bluray|blu ray|bdrip|brrip|webrip|web dl|web|hdtv|dvdrip|remux|hevc|avc|hdr\d*|dovi|sdr|imax)\b", " ", c)
    c = re.sub(r"(?i)\bv\d\b", " ", c)
    nums = re.findall(r"(?<!\d)(\d{1,4})(?!\d)", c)
    return int(nums[-1]) if nums else None


def parse_episode(name: str, allow_guess: bool = False) -> EpisodeInfo | None:
    """Nhận diện tập từ tên file (không kèm đuôi).

    Trả ``None`` nếu là phim lẻ. ``allow_guess=True`` trả thêm phép đoán "không chắc"
    (``confident=False``) — CHỈ để hiển thị/báo lỗi, không bao giờ dùng để ghép hay chặn.
    """
    t = _prep(name)

    m = _P_SXE.search(t)
    if m:
        series, year = _series_year(t[: m.start()])
        return EpisodeInfo(series, year, int(m.group(1)), int(m.group(2)),
                           int(m.group(3)) if m.group(3) else None, True, True)

    m = _P_NX.search(t)
    if m:
        series, year = _series_year(t[: m.start()])
        return EpisodeInfo(series, year, int(m.group(1)), int(m.group(2)), None, True, True)

    m = _P_SEASON_WORD.search(t)
    if m:
        series, year = _series_year(t[: m.start()])
        return EpisodeInfo(series, year, int(m.group(1)), int(m.group(2)), None, True, True)

    for pat in (_P_ABS_DASH, _P_EP_WORD):
        for m in pat.finditer(t):
            digits = m.group(1)
            n = int(digits)
            if _is_year(digits) or n <= 0:
                continue
            series, year = _series_year(t[: m.start()])
            return EpisodeInfo(series, year, 0, n, None, False, True)

    if allow_guess:
        n = _guess_number(t)
        if n is not None:
            return EpisodeInfo("", None, 0, n, None, False, False)
    return None


def is_episode(name: str) -> bool:
    return parse_episode(name) is not None


def episode_conflict(name_a: str, name_b: str) -> bool:
    """``Test-EpisodeConflict``: hai tên đều ghi số tập (chắc chắn) mà khác nhau → chắc chắn khác tập.

    Cả hai có season → khác season hoặc khác tập là xung đột; một bên không có season →
    khác số tập là xung đột. ``S16E643`` / ``- 0643`` → không xung đột.
    """
    a = parse_episode(name_a)
    b = parse_episode(name_b)
    if a is None or b is None:
        return False
    if a.has_season and b.has_season:
        return a.season != b.season or a.episode != b.episode
    return a.episode != b.episode


def same_episode(a: EpisodeInfo | None, b: EpisodeInfo | None) -> bool:
    """Hai tên chỉ cùng một tập (quy tắc của ``Find-DonorFile``).

    Cả hai có season → trùng season + tập; cả hai tuyệt đối → trùng tập; một bên mỗi kiểu
    (``S16E643`` / ``- 643``) → trùng số tập VÀ (season = 1 hoặc số tập ≥ 100), để không nhầm
    với tập lẻ của season khác.
    """
    if a is None or b is None or not a.confident or not b.confident:
        return False
    if a.episode != b.episode:
        return False
    if a.absolute == b.absolute:
        return a.absolute or a.season == b.season
    non_abs = b if a.absolute else a
    return non_abs.season == 1 or a.episode >= 100


def same_episode_names(name_a: str, name_b: str) -> bool:
    return same_episode(parse_episode(name_a), parse_episode(name_b))
