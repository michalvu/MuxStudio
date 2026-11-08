"""§6.6 — Nhận diện tên tập (bản gộp dùng chung ba bước)."""
from __future__ import annotations

import pytest

from core import episodes as ep

# (ten, season, tap, tap_cuoi, co_season, ten_series)
EPISODES = [
    ("Show.S01E05.1080p.WEB-DL", 1, 5, None, True, "Show"),
    ("One Piece S21E1085 1080p", 21, 1085, None, True, "One Piece"),
    ("Show 1x05 720p", 1, 5, None, True, "Show"),
    ("One Piece - 0643", 0, 643, None, False, "One Piece"),
    ("[Judas] One Piece - 1085 [1080p]", 0, 1085, None, False, "One Piece"),
    ("Naruto EP 055", 0, 55, None, False, "Naruto"),
    ("One.Piece.S16E643.1080p", 16, 643, None, True, "One Piece"),
    ("Show.S01E05-E06.1080p", 1, 5, 6, True, "Show"),
    ("Show.S01E05E06", 1, 5, 6, True, "Show"),
    ("Show.S02E05.720p", 2, 5, None, True, "Show"),
    ("Show Season 2 Episode 5", 2, 5, None, True, "Show"),
    ("One Piece - 1085[1080p]", 0, 1085, None, False, "One Piece"),
    ("[SubsPlease] Frieren - 12 (1080p) [ABCD1234]", 0, 12, None, False, "Frieren"),
    ("Kimetsu - 05v2 [1080p]", 0, 5, None, False, "Kimetsu"),
    ("Phim Tap 12", 0, 12, None, False, "Phim"),
    ("Show.Ep.07.HDTV", 0, 7, None, False, "Show"),
    ("Doctor.Who.2005.S01E01.720p", 1, 1, None, True, "Doctor Who"),
    ("1923.S01E01.1080p", 1, 1, None, True, "1923"),
    ("[YTS.MX] Show (2019) S03E10 [1080p]", 3, 10, None, True, "Show"),
]

MOVIES = [
    "1917 - 2019",
    "Blade Runner 2049",
    "Movie - 1080p",
    "Movie.2019.DDP5.1.x264",
    "28.Years.Later.2025.1080p.AMZN.WEB-DL.DDP5.1",
    "Avengers.Endgame.2019.2160p.BluRay.x265-GRP",
    "[YTS.MX] Dune Part Two (2024) [2160p]",
    "2001 A Space Odyssey",
    "Spider-Man.No.Way.Home.2021.1080p.E-AC3",
    "Epic.Movie.2007.720p",
]


@pytest.mark.parametrize("name,season,episode,end,has_season,series", EPISODES)
def test_episode_names(name, season, episode, end, has_season, series):
    e = ep.parse_episode(name)
    assert e is not None
    assert (e.season, e.episode, e.episode_end, e.has_season, e.confident) == (season, episode, end, has_season, True)
    assert e.series == series


def test_series_year():
    assert ep.parse_episode("Doctor.Who.2005.S01E01").year == 2005
    assert ep.parse_episode("Show (2019) S03E10").year == 2019


@pytest.mark.parametrize("name", MOVIES)
def test_movies_are_not_episodes(name):
    assert ep.parse_episode(name) is None
    assert not ep.is_episode(name)


def test_guess_only_when_asked_and_never_confident():
    g = ep.parse_episode("Movie.2019.DDP5.1.x264", allow_guess=True)
    assert g is not None and g.confident is False and g.episode == 1
    assert ep.parse_episode("Movie.2019.DDP5.1.x264") is None


@pytest.mark.parametrize("a,b,conflict", [
    ("Show.S01E10", "Show.S01E01", True),
    ("Show.S01E01", "Show.S02E01", True),
    ("Show.S01E05", "Show.S01E05.720p.vi", False),
    ("One Piece - 0643", "One Piece - 0644", True),    # SubFetch cu: False (TD-02)
    ("One.Piece.S16E643", "One Piece - 0643", False),
    ("Show.S01E05", "Show - 05", False),
    ("Show.S01E05", "Movie (2019)", False),
    ("Movie.2019.DDP5.1", "Movie.2019.DDP5.1", False),  # doan "khong chac" khong bao gio chan
])
def test_episode_conflict(a, b, conflict):
    assert ep.episode_conflict(a, b) is conflict
    assert ep.episode_conflict(b, a) is conflict


@pytest.mark.parametrize("a,b,same", [
    ("Show.S01E05", "Show.S01E05", True),
    ("Show.S01E05", "Show.S02E05", False),
    ("One Piece - 0643", "[Judas] One Piece - 643 [1080p]", True),
    ("One.Piece.S16E643", "One Piece - 0643", True),       # so tap >= 100
    ("Show.S01E05", "Show - 05", True),                    # season 1
    ("Show.S02E05", "Show - 05", False),                   # tap le cua season khac
    ("Show.S01E05", "Movie", False),
])
def test_same_episode(a, b, same):
    assert ep.same_episode_names(a, b) is same


def test_labels():
    assert ep.parse_episode("Show.S01E05").label == "S01E05"
    assert ep.parse_episode("Show.S01E05").ref_label == "S01E05"
    assert ep.parse_episode("One Piece - 0643").label == "E643"
    assert ep.parse_episode("One Piece - 0643").ref_label == "tap #643"
    assert ep.parse_episode("Show 1x05").label == "S01E05"


def test_filter_by_episode_before_trimming_candidates():
    """§6.6: lọc theo số tập TRƯỚC rồi mới cắt danh sách — tên các tập gần như y hệt nhau."""
    from core import naming as nm
    video = "[Judas] One Piece - 0647 [1080p]"
    pool = [f"[Judas] One Piece - {i:04d} [1080p].vi" for i in range(641, 653)]
    vt = nm.title_tokens(video)
    scores = {p: nm.title_score(vt, nm.title_tokens(p)) for p in pool}
    assert len(set(scores.values())) == 1  # diem ten khong phan biet duoc tap
    kept = [p for p in pool if not ep.episode_conflict(video, p)]
    assert kept == ["[Judas] One Piece - 0647 [1080p].vi"]
