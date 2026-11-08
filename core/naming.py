"""Tên: ngôn ngữ, từ khoá tên phim, tách tên + năm, tên file an toàn, tên chuẩn Jellyfin.

Gộp từ ba file .bat (xem docs/SPEC.md mục 3.5, 3.6):

* Ngôn ngữ: ``$LangMap``, ``ConvertTo-PoolLangCode`` (SF); ``ConvertTo-LangCode``,
  ``$KnownLangTags``, ``Split-LangTag``, ``Get-FileLangTag``, ``Test-LangMatch`` (SP);
  ``Get-LangCode``, ``Get-LangName``, ``Get-LangForMux`` (MT) → một bảng ``LANGS``.
* Từ khoá: ``Get-Tokens``/``Get-TitleTokens``, ``Get-Score``/``Get-TitleScore``.
* Tách tên + năm: ``Get-CleanTitle``/``Get-Year`` (SF), ``Get-TitleKey``/``Get-TitleYear`` (SP),
  ``Split-ReleaseName`` (MT) → ``split_release_name`` (quy tắc năm mới, TĐ-05).
* ``Get-SafeFileName``, ``Get-NormTitle`` và phần dựng tên trong ``Get-MovieFileName``,
  ``Get-TmdbTvName``/``Get-TvdbTvName`` (MT).
"""
from __future__ import annotations

import datetime as _dt
import os
import re
from dataclasses import dataclass, field

# ==========================================================================
#  NGÔN NGỮ
# ==========================================================================


@dataclass(frozen=True)
class Lang:
    code: str  # ma 2 chu: 'vi'
    iso2b: str  # ISO 639-2/B: 'vie', 'cze', 'ger' (mkvmerge < v50)
    iso2t: str  # ISO 639-2/T: 'vie', 'ces', 'deu'
    name: str  # ten tieng Anh: 'Vietnamese' (dat ten track)
    subsource: str  # ten SubSource: 'vietnamese' ('' = SubSource khong ho tro)
    aliases: tuple[str, ...] = field(default_factory=tuple)  # cach viet khac
    codepage: int = 1252  # bang ma cu khi file khong phai UTF-8
    charset: str = "windows-1252"  # ten bang ma cho mkvmerge --sub-charset

    def words(self) -> set[str]:
        return {self.code, self.iso2b, self.iso2t, *self.aliases}


# Thu tu = thu tu uu tien khi nhan dien (giu dung thu tu cua Get-LangCode o MuxTool,
# roi toi cac ngon ngu con lai cua $LangMap trong SubFetch).
_LANG_LIST: list[Lang] = [
    Lang("vi", "vie", "vie", "Vietnamese", "vietnamese", ("vn", "viet", "vietnamese", "tiengviet"), 1258, "windows-1258"),
    Lang("cs", "cze", "ces", "Czech", "czech", ("cz", "czech", "sec"), 1250, "windows-1250"),
    Lang("en", "eng", "eng", "English", "english", ("english", "anh"), 1252, "windows-1252"),
    Lang("ja", "jpn", "jpn", "Japanese", "japanese", ("jp", "japanese"), 932, "shift_jis"),
    Lang("ko", "kor", "kor", "Korean", "korean", ("korean",), 949, "euc-kr"),
    Lang("zh", "chi", "zho", "Chinese", "chinese_simplified", ("chinese", "cmn", "yue"), 936, "gbk"),
    Lang("fr", "fre", "fra", "French", "french", ("french",), 1252, "windows-1252"),
    Lang("de", "ger", "deu", "German", "german", ("german",), 1252, "windows-1252"),
    Lang("es", "spa", "spa", "Spanish", "spanish", ("spanish",), 1252, "windows-1252"),
    Lang("th", "tha", "tha", "Thai", "thai", ("thai",), 874, "windows-874"),
    Lang("id", "ind", "ind", "Indonesian", "indonesian", ("indonesian",), 1252, "windows-1252"),
    Lang("ar", "ara", "ara", "Arabic", "arabic", ("arabic",), 1256, "windows-1256"),
    Lang("pt", "por", "por", "Portuguese", "portuguese", ("portuguese",), 1252, "windows-1252"),
    Lang("ru", "rus", "rus", "Russian", "russian", ("russian",), 1251, "windows-1251"),
    Lang("it", "ita", "ita", "Italian", "italian", ("italian",), 1252, "windows-1252"),
    Lang("pl", "pol", "pol", "Polish", "polish", ("polish",), 1250, "windows-1250"),
    Lang("nl", "dut", "nld", "Dutch", "dutch", ("dutch",), 1252, "windows-1252"),
    Lang("tr", "tur", "tur", "Turkish", "turkish", ("turkish",), 1254, "windows-1254"),
]

