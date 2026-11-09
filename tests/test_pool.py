"""Kho nội bộ srt_pool — §6.11 nguồn 2."""
from __future__ import annotations

from core import config as C
from core import naming, pool, tools
from synth import make_ref_times, srt_text, transform_times


def write(p, times):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(srt_text(times), encoding="utf-8", newline="")
    return p


def test_index_language_from_subfolder_or_suffix(tmp_path):
    root = tmp_path / "srt_pool"
    write(root / "vi" / "[Judas] Phim.srt", [1.0])
    write(root / "Phim.cs.srt", [1.0])
    write(root / "Phim_english.srt", [1.0])
    write(root / "khongro.srt", [1.0])
    write(root / "vi" / "Phim.forced.srt", [1.0])
    write(root / "en" / "Phim.SDH.srt", [1.0])
    job = tools.Job()
    idx = pool.build_index(root, job=job)
    assert {k: [p.name for p in v] for k, v in idx.by_lang.items()} == {
        "vi": ["[Judas] Phim.srt"], "cs": ["Phim.cs.srt"], "en": ["Phim_english.srt"]}
    assert idx.unknown == 1 and any("không rõ ngôn ngữ" in m for _, m in job.lines)
    idx2 = pool.build_index(root, default_lang="vi")
    assert len(idx2.by_lang["vi"]) == 2


def test_pool_vote_thresholds(tmp_path):
    cfg = C.Config()
    ref = make_ref_times(700, seed=21)
    root = tmp_path / "srt_pool" / "vi"
    good = write(root / "Phim.2020.good.srt", transform_times(ref, 1.0, 3.3, seed=1))
    pal = write(root / "Phim.2020.pal.srt", transform_times(ref, 25 / 23.976, 0.0, seed=2))
    other = write(root / "Phim.2020.other.srt", make_ref_times(300, start=4300.0, seed=999))  # phim khac: 4 moc khop
    # 8-19 phieu: giu rat it cau thoai trung
    weak = write(root / "Phim.2020.weak.srt", sorted(transform_times(ref, 1.0, 0.0, keep=0.25, seed=3)
                                                    + make_ref_times(400, seed=77)))
    short = write(root / "Phim.2020.short.srt", ref[:20])
    idx = pool.build_index(tmp_path / "srt_pool")
    job = tools.Job()
    cands = pool.pool_candidates(idx, "vi", "Phim.2020.1080p", naming.title_tokens("Phim.2020.1080p"), ref, cfg, job)
    by = {c.payload.name: c for c in cands}
    assert by[good.name].tier == 1 and by[good.name].fps == "đo: khớp" and abs(by[good.name].off.offset - 3.3) < 0.1
    assert by[pal.name].tier == 3 and "LỆCH FPS" in by[pal.name].info
    assert other.name not in by and any(f"bỏ: {other.name}" in m for _, m in job.lines)  # < 8 moc -> bo, in ten
    w = by[weak.name]
    assert w.tier == 2 and w.fps == "đo: nghi ngờ" and "cần kiểm tra" in w.info and 8 <= w.off.votes < 20
    assert by[short.name].off is None and "quá ngắn" in by[short.name].info
    assert by[good.name].off.total == -1  # do that, khong phai uoc tinh


def test_episode_filter_before_try_limit(tmp_path):
    """Lọc theo số tập TRƯỚC rồi mới cắt PoolTryLimit — tên các tập gần như y hệt nhau."""
    cfg = C.Config({"PoolTryLimit": 2})
    root = tmp_path / "srt_pool" / "vi"
    for i in range(1, 13):
        write(root / f"[Judas] One Piece - {640 + i:04d} [1080p].srt", [1.0])
    idx = pool.build_index(tmp_path / "srt_pool")
    name = "[Judas] One Piece - 0647 [1080p]"
    cands = pool.pool_candidates(idx, "vi", name, naming.title_tokens(name), None, cfg)
    assert [c.release for c in cands] == ["[Judas] One Piece - 0647 [1080p].srt"]


def test_no_ref_means_unmeasured_tier2(tmp_path):
    cfg = C.Config()
    write(tmp_path / "srt_pool" / "en" / "Movie.2019.srt", make_ref_times(100))
    idx = pool.build_index(tmp_path / "srt_pool")
    c = pool.pool_candidates(idx, "en", "Movie.2019", ["movie"], None, cfg)[0]
    assert (c.tier, c.fps, c.off, c.src) == (2, "?", None, "Kho")


def test_missing_language(tmp_path):
    job = tools.Job()
    assert pool.pool_candidates(pool.build_index(tmp_path / "khong-co"), "vi", "x", [], None, C.Config(), job) == []
    assert any("không có file nào" in m for _, m in job.lines)
