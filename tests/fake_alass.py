"""alass GIẢ cho test bước ② (không cần alass thật).

Gọi giống alass thật: ``python fake_alass.py [tham số...] REF INC OUT``.
Cấu hình qua biến môi trường ``FAKE_ALASS`` = đường dẫn file JSON:

* ``log``: file JSONL — mỗi lần gọi ghi ``{flags, ref, inc, out, wav}`` (``wav`` = kênh/tần số/bit nếu REF là WAV)
* ``help``: chữ in ra cho ``--help``
* ``offset``: số giây cộng vào mọi mốc (mặc định 0)
* ``fail``: danh sách luật ``{"ref": "wav"|"video"|"sub"|"any", "flags": true|false}``; khớp luật nào → mã 1
* ``zero_first``: dồn N dòng đầu về 00:00:00 (giả lập lỗi alass)
"""
from __future__ import annotations

import json
import os
import re
import sys
import wave

DEFAULT_HELP = """alass 2.0.0 (giả)
USAGE: alass [OPTIONS] <REFERENCE_FILE> <INCORRECT_SUB_FILE> <OUTPUT_FILE>
    --no-split               Do not try to split
    --disable-fps-guessing   Do not guess framerate
    --split-penalty <n>      Penalty
"""


def stamp(sec: float) -> str:
    ms = int(round(max(0.0, sec) * 1000))
    h, ms = divmod(ms, 3_600_000)
    m, ms = divmod(ms, 60_000)
    s, ms = divmod(ms, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def ass_stamp(sec: float) -> str:
    cs = int(round(max(0.0, sec) * 100))
    h, cs = divmod(cs, 360_000)
    m, cs = divmod(cs, 6000)
    s, cs = divmod(cs, 100)
    return f"{h}:{m:02d}:{s:02d}.{cs:02d}"


def main(argv: list[str]) -> int:
    conf = {}
    if os.environ.get("FAKE_ALASS"):
        with open(os.environ["FAKE_ALASS"], encoding="utf-8") as fh:
            conf = json.load(fh)
    if "--help" in argv:
        print(conf.get("help", DEFAULT_HELP))
        return 0
    flags = [a for a in argv[:-3]]
    ref, inc, out = argv[-3:]
    rec = {"flags": flags, "ref": ref, "inc": inc, "out": out, "wav": None}
    if ref.lower().endswith(".wav"):
        with wave.open(ref, "rb") as w:
            rec["wav"] = {"channels": w.getnchannels(), "rate": w.getframerate(), "bits": w.getsampwidth() * 8}
    if conf.get("log"):
        with open(conf["log"], "a", encoding="utf-8") as fh:
            fh.write(json.dumps(rec) + "\n")
    kind = "wav" if ref.lower().endswith(".wav") else "video" if ref.lower().endswith((".mkv", ".mp4")) else "sub"
    for rule in conf.get("fail", []):
        if rule.get("ref", "any") in ("any", kind) and rule.get("flags", bool(flags)) == bool(flags):
            print("error: missing field `codec_long_name`", file=sys.stderr)
            return 1
    off = float(conf.get("offset", 0.0))
    zero = int(conf.get("zero_first", 0))
    with open(inc, encoding="utf-8", errors="replace", newline="") as fh:
        txt = fh.read()
    if inc.lower().endswith((".ass", ".ssa")):
        def sh(m: re.Match) -> str:
            def t(h, mi, s, c):
                return int(h) * 3600 + int(mi) * 60 + int(s) + int(c) / 100
            a = t(*m.group(2, 3, 4, 5)) + off
            b = t(*m.group(6, 7, 8, 9)) + off
            return f"{m.group(1)}{ass_stamp(a)},{ass_stamp(b)},"
        txt = re.sub(r"(?m)^(Dialogue:\s*[^,]*,)(\d+):(\d{2}):(\d{2})\.(\d{2}),(\d+):(\d{2}):(\d{2})\.(\d{2}),", sh, txt)
    else:
        n = [0]

        def sh2(m: re.Match) -> str:
            def t(h, mi, s, ms):
                return int(h) * 3600 + int(mi) * 60 + int(s) + int(ms) / 1000
            n[0] += 1
            a = t(*m.group(1, 2, 3, 4)) + off
            b = t(*m.group(5, 6, 7, 8)) + off
            if n[0] <= zero:
                a, b = 0.0, 0.01
            return f"{stamp(a)} --> {stamp(b)}"
        txt = re.sub(r"(\d{2}):(\d{2}):(\d{2}),(\d{3}) --> (\d{2}):(\d{2}):(\d{2}),(\d{3})", sh2, txt)
    with open(out, "w", encoding="utf-8", newline="") as fh:
        fh.write(txt)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