#: Ma 2 chu -> Lang (giu thu tu uu tien)
LANGS: dict[str, Lang] = {}
_WORD_TO_CODE: dict[str, str] = {}


def _rebuild_index() -> None:
    _WORD_TO_CODE.clear()
    for lg in LANGS.values():
        for w in lg.words():
            _WORD_TO_CODE.setdefault(w.lower(), lg.code)


for _lg in _LANG_LIST:
    LANGS[_lg.code] = _lg
_rebuild_index()

#: 12 ngon ngu co o tick trong SubFetch ($Lang_vi ... $Lang_ar), dung thu tu
FETCH_LANG_SWITCHES: tuple[str, ...] = ("vi", "en", "cs", "ja", "ko", "zh", "fr", "de", "es", "th", "id", "ar")


def register_language(code: str) -> None:
    """Thêm mã ngôn ngữ lạ (từ ``LangExtra`` / cấu hình) để mọi bước nhận ra nó."""
    c = (code or "").strip().lower()
    if not c or c in LANGS or not re.fullmatch(r"[a-z]{2,3}", c):
        return
    LANGS[c] = Lang(c, c, c, c.upper(), "", ())
    _rebuild_index()


def get_lang(code: str) -> Lang | None:
    return LANGS.get((code or "").strip().lower().lstrip("."))


def lang_from_word(w: str | None) -> str:
    """Một từ → mã 2 chữ, '' nếu không nhận ra (``ConvertTo-LangCode``/``ConvertTo-PoolLangCode``)."""
    if not w:
        return ""
    x = str(w).strip().lower()
    return _WORD_TO_CODE.get(x, "")


def lang_from_track(language: str | None, language_ietf: str | None = None) -> str:
    """Ngôn ngữ của track MKV → mã 2 chữ (``Get-LangCode``).

    Xét chuỗi ``"<language> <language_ietf>"`` theo thứ tự ưu tiên của bảng.
    Mã lạ trả nguyên (chữ thường); không có/``und`` → ``'und'``.
    """
    s = f"{language or ''} {language_ietf or ''}".lower()
    toks = set(re.findall(r"[a-z0-9]+", s))
    for lg in LANGS.values():
        if toks & lg.words():
            return lg.code
    lang = (language or "").strip()
    if lang and lang.lower() != "und":
        return lang.lower()
    return "und"


def lang_name(code: str | None) -> str:
    """Tên tiếng Anh để đặt tên track (``Get-LangName``). ``und`` → ''."""
    c = (code or "").strip().lower()
    if c in ("", "und"):
        return ""
    lg = LANGS.get(c)
    return lg.name if lg else c.upper()


def lang_for_mux(code: str, mkv_major_version: int) -> str:
    """Mã ngôn ngữ cho mkvmerge (``Get-LangForMux``): v50+ hiểu mã 2 chữ, bản cũ cần ISO 639-2/B."""
    if mkv_major_version >= 50:
        return code
    lg = LANGS.get(code)
    return lg.iso2b if lg else code


def lang_match(tag: str | None, want: str | None) -> bool:
    """Ngôn ngữ ``tag`` (vd 'cze cs', 'ger', 'vi') có phải ``want`` không (thay ``Test-LangMatch``).

    Sửa lỗi TĐ-04: chuẩn hoá cả hai phía về mã 2 chữ rồi so.
    """
    if not tag or not want:
        return False
    w = want.strip().lower()
    want_c = lang_from_word(w) or w
    for tok in re.findall(r"[a-z0-9]+", tag.lower()):
        if (lang_from_word(tok) or tok) == want_c:
            return True
    if want_c not in LANGS:  # ma la: giu cach so long cua .bat
        t = tag.strip().lower()
        if t == w or (len(t) >= 2 and len(w) >= 2 and t[:2] == w[:2]):
            return True
    return False


