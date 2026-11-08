"""§6.3 — Đọc mốc thời gian; TĐ-01 — lỗi giờ của Format-SrtStamp."""
from __future__ import annotations

import pytest

from core import srt
from synth import srt_text


@pytest.mark.parametrize("sec,txt", [
    (0.0, "00:00:00,000"),
    (10.5, "00:00:10,500"),
    (1799.0, "00:29:59,000"),
    (2700.0, "00:45:00,000"),      # .bat: 01:45:00,000
    (3599.9, "00:59:59,900"),      # .bat: 01:59:59,900
    (3600.0, "01:00:00,000"),
    (5400.0, "01:30:00,000"),      # .bat: 02:30:00,000
    (6000.25, "01:40:00,250"),     # .bat: 02:40:00,250
    (0.0005, "00:00:00,001"),      # lam tron mili-giay nhu .NET Framework
    (59.9996, "00:01:00,000"),
    (-3.0, "00:00:00,000"),
    (36000.0, "10:00:00,000"),
])
def test_format_stamp_hours_are_floor_not_rounded(sec, txt):
    assert srt.format_stamp(sec) == txt


def test_stamp_roundtrip_every_minute_for_three_hours():
    for m in range(0, 180):
        s = m * 60 + 0.123
        assert srt.parse_stamp(srt.format_stamp(s)) == pytest.approx(s, abs=1e-9)


@pytest.mark.parametrize("t,v", [("01:02:03,456", 3723.456), ("01:02:03.456", 3723.456), ("0:00:01,50", 1.5), ("00:00:01,05", 1.05)])
def test_parse_stamp(t, v):
    assert srt.parse_stamp(t) == pytest.approx(v)


def test_cue_times_srt_sorted(tmp_path):
    p = tmp_path / "[Judas] x.srt"
    p.write_text(srt_text([30.0, 10.0, 2700.5]), encoding="utf-8")
    assert srt.cue_times(p) == [10.0, 30.0, 2700.5]


def test_cue_times_srt_dot_separator_and_two_digits():
    txt = "1\n00:00:01.50 --> 00:00:02.00\nA\n\n2\n00:00:03,250 --> 00:00:04,000\nB\n"
    assert srt.cue_times_from_text(txt) == [1.5, 3.25]


def test_cue_times_ass_numeric_layer_and_marked():
    ass = (
        "[Script Info]\nTitle: x\n\n[Events]\n"
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
        "Dialogue: 0,0:00:05.20,0:00:07.00,Default,,0,0,0,,Xin chào\n"
        "Dialogue: Marked=0,0:01:02.34,0:01:03.00,Default,,0,0,0,,SSA v4\n"
        "Comment: 0,0:00:09.00,0:00:10.00,Default,,0,0,0,,bỏ qua\n"
    )
    assert srt.cue_times_from_text(ass) == [5.2, 62.34]


def test_cue_times_vtt_with_and_without_hours():
    vtt = "WEBVTT\n\n00:01.000 --> 00:04.000\nA\n\n01:02:03.500 --> 01:02:05.000\nB\n\n00:10.250 --> 00:11.000\nC\n"
    assert srt.cue_times_from_text(vtt) == [1.0, 10.25, 3723.5]


def test_cue_times_utf16_bom(tmp_path):
    p = tmp_path / "u16.srt"
    p.write_bytes(b"\xff\xfe" + srt_text([1.0, 2.0]).encode("utf-16-le"))
    assert srt.cue_times(p) == [1.0, 2.0]


def test_cue_times_none_when_nothing(tmp_path):
    p = tmp_path / "empty.srt"
    p.write_text("", encoding="utf-8")
    assert srt.cue_times(p) is None
    assert srt.cue_times(tmp_path / "khong-co.srt") is None
    assert srt.cue_times_from_text("hello") is None


def test_preview_times():
    pv = "1\n00:00:12,000 --> 00:00:14,000\nHi\n\n2\n00:00:03,500 --> 00:00:05,000\nHo"
    assert srt.preview_times(pv) == [3.5, 12.0]
    assert srt.preview_times(None) == []


def test_blocks_roundtrip_keeps_hours_text_and_tail(tmp_path):
    times = [5.0, 1900.0, 2700.0, 3599.0, 4000.0, 6000.25]
    texts = [f"Dòng {i}\nhai dòng" for i in range(len(times))]
    p = tmp_path / "a.srt"
    p.write_text(srt_text(times, texts=texts), encoding="utf-8")
    blocks = srt.read_blocks(p)
    blocks[1].tail = " X1:10 X2:20"
    out = tmp_path / "b.srt"
    assert srt.write_blocks(out, blocks)
    again = srt.read_blocks(out)
    assert [b.start for b in again] == times
    assert [b.text.replace("\r\n", "\n") for b in again] == texts
    assert again[1].tail == " X1:10 X2:20"
    raw = out.read_bytes()
    assert not raw.startswith(b"\xef\xbb\xbf")  # UTF-8 khong BOM
    assert b"\r\n" in raw  # CRLF nhu .bat
    assert b"00:45:00,000 --> " in raw and b"01:45:00" not in raw


def test_read_blocks_without_trailing_newline_and_renumbering(tmp_path):
    p = tmp_path / "c.srt"
    p.write_text("7\n00:00:01,000 --> 00:00:02,000\nA\n\n9\n00:00:03,000 --> 00:00:04,000\nB", encoding="utf-8")
    bl = srt.read_blocks(p)
    assert [b.text for b in bl] == ["A", "B"]
    out = tmp_path / "d.srt"
    srt.write_blocks(out, bl)
    assert out.read_bytes().decode("utf-8").startswith("1\r\n00:00:01,000")


def test_read_blocks_returns_none_for_ass(tmp_path):
    p = tmp_path / "x.ass"
    p.write_text("[Events]\nDialogue: 0,0:00:05.20,0:00:07.00,Default,,0,0,0,,X\n", encoding="utf-8")
    assert srt.read_blocks(p) is None  # buoc goi bo qua, KHONG doi dinh dang (Q7)


def test_max_shift():
    assert srt.max_shift([10, 20, 30], [12, 21, 35]) == 5
    assert srt.max_shift([10, 20], [4, 21]) == 6
    assert srt.max_shift(None, [1]) is None
