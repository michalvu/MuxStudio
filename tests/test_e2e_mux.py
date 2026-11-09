"""Bước ③ đầu–cuối với ffmpeg / ffprobe / mkvmerge / mkvextract THẬT (thiếu thì tự bỏ qua).

* Test TIẾNG BÍP (§8 PROMPT): phim 23,976 fps có phụ đề tiếng Anh; "bản nguồn" 25 fps có audio tiếng Séc là tiếng bíp
  đặt đúng từng câu thoại, lệch +1,5 s (và phụ đề tiếng Anh của chính bản nguồn). Ghép → đo vị trí tiếng bíp trong
  file kết quả bằng ``silencedetect``: sai số < 20 ms ở đầu và cuối phim. Thêm ca cùng FPS, lệch âm (không encode lại).
  Audio tiếng Anh GỐC của phim cũng là tiếng bíp tại đúng câu thoại → so bíp của audio ghép với bíp của audio gốc trong
  CÙNG một lần giải mã (đúng thứ người xem nghe). Audio gốc để PCM: AAC có "đoạn mồi" ~21 ms mà ffmpeg mới tự cắt,
  ffmpeg cũ (bản đi kèm alass trên Windows) thì không → đo theo AAC thì kết quả phụ thuộc phiên bản ffmpeg.
* Mux thật: mã thoát 0 / ≥ 2 (xoá file dở dang).
"""
from __future__ import annotations

import array
import math
import re
import shutil
import subprocess
import wave

import pytest

from core import config as C
from core import mux, tools
from synth import make_ref_times, srt_text


def _find(name):
    return shutil.which(name) or tools.find_tool(name, C.Config({}))


T = {n: _find(n) for n in ("mkvmerge", "mkvextract", "ffmpeg", "ffprobe")}
pytestmark = pytest.mark.skipif(not all(T.values()), reason="thiếu mkvmerge/mkvextract/ffmpeg/ffprobe")
SR = 48000
BEEP = 0.15  # giay


def beep_wav(path, beeps, dur):
    """WAV mono 48 kHz: im lặng, tiếng bíp 1 kHz dài 150 ms tại mỗi mốc."""
    n = int(dur * SR)
    buf = array.array("h", bytes(2 * n))
    tone = array.array("h", (int(16000 * math.sin(2 * math.pi * 1000 * i / SR)) for i in range(int(BEEP * SR))))
    for t in beeps:
        i = int(round(t * SR))
        buf[i:i + len(tone)] = tone[: max(0, min(len(tone), n - i))]
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(buf.tobytes())


def make_video(path, fps, dur, audio_wav=None):
    """Video đen + audio tiếng Anh: im lặng (AAC), hoặc lấy từ ``audio_wav`` — khi đó giữ PCM: AAC có "đoạn mồi"
    1024 mẫu (~21 ms) mà ffmpeg cũ (vd bản đi kèm alass trên Windows) không cắt khi giải mã → bíp của audio GỐC đo trễ
    ~21 ms, phép đo phụ thuộc phiên bản ffmpeg chứ không phải kết quả ghép."""
    src = ["-i", str(audio_wav)] if audio_wav else ["-f", "lavfi", "-i", f"anullsrc=r={SR}:cl=mono"]
    acodec = "pcm_s16le" if audio_wav else "aac"
    subprocess.run([T["ffmpeg"], "-v", "error", "-y", "-f", "lavfi", "-i", f"color=c=black:s=64x36:r={fps}:d={dur}",
                    *src, "-t", str(dur), "-c:v", "mpeg4", "-c:a", acodec, "-metadata:s:a:0", "language=eng", str(path)],
                   check=True)


def make_mkv(out, video, extra):
    """extra: [(file, language)]"""
    cmd = [T["mkvmerge"], "-q", "-o", str(out), str(video)]
    for f, lang in extra:
        cmd += ["--language", f"0:{lang}", str(f)]
    r = subprocess.run(cmd, capture_output=True)
    assert r.returncode in (0, 1), r.stdout


def beep_onsets(mkv, a_index):
    r = subprocess.run([T["ffmpeg"], "-v", "info", "-i", str(mkv), "-map", f"0:a:{a_index}", "-af",
                        "silencedetect=noise=-40dB:d=0.05", "-f", "null", "-"], capture_output=True, text=True)
    return [float(x) for x in re.findall(r"silence_end: ([0-9.]+)", r.stderr)]