def _known_tag(g: str) -> bool:
    return g.lower() in _WORD_TO_CODE and len(g) <= 3


def split_lang_tag(base: str) -> tuple[str, str]:
    """``"Phim.S01E01.vi"`` → ``("Phim.S01E01", "vi")`` (``Split-LangTag``). Hậu tố chuẩn hoá: ``cz`` → ``cs``."""
    m = re.search(r"\.([A-Za-z]{2,3})$", base)
    if m and _known_tag(m.group(1)):
        return base[: m.start()], lang_from_word(m.group(1))
    return base, ""


def lang_tag_of_file(path: str | os.PathLike[str], root: str | os.PathLike[str] | None = None) -> str:
    """Đoán ngôn ngữ một file phụ đề (``Get-FileLangTag`` + ``Get-PoolLang``). Trả mã hoặc ''.

    1) Tên thư mục con CẤP 1 ngay dưới ``root`` (vd ``srt_can_sync\\vi\\...``) — chỉ cấp 1 để
       không nhận nhầm thư mục phim tên 'en'.
    2) Hậu tố cuối tên file: ``Phim.vi.srt``, ``Phim_vietnamese.srt``, ``Phim[cs].srt``…
    """
    full = os.fspath(path)
    base = os.path.splitext(os.path.basename(full))[0]
    if root:
        r = os.fspath(root)
        rel = full
        if rel.lower().startswith(r.lower()):
            rel = rel[len(r):]
        segs = [s for s in re.split(r"[\\/]", rel) if s]
        if len(segs) > 1:
            c = lang_from_word(segs[0])
            if c:
                return c
    m = re.search(r"[\._\-\[\(]([A-Za-z]{2,12})$", base)
    if m:
        c = lang_from_word(m.group(1))
        if c:
            return c
    parts = re.split(r"[\._\-]", base)
    if len(parts) > 1:
        c = lang_from_word(parts[-1])
        if c:
            return c
    return ""


# ==========================================================================
#  TỪ KHOÁ TÊN PHIM + ĐIỂM GIỐNG NHAU
# ==========================================================================

#: Từ kỹ thuật / ngôn ngữ / thể loại — không mang thông tin nhận dạng phim (bản SubPrep, ⊇ SubFetch)
NOISE_WORDS: frozenset[str] = frozenset(
    """
bluray blu ray bdrip brrip bdremux remux webdl web dl webrip web hdtv dvdrip hdrip hddvd
x264 x265 h264 h265 hevc avc xvid aac ac3 eac3 dd ddp dts dtshd truehd atmos flac opus mp3 mp4 mkv avi
hdr dovi sdr imax proper repack extended uncut unrated remastered criterion multi dual vietsub subs sub
subtitles bit amzn nf dsnp hmax atvp hulu pcok itunes internal uhd 4k dolby vision ma
eng rus vie viet vietnamese cze czech ces cz cs en fra ger spa ita jpn kor chi
action drama comedy horror thriller adventure animation
""".split()
)
STOP_WORDS: frozenset[str] = frozenset("the of a an and in on at to for".split())


def title_tokens(name: str) -> list[str]:
    """Tách tên file thành tập từ khoá nhận dạng phim (``Get-TitleTokens``).

    Bỏ phần trong ( ) và [ ], tách theo ký tự không phải chữ/số ASCII, bỏ từ < 2 ký tự,
    số thuần, ``1080p``, từ dừng, từ nhiễu. Giữ thứ tự, không trùng.
    """
    t = re.sub(r"\([^\)]*\)", " ", name)
    t = re.sub(r"\[[^\]]*\]", " ", t)
    out: list[str] = []
    seen: set[str] = set()
    for w in re.split(r"[^A-Za-z0-9]+", t):
        if not w:
            continue
        lw = w.lower()
        if len(lw) < 2:
            continue
        if re.fullmatch(r"\d+", lw) or re.fullmatch(r"\d+p", lw):
            continue
        if lw in STOP_WORDS or lw in NOISE_WORDS:
            continue
        if lw not in seen:
            seen.add(lw)
            out.append(lw)
    return out


