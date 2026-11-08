"""§6.3 — PGS (.sup) không cần OCR."""
from __future__ import annotations

import pytest

from core import pgs, srt
from synth import pgs_stream


def test_show_hide_pairs():
    data = pgs_stream([(100, "show"), (102, "hide"), (1900, "show"), (1903.5, "hide")])
    assert pgs.pgs_cues(data) == [(100.0, 102.0), (1900.0, 1903.5)]


def test_palette_only_pcs_is_ignored():
    data = pgs_stream([(10, "show"), (11, "palette"), (12, "hide")])
    assert pgs.pgs_cues(data) == [(10.0, 12.0)]


def test_open_cue_closed_by_next_show():
    data = pgs_stream([(10, "show"), (13, "show"), (15, "hide")])
    assert pgs.pgs_cues(data) == [(10.0, 13.0), (13.0, 15.0)]


def test_short_cues_and_unclosed_tail_dropped():
    data = pgs_stream([(10, "show"), (10.05, "hide"), (20, "show"), (21, "hide"), (30, "show")])
    assert pgs.pgs_cues(data) == [(20.0, 21.0)]


def test_non_pcs_segments_skipped_by_size():
    data = pgs_stream([(5, "other"), (6, "show"), (7, "other"), (8, "hide")])
    assert pgs.pgs_cues(data) == [(6.0, 8.0)]


def test_garbage_stops_parsing():
    data = pgs_stream([(5, "show"), (6, "hide")]) + b"XX" + pgs_stream([(9, "show"), (10, "hide")])
    assert pgs.pgs_cues(data) == [(5.0, 6.0)]
    assert pgs.pgs_cues(b"PG") == []


def test_timing_srt_file_hours_correct(tmp_path):
    """TĐ-01: PGS ở 1900 s / 2700 s phải ra 00:31:40 / 00:45:00 (bản .bat: 01:31:40 / 01:45:00)."""
    data = pgs_stream([(100, "show"), (102, "hide"), (1900, "show"), (1902, "hide"),
                       (2700, "show"), (2702, "hide"), (4000, "show"), (4002, "hide")])
    sup = tmp_path / "[Judas] Phim.sup"
    out = tmp_path / "[Judas] Phim.srt"
    sup.write_bytes(data)
    assert pgs.pgs_to_timing_srt(sup, out, "*") == 4
    txt = out.read_bytes().decode("utf-8")
    assert "00:31:40,000 --> 00:31:42,000" in txt
    assert "00:45:00,000 --> 00:45:02,000" in txt
    assert "01:06:40,000 --> 01:06:42,000" in txt
    assert txt.count("\r\n*\r\n") == 4
    assert srt.cue_times(out) == [100.0, 1900.0, 2700.0, 4000.0]


def test_timing_srt_failure_returns_zero(tmp_path):
    sup = tmp_path / "x.sup"
    sup.write_bytes(b"not pgs at all, nope")
    assert pgs.pgs_to_timing_srt(sup, tmp_path / "x.srt") == 0
    assert pgs.pgs_to_timing_srt(tmp_path / "khong-co.sup", tmp_path / "y.srt") == 0


@pytest.mark.parametrize("t", [0.0, 1.5, 3599.99, 5400.123])
def test_pts_90khz(t):
    data = pgs_stream([(t, "show"), (t + 1, "hide")])
    s, e = pgs.pgs_cues(data)[0]
    assert s == pytest.approx(t, abs=1 / 90000)
    assert e == pytest.approx(t + 1, abs=1 / 90000)
