"""Tên phim, ngôn ngữ, tên file an toàn, tên chuẩn Jellyfin (§6.10 phần không cần mạng)."""
from __future__ import annotations

import os

import pytest

from core import naming as nm


# ---------------- tach ten + nam ----------------
@pytest.mark.parametrize("name,title,year", [
    ("Avengers.Endgame.2019.2160p.BluRay.x265-GRP", "Avengers Endgame", 2019),
    ("28.Years.Later.2025.1080p.AMZN.WEB-DL.DDP5.1", "28 Years Later", 2025),
    ("28 Years Later (2025) NF WEB", "28 Years Later", 2025),
    ("[YTS.MX] Dune Part Two (2024) [2160p]", "Dune Part Two", 2024),
    ("1917 - 2019", "1917", 2019),
    ("1917.2019.1080p", "1917", 2019),
    ("Blade Runner 2049", "Blade Runner 2049", None),                 # TD-05 / Q4
    ("Blade.Runner.2049.2017.1080p.BluRay", "Blade Runner 2049", 2017),
    ("2001 A Space Odyssey", "2001 A Space Odyssey", None),           # TD-05 / Q4
    ("2001.A.Space.Odyssey.1968.1080p", "2001 A Space Odyssey", 1968),
    ("Uncut.Gems.2019.1080p", "Uncut Gems", 2019),
    ("Charlotte's.Web.2006.720p", "Charlotte's Web", 2006),
    ("Movie - 1080p", "Movie", None),
    ("Mission Impossible.EXTENDED.1080p", "Mission Impossible", None),
    ("Some.Movie.2019.EXTENDED.1080p.WEB-DL", "Some Movie", 2019),
])
def test_split_release_name(name, title, year):
    r = nm.split_release_name(name, current_year=2026)
    assert (r.title, r.year) == (title, year)


def test_year_rule_uses_current_year():
    assert nm.split_release_name("Future.Film.2027.1080p", current_year=2026).year == 2027  # nam nay + 1
    assert nm.split_release_name("Future.Film.2028.1080p", current_year=2026).year is None


# ---------------- tu khoa ----------------
def test_title_tokens_subprep_noise_and_order():
    assert nm.title_tokens("The.Lord.of.the.Rings.2001.1080p.BluRay.x264-GRP[YTS] (Extended)") == ["lord", "rings", "grp"]
    assert nm.title_tokens("Action.Movie.Drama.avi") == ["movie"]  # the loai + avi la tu nhieu (ban SubPrep)
    assert nm.title_tokens("Phim Hay 2") == ["phim", "hay"]


def test_title_score():
    a = nm.title_tokens("Dune.Part.Two.2024.2160p")
    b = nm.title_tokens("Dune Part Two (2024) [YTS]")
    assert nm.title_score(a, b) == 1.0
    assert nm.title_score(a, []) == 0.0
    assert nm.title_score(["a1", "b1"], ["a1", "c1", "d1"]) == pytest.approx(1 / 3)


def test_title_key_and_norm():
    assert nm.title_key("28.Years.Later.2025.1080p.AMZN.WEB-DL") == "28yearslater"
    assert nm.title_key("28 Years Later (2025) NF WEB") == "28yearslater"
    assert nm.norm_title("Spider-Man: No Way Home") == "spidermannowayhome"


# ---------------- ngon ngu ----------------
@pytest.mark.parametrize("w,c", [("vi", "vi"), ("VIE", "vi"), ("vietnamese", "vi"), ("tiengviet", "vi"), ("vn", "vi"),
                                 ("cz", "cs"), ("ces", "cs"), ("sec", "cs"), ("anh", "en"), ("eng", "en"), ("ger", "de"),
                                 ("deu", "de"), ("jp", "ja"), ("chi", "zh"), ("ind", "id"), ("ara", "ar"), ("xx", ""), ("", "")])
def test_lang_from_word(w, c):
    assert nm.lang_from_word(w) == c


@pytest.mark.parametrize("lang,ietf,c", [("cze", "cs", "cs"), ("ger", "de", "de"), ("eng", "en", "en"), ("und", "", "und"),
                                         ("", None, "und"), ("spa", "es-419", "es"), ("und", "pt-BR", "pt"),
                                         ("hun", "hu", "hun"), ("jpn", "", "ja"), ("chi", "zh-Hans", "zh")])
def test_lang_from_track(lang, ietf, c):
    assert nm.lang_from_track(lang, ietf) == c