def title_score(a: list[str], b: list[str]) -> float:
    """Số từ chung / số từ của bên nhiều hơn (``Get-TitleScore``/``Get-Score``)."""
    if not a or not b:
        return 0.0
    bs = set(b)
    common = sum(1 for x in a if x in bs)
    return common / max(len(a), len(b))


# ==========================================================================
#  TÁCH TÊN PHIM + NĂM
# ==========================================================================

_CRC_RE = re.compile(r"\[[0-9A-Fa-f]{8}\]")
_YEAR_RE = re.compile(r"\b(?:19|20)\d{2}\b")

# Tu ky thuat CHAC CHAN (khong bao gio la mot phan ten phim): cat ten tai day.
_STRONG_JUNK = re.compile(
    r"(?i)\b(4320p|2160p|1440p|1080p|720p|576p|480p|360p|4k|uhd|blu-?ray|blu ray|bdrip|brrip|bdremux|remux|"
    r"web-?dl|web dl|webrip|hdtv|dvdrip|hdrip|x ?26[45]|h ?26[45]|hevc|avc|xvid|aac\d*|e?ac3|ddp?\d?|"
    r"dts(?:-hd)?|truehd|hdr\d*|dovi|10 ?bit|8 ?bit|S\d{1,2}E\d{1,4})\b"
)
# Tu ky thuat co the trung voi tu trong ten phim ("Charlotte's Web", "Uncut Gems", "Dual"):
# chi dung de cat ten khi KHONG co nam.
_WEAK_JUNK = re.compile(
    r"(?i)\b(web|atmos|flac|opus|mp3|sdr|imax|proper|repack|extended|uncut|unrated|remastered|criterion|"
    r"multi|dual|vietsub|thuyet minh|long tieng|amzn|nf|dsnp|hmax|atvp|hulu|pcok|itunes|internal)\b"
)


def _clean_title(s: str) -> str:
    s = re.sub(r"[\(\)\[\]\{\}]", " ", s)
    s = re.sub(r"\s+", " ", s)
    return s.strip(" -_")


@dataclass(frozen=True)
class ReleaseName:
    title: str
    year: int | None


def _normalize_release(name: str) -> str:
    t = _CRC_RE.sub(" ", name)
    t = re.sub(r"\[[^\]]*\]", " ", t)
    t = re.sub(r"[\._]+", " ", t)
    return re.sub(r"\s+", " ", t).strip()


def pick_year(region: str, current_year: int | None = None) -> tuple[str | None, int | None]:
    """Năm = năm CUỐI CÙNG hợp lệ trong ``region``; trả (phần tên trước năm, năm).

    Không phải năm nếu: lớn hơn năm hiện tại + 1 (``Blade Runner 2049``), hoặc bỏ đi thì
    tên rỗng (``2001 A Space Odyssey``, ``1917``). (TĐ-05, đã duyệt Q4.)
    """
    cy = current_year or _dt.date.today().year
    for m in reversed(list(_YEAR_RE.finditer(region))):
        val = int(m.group(0))
        if val > cy + 1:
            continue
        before = _clean_title(region[: m.start()])
        if not before:
            continue
        return before, val
    return None, None


def split_release_name(name: str, current_year: int | None = None) -> ReleaseName:
    """Tên release → tên phim + năm (thay ``Split-ReleaseName``/``Get-CleanTitle``/``Get-Year``).

    ``Avengers.Endgame.2019.2160p.BluRay.x265-GRP`` → ("Avengers Endgame", 2019)
    """
    t = _normalize_release(name)
    m = _STRONG_JUNK.search(t)
    region = t[: m.start()] if m else t
    title, year = pick_year(region, current_year)
    if title is not None:
        return ReleaseName(title, year)
    w = _WEAK_JUNK.search(region)
    if w and _clean_title(region[: w.start()]):
        region = region[: w.start()]
    return ReleaseName(_clean_title(region), None)


def title_year(name: str, current_year: int | None = None) -> int | None:
    return split_release_name(name, current_year).year


