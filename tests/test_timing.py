"""§6.4 — So khớp thời gian (lõi của mọi phép đo)."""
from __future__ import annotations

import locale
import random

import pytest

from core import timing as tm
from synth import make_ref_times, transform_times

OFFSETS = [0.0, 12.3, -30.45, 95.1]


@pytest.mark.parametrize("ratio", tm.FPS_RATIOS, ids=lambda r: r.name)
@pytest.mark.parametrize("offset", OFFSETS)
def test_relation_finds_known_scale_and_offset(ratio, offset):
    ref = make_ref_times(700, seed=11)
    tgt = transform_times(ref, ratio.ratio, offset, seed=int(abs(offset) * 10) + 3)
    rel = tm.timing_relation(ref, tgt)
    assert rel is not None
    assert rel.scale == ratio.ratio, f"chọn {rel.name}, đúng phải là {ratio.name}"
    assert abs(rel.offset - offset) < 0.1
    assert rel.votes >= 20
    assert rel.fps_mismatch == (ratio.ratio != 1.0)


def test_relation_other_movie_is_below_threshold():
    """Phụ đề của "phim khác" → dưới ngưỡng 20 mốc (khớp sai chỉ 5–15 phiếu)."""
    worst = 0
    for seed in range(12):
        ref = make_ref_times(700, seed=100 + seed)
        other = make_ref_times(650, seed=500 + seed)
        rel = tm.timing_relation(ref, other)
        assert rel is not None
        worst = max(worst, rel.votes)
    assert worst < 20, f"phim khác đạt {worst} phiếu"


def test_relation_needs_30_cues():
    ref = make_ref_times(100)
    assert tm.timing_relation(ref, ref[:29]) is None
    assert tm.timing_relation(ref[:29], ref) is None
    assert tm.timing_relation(None, ref) is None
    assert tm.timing_relation(ref, []) is None
    assert tm.timing_relation(ref, ref[:30]) is not None


def test_relation_drift_uses_last_target_cue():
    ref = make_ref_times(400, seed=5)
    r = tm.FPS_RATIOS[3]  # PAL 25 -> 23.976
    tgt = transform_times(ref, r.ratio, 0.0, seed=6)
    rel = tm.timing_relation(ref, tgt)
    assert rel.drift == pytest.approx(abs((r.ratio - 1.0) * tgt[-1]))


def test_even_sample_rounds_half_to_even_like_powershell():
    arr = list(range(1000))
    s = tm.even_sample(arr, 400)  # step 2.5: [int]2.5 = 2, [int]7.5 = 8
    assert s[:9] == [0, 2, 5, 8, 10, 12, 15, 18, 20]
    assert tm.even_sample([1, 2, 3], 5) == [1, 2, 3]
    assert len(tm.even_sample(list(range(401)), 400)) == 400


def test_offset_vote_tie_keeps_first_inserted_bin():
    # tgt=0: d = 1.5 (o 10) va 3.0 (o 20) -> moi o 1 phieu; o gap truoc (10) thang
    v = tm.offset_vote([1.5, 3.0], [0.0])
    assert v.votes == 1
    assert v.offset == pytest.approx(1.5)


def test_offset_vote_window_equals_full_scan():
    """Bản duyệt cửa sổ (tìm nhị phân) cho kết quả GIỐNG HỆT bản duyệt hết của .bat."""
    rng = random.Random(7)
    for _ in range(20):
        ref = sorted(rng.uniform(0, 6000) for _ in range(400))
        tgt = sorted(rng.uniform(0, 6000) for _ in range(100))
        fast = tm.offset_vote(ref, tgt)
        slow = tm.offset_vote(list(reversed(ref)), tgt)  # khong sap xep -> duyet het
        # dao nguoc ref doi thu tu chen khoa -> chi so so phieu; con so do lech so voi ban duyet het cung thu tu:
        assert fast.votes == slow.votes
    # cung thu tu: so sanh voi cai dat duyet het viet tay
    ref = sorted(rng.uniform(0, 6000) for _ in range(400))
    tgt = sorted(rng.uniform(0, 6000) for _ in range(100))
    votes: dict[int, int] = {}
    for t in tgt:
        for r in ref:
            d = r - t
            if -400 <= d <= 400:
                k = round(d / 0.15)
                votes[k] = votes.get(k, 0) + 1
    bk, bv = 0, -1
    for k, c in votes.items():
        if c > bv:
            bv, bk = c, k
    c = bk * 0.15
    s = 0.0
    n = 0
    for t in tgt:
        for r in ref:
            if abs((r - t) - c) <= 0.35:
                s += r - t
                n += 1
    got = tm.offset_vote(ref, tgt)
    assert got.votes == bv
    assert got.offset == (s / n if n else c)  # bang nhau tung bit