def build(root, mine_fps, donor_scale, donor_shift, n=45, seed=21):
    """Thư viện: phim của bạn (mốc M_i) + bản nguồn trong audio_nguon (mốc D_i = M_i / scale + shift)."""
    rng_times = make_ref_times(n, start=10.0, seed=seed, min_gap=4.0, max_gap=8.0)
    dur = rng_times[-1] + 15
    work = root / "_tao"
    work.mkdir()
    mine_v = work / "mine.mkv"
    mine_wav = work / "en.wav"
    beep_wav(mine_wav, rng_times, dur)  # audio goc cua phim: bip dung tung cau thoai
    make_video(mine_v, mine_fps, dur, mine_wav)
    en = work / "en.srt"
    en.write_text(srt_text(rng_times, 1.0), encoding="utf-8", newline="")
    stem = "Phim.Bip.2020.1080p"
    make_mkv(root / f"{stem}.mkv", mine_v, [(en, "eng")])
    d_times = [m / donor_scale + donor_shift for m in rng_times]
    d_dur = dur / donor_scale + donor_shift + 5
    donor_v = work / "donor.mkv"
    make_video(donor_v, "25" if donor_scale != 1.0 else mine_fps, d_dur)
    wav = work / "cs.wav"
    beep_wav(wav, d_times, d_dur)
    den = work / "den.srt"
    den.write_text(srt_text(d_times, 1.0), encoding="utf-8", newline="")
    (root / "audio_nguon").mkdir()
    # file nguon: video + audio Sec (tieng bip PCM) + phu de Anh cua chinh ban nguon (bo audio Anh cua video nen)
    cmd = [T["mkvmerge"], "-q", "-o", str(root / "audio_nguon" / f"{stem}.CZ.mkv"), "--no-audio", str(donor_v),
           "--language", "0:cze", str(wav), "--language", "0:eng", str(den)]
    r = subprocess.run(cmd, capture_output=True)
    assert r.returncode in (0, 1), r.stdout
    shutil.rmtree(work)
    return stem, rng_times


def run_mux(root, cfg=None):
    s = mux.MuxSession(root, C.Config({"DonorLangs": ["cs"], **(cfg or {})}), {}, tools.Job(), tool_paths=T)
    plans = s.plan_all("none")
    assert len(plans) == 1 and plans[0].status == "ok", plans[0].reasons
    rep = s.execute(plans)
    s.close()
    return plans[0], rep


@pytest.mark.parametrize("case", ["pal_plus_1_5s", "same_fps_minus_2s"])
def test_beep_alignment_under_20ms(movie_dir, case):
    if case == "pal_plus_1_5s":
        stem, times = build(movie_dir, "24000/1001", 25 / 23.976, 1.5)
    else:
        stem, times = build(movie_dir, "24000/1001", 1.0, -2.0, seed=22)
    plan, rep = run_mux(movie_dir)
    assert rep.ok == 1, [m for _, m in plan.notes]
    (dt,) = plan.donor.tracks
    if case == "pal_plus_1_5s":
        assert dt.stretch and "asetrate=" in dt.filter and "atempo" not in dt.filter and plan.donor.timing.votes >= 20
    else:
        assert not dt.stretch and dt.offset_ms == 2000  # cung FPS: --sync, KHONG encode lai
    out = plan.out_path
    orig, dub = beep_onsets(out, 0), beep_onsets(out, 1)  # cung mot lan giai ma
    assert len(dub) >= len(times) - 2, dub[:5]
    for want in (times[0], times[-1]):  # dau phim va cuoi phim
        o = min(orig, key=lambda x: abs(x - want))
        d = min(dub, key=lambda x: abs(x - want))
        assert abs(o - want) < 0.030  # bip cua audio goc dung cau thoai
        assert abs(d - o) < 0.020, (want, o, d)  # yeu cau §8: < 20 ms
        # thuc te: ffmpeg moi < 2 ms; ffmpeg cu khong cat "doan moi" 256 mau cua E-AC-3 (~5,3 ms). Chuoi atempo cu: ~21 ms
        assert abs(d - o) < 0.010, (want, o, d)


def test_real_mux_exit_code_error_deletes_partial(movie_dir):
    stem = "Phim.Loi.2020"
    v = movie_dir / "v.mkv"
    make_video(v, "24000/1001", 2)
    shutil.move(v, movie_dir / f"{stem}.mkv")
    sub = movie_dir / f"{stem}.vi.srt"
    sub.write_text(srt_text([0.5], 0.5), encoding="utf-8", newline="")
    s = mux.MuxSession(movie_dir, C.Config({"UseDonorAudio": False}), {}, tools.Job(), tool_paths=T)
    (plan,) = s.plan_all("none")
    sub.write_bytes(b"\x00\x01khong phai phu de")  # mkvmerge khong doc duoc -> ma thoat 2
    rep = s.execute([plan])
    s.close()
    assert rep.failed == 1 and plan.result.code >= 2
    assert not any(p.suffix == ".mkv" for p in (movie_dir / "Output").iterdir())  # khong de lai file do dang