_TITLE_KEY_JUNK = re.compile(
    r"(?i)\b(4320p|2160p|1440p|1080p|720p|576p|480p|360p|4k|uhd|bluray|blu ray|bdrip|brrip|bdremux|remux|"
    r"web-?dl|webrip|web|hdtv|dvdrip|hdrip|x ?26[45]|h ?26[45]|hevc|avc|xvid|aac\d*|ac3|eac3|ddp?\d?|"
    r"dts(-hd)?|truehd|atmos|flac|opus|mp3|hdr\d*|dovi|sdr|imax|proper|repack|extended|uncut|unrated|"
    r"remastered|criterion|multi|dual|vietsub|thuyet minh|10 ?bit|8 ?bit|amzn|nf|dsnp|hmax|atvp|hulu|"
    r"pcok|itunes|internal|repack)\b"
)


def title_key(name: str) -> str:
    """``Get-TitleKey`` (SubPrep, chỉ để hiển thị): ``28.Years.Later.2025.1080p…`` → ``28yearslater``."""
    t = re.sub(r"\[[^\]]*\]", " ", name)
    t = re.sub(r"\([^\)]*\)", " ", t)
    t = re.sub(r"[\._]+", " ", t)
    t = re.sub(r"\s+", " ", t)
    m = _TITLE_KEY_JUNK.search(t)
    if m:
        t = t[: m.start()]
    t = re.sub(r"(?i)\b(19|20)\d{2}\b", " ", t)
    return re.sub(r"[^A-Za-z0-9]", "", t).lower()


def norm_title(t: str | None) -> str:
    """``Get-NormTitle``: chỉ giữ chữ + số ASCII, chữ thường."""
    return re.sub(r"[^a-zA-Z0-9]", "", t or "").lower()


# ==========================================================================
#  TÊN FILE AN TOÀN + TÊN CHUẨN JELLYFIN
# ==========================================================================


def safe_filename(n: str) -> str:
    """``Get-SafeFileName``: ``:`` → `` -``, bỏ ký tự Windows cấm, gộp khoảng trắng, bỏ dấu chấm cuối."""
    n = n.replace(":", " -")
    n = re.sub(r'[\\/:*?"<>|]', "", n)
    n = re.sub(r"\s+", " ", n)
    return n.strip().rstrip(".")


def jellyfin_title(title: str) -> str:
    """Tên phim/series sạch ngoặc (Jellyfin đọc nhầm phần trong ngoặc)."""
    t = re.sub(r"[\[\]\(\)\{\}]", " ", safe_filename(title))
    return re.sub(r"\s+", " ", t).strip()


def movie_base_name(title: str, year: int | str, db: str, db_id: int | str) -> str:
    """``Tên phim (Năm) [tmdbid-123]`` / ``[tvdbid-123]`` — tên thư mục và tên file phim lẻ."""
    return f"{jellyfin_title(title)} ({year}) [{db.lower()}id-{db_id}]"


def show_folder_name(name: str, year: int | str | None, db: str, db_id: int | str) -> str:
    """Thư mục series: ``Tên series (Năm) [tmdbid-123]`` (không có năm thì bỏ phần năm)."""
    nm = jellyfin_title(name)
    if year:
        return f"{nm} ({year}) [{db.lower()}id-{db_id}]"
    return f"{nm} [{db.lower()}id-{db_id}]"


def season_folder_name(season: int) -> str:
    """``Season 01``. Không bao giờ ``Season 00`` (Jellyfin coi là tập đặc biệt)."""
    return f"Season {max(1, int(season)):02d}"


def episode_code(season: int, episode: int, episode_end: int | None = None) -> str:
    """``S01E05`` hoặc ``S01E05-E06`` (tập đôi). Season ≤ 0 → 1."""
    s = max(1, int(season))
    code = f"S{s:02d}E{int(episode):02d}"
    if episode_end:
        code += f"-E{int(episode_end):02d}"
    return code


def episode_base_name(show_name: str, year: int | str | None, code: str, episode_title: str | None = None) -> str:
    """``Tên series (Năm) - S01E05 - Tên tập``."""
    prefix = f"{jellyfin_title(show_name)} ({year})" if year else jellyfin_title(show_name)
    base = f"{prefix} - {code}"
    if episode_title:
        et = re.sub(r"[\[\]\{\}]", " ", safe_filename(episode_title))
        et = re.sub(r"\s+", " ", et).strip()
        if et:
            base += f" - {et}"
    return safe_filename(base)