@pytest.mark.parametrize("tag,want,ok", [
    ("cze cs", "cs", True), ("cze ", "cs", True), ("ger", "de", True), ("jpn", "ja", True),  # TD-04: .bat ra False
    ("chi", "zh", True), ("spa", "es", True), ("eng en", "en", True), ("vie", "vi", True),
    ("eng", "cs", False), ("und", "en", False), ("", "en", False),
])
def test_lang_match_fixed(tag, want, ok):
    assert nm.lang_match(tag, want) is ok


def test_lang_name_and_for_mux():
    assert nm.lang_name("vi") == "Vietnamese"
    assert nm.lang_name("und") == ""
    assert nm.lang_name("hun") == "HUN"
    assert nm.lang_for_mux("cs", 50) == "cs"
    assert nm.lang_for_mux("cs", 49) == "cze"
    assert nm.lang_for_mux("de", 40) == "ger"
    assert nm.lang_for_mux("hun", 40) == "hun"


@pytest.mark.parametrize("base,out", [
    ("Phim.S01E01.vi", ("Phim.S01E01", "vi")),
    ("Phim.S01E01.cz", ("Phim.S01E01", "cs")),   # TD-15
    ("Phim.S01E01.eng", ("Phim.S01E01", "en")),
    ("Phim.S01E01", ("Phim.S01E01", "")),
    ("Phim.Two", ("Phim.Two", "")),
])
def test_split_lang_tag(base, out):
    assert nm.split_lang_tag(base) == out


def test_lang_tag_of_file(tmp_path):
    root = tmp_path / "srt_can_sync"
    p = root / "vi" / "[Judas] Show - 01.srt"
    assert nm.lang_tag_of_file(p, root) == "vi"
    assert nm.lang_tag_of_file(root / "Show.S01E01.cs.srt", root) == "cs"
    assert nm.lang_tag_of_file(root / "Show.S01E01_vietnamese.srt", root) == "vi"
    assert nm.lang_tag_of_file(root / "Show.S01E01-en.srt", root) == "en"
    assert nm.lang_tag_of_file(root / "Show.S01E01[en].srt", root) == ""  # nhu .bat: dau ] o cuoi -> khong nhan
    assert nm.lang_tag_of_file(root / "Show.S01E01.srt", root) == ""
    # chi xet thu muc con CAP 1: thu muc phim ten 'en' sau hon khong tinh
    assert nm.lang_tag_of_file(root / "Phim" / "en" / "x.srt", root) == ""
    assert nm.lang_tag_of_file(root / "id" / "x.srt", root) == "id"  # them ngon ngu ngoai 10 cu


def test_register_extra_language():
    nm.register_language("hu")
    assert nm.lang_from_word("hu") == "hu"
    assert nm.split_lang_tag("Film.hu") == ("Film", "hu")


# ---------------- ten file + Jellyfin ----------------
@pytest.mark.parametrize("n,out", [
    ("Mission: Impossible", "Mission - Impossible"),
    ('What? <Why> "No" | a/b\\c *', "What Why No abc"),
    ("Movie.  ", "Movie"),
    ("  A   B ", "A B"),
])
def test_safe_filename(n, out):
    assert nm.safe_filename(n) == out


def test_jellyfin_names():
    assert nm.movie_base_name("Spider-Man: Across the Spider-Verse (Part One)", 2023, "tmdb", 569094) == \
        "Spider-Man - Across the Spider-Verse Part One (2023) [tmdbid-569094]"
    assert nm.movie_base_name("Dune", 2021, "TVDB", 123) == "Dune (2021) [tvdbid-123]"
    assert nm.show_folder_name("One Piece", 1999, "tmdb", 37854) == "One Piece (1999) [tmdbid-37854]"
    assert nm.show_folder_name("One Piece", None, "tvdb", 81797) == "One Piece [tvdbid-81797]"
    assert nm.season_folder_name(0) == "Season 01"  # khong bao gio Season 00
    assert nm.season_folder_name(16) == "Season 16"
    assert nm.episode_code(0, 643) == "S01E643"
    assert nm.episode_code(1, 5, 6) == "S01E05-E06"
    assert nm.episode_base_name("One Piece", 1999, "S21E1085", "The Straw Hats' [Promise]") == \
        "One Piece (1999) - S21E1085 - The Straw Hats' Promise"
    assert nm.episode_base_name("Show", None, "S01E01") == "Show - S01E01"


def test_jellyfin_full_paths_tv_and_movie():
    show = nm.show_folder_name("Show", 2019, "tmdb", 1)
    p = os.path.join(show, nm.season_folder_name(1), nm.episode_base_name("Show", 2019, nm.episode_code(1, 5), "Pilot") + ".mkv")
    assert p.replace("\\", "/") == "Show (2019) [tmdbid-1]/Season 01/Show (2019) - S01E05 - Pilot.mkv"