def test_quick_offset_true_previews_accepted_random_rejected():
    """Preview đúng: nhận 60/60; preview ngẫu nhiên: lọt 0/60 (như ghi chú trong .bat)."""
    rng = random.Random(42)
    ok = 0
    bad = 0
    for i in range(60):
        ref = make_ref_times(700, seed=1000 + i)
        off = round(rng.uniform(-170, 170), 1)
        start = rng.randrange(0, 50)
        pv = [r - off for r in ref[start:start + 5]]
        pv = [t for t in pv if t > 0]
        if len(pv) < 3:
            pv = [r - off for r in ref[100:105]]
        q = tm.quick_offset(ref, pv)
        if q is not None and abs(q.offset - off) <= 0.125:
            ok += 1
        rnd = sorted(rng.uniform(0, 5000) for _ in range(5))
        if tm.quick_offset(ref, rnd) is not None:
            bad += 1
    assert ok == 60
    assert bad == 0


def test_quick_offset_with_jitter_inside_bin():
    ref = make_ref_times(500, seed=9)
    pv = [r - 12.5 + d for r, d in zip(ref[10:15], (0.03, -0.02, 0.01, -0.03, 0.02))]
    q = tm.quick_offset(ref, pv)
    assert q is not None and q.offset == 12.5 and q.total == 5


def test_quick_offset_needs_three_cues():
    ref = make_ref_times(100)
    assert tm.quick_offset(ref, ref[:2]) is None
    assert tm.quick_offset(None, ref[:5]) is None


def test_ref_bins_matches_linear_scan():
    ref = make_ref_times(800, seed=3)
    bins = tm.RefBins(ref)
    rng = random.Random(4)
    for _ in range(2000):
        x = rng.uniform(0, 7000)
        c = int(x // 1)
        linear = any(int(r // 1) in (c, c - 1, c + 1, c - 2) for r in ref)
        assert bins.near(x) == linear


@pytest.mark.parametrize("s,v", [("23.976", 23.976), ("23,976", 23.976), (" 25 ", 25.0), ("1e3", 1000.0),
                                 ("", 0.0), (None, 0.0), ("abc", 0.0), ("1,234.5", 0.0), (24, 24.0), (True, 0.0)])
def test_to_double_locale_independent(s, v):
    assert tm.to_double(s) == v


def test_to_double_under_czech_locale():
    for name in ("cs_CZ.UTF-8", "vi_VN.UTF-8", "de_DE.UTF-8"):
        try:
            old = locale.setlocale(locale.LC_NUMERIC)
            locale.setlocale(locale.LC_NUMERIC, name)
        except locale.Error:
            continue
        try:
            assert tm.to_double("23.976") == 23.976
            assert tm.fmt_inv(23.976) == "23.976"
        finally:
            locale.setlocale(locale.LC_NUMERIC, old)


@pytest.mark.parametrize("x,s", [(25 / 23.976, "1.042709"), (1.0, "1"), (0.5, "0.5"), (-0.0000001, "0"), (48000.0, "48000")])
def test_fmt_inv_dot_decimal(x, s):
    assert tm.fmt_inv(x) == s
    assert "," not in tm.fmt_inv(x)


def test_fps_ratio_table_order_matches_bat():
    assert [round(r.ratio, 6) for r in tm.FPS_RATIOS] == [
        1.0, round(24 / 23.976, 6), round(23.976 / 24, 6), round(25 / 23.976, 6),
        round(23.976 / 25, 6), round(25 / 24, 6), round(24 / 25, 6)]
