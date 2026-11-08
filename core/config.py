"""Cấu hình: ``%APPDATA%\\MuxStudio\\config.json`` + nhập từ file .bat.

* Khoá giữ đúng tên biến trong .bat (docs/SPEC.md mục 5) để nhập thẳng.
* Bí mật KHÔNG BAO GIỜ nằm trong file này (xem ``core.secrets``) — có lỡ xuất hiện trong
  ``config.json`` cũng bị bỏ khi đọc và không bao giờ được ghi.
* Khoá lạ trong ``config.json`` được giữ nguyên khi ghi lại.
"""
from __future__ import annotations

import copy
import json
import os
import re
import tempfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Iterator, MutableMapping

from . import naming
from .secrets import SECRET_KEYS

CONFIG_VERSION = 1

# ==========================================================================
#  BẢNG KHOÁ CẤU HÌNH
# ==========================================================================


@dataclass(frozen=True)
class Field:
    key: str
    default: Any
    group: str  # chung | api | fetch | prep | mux
    label: str
    help: str = ""
    kind: str = ""  # bool int float str dir file list dict choice langs (rong = theo kieu mac dinh)
    choices: tuple[str, ...] = ()
    advanced: bool = False


GROUPS: dict[str, str] = {
    "chung": "Chung — thư mục & công cụ",
    "api": "API & tài khoản",
    "fetch": "① Tải phụ đề",
    "prep": "② Chuẩn bị phụ đề",
    "mux": "③ Mux",
}

_D = "C:\\Users\\fenha\\Desktop\\"

FIELDS: tuple[Field, ...] = (
    # ---------------- Chung ----------------
    Field("MkvToolNixDir", _D + "mkvtoolnix", "chung", "Thư mục MKVToolNix", "Nơi có mkvmerge.exe, mkvextract.exe.", "dir"),
    Field("AlassDir", _D + "alass-windows64\\bin\\alass", "chung", "Thư mục alass", "Nơi có alass.exe (thường có cả ffmpeg.exe, ffprobe.exe).", "dir"),
    Field("ExtraToolDirs", [_D + "alass-windows64\\bin\\alass"], "chung", "Thư mục tìm thêm công cụ", "Nơi tìm thêm ffprobe/ffmpeg.", "list"),
    Field("MkvmergePath", "", "chung", "mkvmerge.exe", "Để trống = tự tìm.", "file"),
    Field("MkvextractPath", "", "chung", "mkvextract.exe", "Để trống = tự tìm.", "file"),
    Field("FfmpegPath", "", "chung", "ffmpeg.exe", "Để trống = tự tìm.", "file"),
    Field("FfprobePath", "", "chung", "ffprobe.exe", "Để trống = tự tìm.", "file"),
    Field("AlassPath", "", "chung", "alass.exe", "Để trống = tự tìm.", "file"),
    Field("ScanSubFolders", True, "chung", "Quét cả thư mục con", "Nhiều season một lượt."),
    Field("VideoExts", [".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov"], "chung", "Đuôi file phim", "", "list", advanced=True),
    Field("SubFolderName", "srt_can_sync", "chung", "Thư mục phụ đề cần sync", "Nơi hạ phụ đề tải về (bước ② xử lý tiếp)."),
    Field("ExtractFolder", "srt_goc", "chung", "Thư mục phụ đề gốc", "Phụ đề trích từ MKV làm mốc thời gian (không để trống)."),
    Field("PoolFolder", "srt_pool", "chung", "Thư mục kho phụ đề tự tải", "Nên chia thư mục con vi\\ cs\\ en\\."),
    Field("DonorFolder", "audio_nguon", "chung", "Thư mục file nguồn audio", "File phim khác để lấy audio ghép vào."),
    Field("MinVerifyVotes", 20, "chung", "Số mốc khớp tối thiểu", "Khớp ĐÚNG thường 25–35 phiếu; khớp SAI chỉ 5–15. Đặt 8 từng làm ghép nhầm tập. Tối thiểu nên là 20."),
    Field("FpsSampleRef", 400, "chung", "Số mốc lấy mẫu bên phụ đề gốc", "", advanced=True),
    Field("FpsSampleTgt", 100, "chung", "Số mốc lấy mẫu bên phụ đề cần đo", "", advanced=True),
    Field("PgsTimingFallback", True, "chung", "Đọc mốc từ phụ đề PGS", "MKV chỉ có phụ đề dạng ảnh (PGS .sup)? Đọc thẳng mốc thời gian, không cần OCR."),
    Field("PgsTimingText", "*", "chung", "Chữ giữ chỗ cho dòng PGS", "Chỉ là chỗ giữ chỗ, không ai đọc.", advanced=True),
    Field("KeepUndoRuns", 20, "chung", "Số lần chạy giữ để hoàn tác", "Áp dụng cả ba bước."),
    Field("DeleteToRecycleBin", True, "chung", "Xoá vào Thùng rác", "Khôi phục được, thay vì xoá hẳn."),
    Field("SubFetchLogDir", "_subfetch_logs", "chung", "Thư mục nhật ký bước ①", "", advanced=True),
    Field("SubPrepLogDir", "_subprep_logs", "chung", "Thư mục nhật ký bước ②", "", advanced=True),
    Field("MuxLogDir", "_muxtool_logs", "chung", "Thư mục nhật ký bước ③", "", advanced=True),
    # ---------------- API ----------------
    Field("OsUserAgent", "MyApp v1.0.0", "api", "User-Agent OpenSubtitles", "BẮT BUỘC dạng \"TênỨngDụng vX.Y.Z\". Tên riêng chỉ chạy khi đã đăng ký API Consumer trên opensubtitles.com (nếu không: lỗi 403 kong-user-agent-block)."),
    Field("SubSourceApiBase", "https://api.subsource.net/api/v1", "api", "Địa chỉ API SubSource", "", advanced=True),
    Field("ApiPauseMs", 1100, "api", "Nghỉ giữa các lượt gọi (ms)", "Trần SubSource 60 lượt/phút."),
    Field("TmdbRetry", 3, "api", "Số lần thử lại khi lỗi mạng", "Dùng cho TMDB và TVDB."),
    Field("TmdbLang", "en-US", "api", "Ngôn ngữ TMDB", "'vi-VN' nếu muốn lấy tên tiếng Việt."),
    Field("TvdbLang", "eng", "api", "Ngôn ngữ TVDB", "Mã 3 chữ: 'eng', 'vie', 'jpn'…"),
    Field("TvdbSeasonType", "default", "api", "Kiểu đánh số season TVDB", "'default' (theo phát sóng) | 'dvd' | 'official'.", "choice", ("default", "dvd", "official")),
    # ---------------- ① Tải phụ đề ----------------
    Field("Languages", ["vi", "en", "cs"], "fetch", "Ngôn ngữ cần tải", "Thứ tự ở đây cũng là thứ tự xử lý.", "langs"),
    Field("UseLangSubFolders", True, "fetch", "Tách thư mục con theo ngôn ngữ", "srt_can_sync\\vi\\, \\cs\\, \\en\\ (bước ② đọc tên thư mục để biết ngôn ngữ)."),
    Field("MaxShowCandidates", 12, "fetch", "Số dòng tối đa trong bảng chọn", ""),
    Field("VerifyTiming", True, "fetch", "Đối chiếu mốc thời gian", "Đo với phụ đề gốc trong MKV trước khi chấp nhận. Bước ① KHÔNG sửa thời gian — việc đó của bước ②."),
    Field("Interactive", True, "fetch", "Tự chọn trong bảng ứng viên", "Tắt = tự lấy bản đứng đầu bảng."),
    Field("RememberChoice", True, "fetch", "Nhớ lựa chọn cho các tập sau", "Đã chọn cho season + ngôn ngữ nào thì dùng lại cho các tập sau."),
    Field("AutoPickWhenSingle", True, "fetch", "Chỉ 1 ứng viên thì lấy luôn", ""),
    Field("UseExistingSubs", True, "fetch", "Dùng phụ đề bạn tự bỏ vào srt_can_sync", "Có file sẵn → đổi tên cho khớp phim và bỏ qua tải."),
    Field("ExistingMinScore", 0.35, "fetch", "Độ giống tên tối thiểu (file có sẵn)", "Chỉ dùng khi không đo được thời gian."),
    Field("ExistingRenameMode", "copy", "fetch", "Cách đưa file có sẵn về đúng tên", "'copy' = giữ nguyên file gốc (an toàn, khuyên dùng); 'move' = đổi tên hẳn file gốc.", "choice", ("copy", "move")),
    Field("SkipIfExists", True, "fetch", "Đã có phụ đề ngôn ngữ đó thì bỏ qua", ""),
    Field("KeepExtractedRef", True, "fetch", "Giữ phụ đề gốc đã trích", "Giữ trong srt_goc: lần sau khỏi trích lại, mở ra xem được."),
    Field("StrictFpsMatch", True, "fetch", "Loại bản khai báo FPS khác video", "Phụ đề PAL 25fps ghép vào phim 23.976 sẽ trôi dần tới ~5 phút ở cuối."),
    Field("AllowUnknownFps", True, "fetch", "Cho phép bản không khai báo FPS", "Sẽ đo bằng mốc thời gian sau khi tải."),
    Field("KeepPaidDownloads", True, "fetch", "Giữ file OpenSubtitles kiểm chứng yếu", "Đã tốn quota thì giữ kèm cảnh báo. Tắt = chuyển vào Thùng rác và thử bản kế tiếp."),
    Field("UsePool", True, "fetch", "Dùng kho phụ đề srt_pool", ""),
    Field("PoolTryLimit", 8, "fetch", "Số file đo thử mỗi ngôn ngữ (kho)", ""),
    Field("PoolMinVotes", 8, "fetch", "Ngưỡng loại hẳn (kho)", "Dưới mức này coi là phim khác → bỏ. Từ mức này tới 'Số mốc khớp tối thiểu' vẫn hiện kèm 'cần kiểm tra'."),
    Field("PoolFirst", True, "fetch", "Kho có bản khớp thì không hỏi mạng", "Tiết kiệm thời gian và quota."),
    Field("PoolDefaultLang", "", "fetch", "Ngôn ngữ mặc định cho file kho không rõ ngôn ngữ", "Để trống = bỏ qua file không rõ ngôn ngữ."),
    Field("ExcludeSdh", True, "fetch", "Không lấy bản SDH / Hearing Impaired", ""),
    Field("ExcludeForced", True, "fetch", "Không lấy bản Forced / foreignParts", ""),
    Field("MaxPages", 10, "fetch", "Số trang tối đa (SubSource)", "Một mục có thể có 150+ bản, đừng đặt quá thấp."),
    Field("UseOpenSubtitles", True, "fetch", "Dùng OpenSubtitles", "Nguồn dự phòng (tốn quota nên để sau cùng)."),
    Field("OsWarnRemaining", 50, "fetch", "Cảnh báo khi còn ≤ lượt tải", ""),
    Field("OsStopRemaining", 5, "fetch", "Ngừng tải khi còn ≤ lượt tải", ""),
    Field("OsExcludeMachine", True, "fetch", "Loại hẳn bản dịch máy / AI", "Tắt = vẫn cho phép nhưng xếp sau bản thường."),
    # ---------------- ② Chuẩn bị phụ đề ----------------
    Field("RefFolderName", "", "prep", "Thư mục file mốc", "Để trống = dùng chính thư mục phim."),
    Field("OutFolderName", "", "prep", "Thư mục ghi kết quả sync", "Để trống = ghi thẳng cạnh file phim (khuyên dùng). Hoặc 'srt_hoan_thanh'."),
    Field("RefExcludeFolders", ["srt_pool", "srt_can_sync", "srt_hoan_thanh", "_subprep_logs", "_subfetch_logs", "_subsource_probe", "Output"], "prep", "Thư mục loại khỏi việc tìm mốc", "Phụ đề trong srt_pool bị coi là 'mốc' sẽ đổi tên sai hàng loạt. Luôn loại thêm thư mục nguồn audio, kho và srt_can_sync.", "list"),
    Field("RefSubLangs", ["en", "vi", "cs"], "prep", "Thứ tự ngôn ngữ khi trích phụ đề gốc", "", "list"),
    Field("SkipExistingRef", True, "prep", "Đã trích rồi thì bỏ qua", ""),
    Field("SubExts", [".srt", ".ass", ".ssa", ".vtt"], "prep", "Đuôi file phụ đề", "", "list", advanced=True),
    Field("SkipExistingOut", True, "prep", "Bỏ qua file đã sync rồi", ""),
    Field("AlassExtraArgs", [], "prep", "Tham số thêm cho alass", "Vd: --split-penalty 7", "list", advanced=True),
    Field("SyncAudioMode", "ask", "prep", "Chọn audio track làm mốc", "'ask' = hiện bảng để chọn; 'auto' = audio Default của file; 'index' = theo số thứ tự; 'lang' = theo ngôn ngữ.", "choice", ("ask", "auto", "index", "lang")),
    Field("SyncAudioIndex", 0, "prep", "Số thứ tự audio (đếm từ 0)", ""),
    Field("SyncAudioLang", "en", "prep", "Ngôn ngữ audio làm mốc", ""),
    Field("MaxShiftSeconds", 130, "prep", "Ngưỡng dịch tối đa (giây)", "alass dịch quá ngưỡng này → coi là SAI. Chỉ TỪ CHỐI khi mốc là audio; mốc là phụ đề thì chỉ cảnh báo. 0 = tắt ngưỡng."),
    Field("AlassArgsSubMode", [], "prep", "Tham số alass khi mốc là phụ đề", "Để trống = mặc định của alass (được cắt đoạn và sửa framerate).", "list", advanced=True),
    Field("AlassArgsAudioMode", ["--no-split", "--disable-fps-guessing"], "prep", "Tham số alass khi mốc là audio", "VAD dễ đoán nhầm nhạc thành giọng nói nên siết lại.", "list", advanced=True),
    Field("NormalizeEncoding", True, "prep", "Chuyển phụ đề về UTF-8", "Làm trên bản sao, không sửa file nguồn."),
    Field("TitleMinScore", 0.5, "prep", "Điểm giống tên tối thiểu", "Để nhận là cùng phim."),
    Field("TitleMinMargin", 0.15, "prep", "Khoảng cách tối thiểu với ứng viên nhì", "Chống nhầm."),
    Field("TrimLeadingExtra", True, "prep", "Cắt đoạn recap đầu file", "Đoạn 'tóm tắt tập trước' không có trong phim."),
    Field("MaxZeroStartCues", 2, "prep", "Số dòng ở 00:00:00 tối đa", "Trên mức này coi là khối bị dồn về 00:00:00."),
    Field("HealthReport", True, "prep", "Xuất CSV báo cáo sức khoẻ", ""),
    Field("AnalyzeTiming", True, "prep", "Phân tích FPS / độ lệch trước khi sync", "Tắt nếu thấy chậm."),
    Field("FpsAutoAllowGuess", True, "prep", "Lệch FPS → cho alass tự sửa framerate", "Bỏ --disable-fps-guessing cho file đó."),
    Field("AskCleanup", True, "prep", "Hỏi dọn dẹp file nguồn sau khi xong", ""),
    # ---------------- ③ Mux ----------------
    Field("SkipOnMultipleEnAudio", True, "mux", "≥ 2 audio tiếng Anh: chờ bạn chọn", "Tắt = tự lấy track bitrate cao nhất làm Default."),
    Field("RemoveCommentaryAudio", True, "mux", "Xoá audio bình luận", "Commentary / director / cast…"),
    Field("RemoveDescriptiveAudio", True, "mux", "Xoá audio thuyết minh cho người khiếm thị", "Audio description."),
    Field("RemoveCommentarySubs", True, "mux", "Xoá phụ đề bình luận", ""),
    Field("RemoveImageSubs", True, "mux", "Xoá phụ đề dạng ảnh (PGS/VobSub)", "Không hiện được trên nhiều client iOS/trình duyệt. Phụ đề văn bản vẫn giữ."),
    Field("AskWhichAudio", True, "mux", "Hiện bảng chọn audio cần xoá", ""),
    Field("AskAudioMinTracks", 2, "mux", "Từ bao nhiêu audio thì hiện bảng", ""),
    Field("RememberAudioChoice", "ask", "mux", "Ghi nhớ lựa chọn audio", "'ask' = ô 'Áp dụng cho mọi file còn lại' bật sẵn; 'always' = luôn áp dụng; 'never' = mỗi file chọn lại.", "choice", ("ask", "always", "never")),
    Field("UseSubPicker", True, "mux", "Bảng chọn phụ đề theo tập", ""),
    Field("SubPickerMinFiles", 1, "mux", "Từ bao nhiêu file thì hiện bảng", ""),
    Field("AddCodecTag", True, "mux", "Gắn thẻ codec vào TÊN FILE", "Jellyfin dùng phần sau ' - ' làm nhãn phiên bản. Không bao giờ gắn vào tên thư mục."),
    Field("CodecTagForTv", False, "mux", "Gắn thẻ codec cho phim bộ", "Mặc định TẮT (Jellyfin xử lý kém ổn định hơn)."),
    Field("CodecTagParts", ["res", "hdr", "vcodec", "acodec"], "mux", "Thành phần thẻ codec", "", "list"),
    Field("WriteRunLog", True, "mux", "Ghi chi tiết vào nhật ký", ""),
    Field("SubKeepLangs", ["vi", "cs", "en"], "mux", "Ngôn ngữ phụ đề giữ lại", "", "list"),
    Field("SubDefaultPriority", ["vi", "en", "cs"], "mux", "Thứ tự ưu tiên phụ đề Default", "", "list"),
    Field("RemoveForcedSubs", True, "mux", "Xoá phụ đề Forced", ""),
    Field("SkipOnAmbiguousUnd", True, "mux", "≥ 2 audio có track 'und' → bỏ qua file", ""),
    Field("DefaultExternalLang", "vi", "mux", "Ngôn ngữ của sub ngoài không có hậu tố", ""),
    Field("ExternalSubOverridesInternal", True, "mux", "Sub ngoài thay sub trong cùng ngôn ngữ", ""),
    Field("SkipIfOutputExists", False, "mux", "Bỏ qua file đã mux rồi", ""),
    Field("UseTrackOrder", True, "mux", "Sắp xếp lại thứ tự track", ""),
    Field("SetTitleFromName", True, "mux", "Đặt title theo tên file", "Xoá title rác."),
    Field("SubExtPriority", [".srt", ".ass", ".ssa", ".sup", ".vtt"], "mux", "Ưu tiên đuôi sub ngoài", "", "list"),
    Field("TmdbConfirm", True, "mux", "Hỏi lại khi kết quả không chắc", "Áp dụng cả TMDB và TVDB: dòng hiện 'Cần chọn'."),
    Field("TmdbCreateFolder", True, "mux", "Mỗi phim lẻ một thư mục riêng", "Chuẩn Jellyfin/Emby."),
    Field("TvFetchEpisodeTitle", True, "mux", "Lấy tên tập", "Tra thêm 1 lượt mỗi tập."),
    Field("TvSeasonFolder", True, "mux", "Tạo thư mục Season 01\\", "Jellyfin rất nên bật."),
    Field("TvAbsoluteToSeason", True, "mux", "Đổi số tập tuyệt đối sang season/tập", "Vd One Piece S16E643 → mã season/tập của TMDB/TVDB."),
    Field("NameFallbackOther", False, "mux", "Không tìm thấy thì thử database còn lại", "Tắt (mặc định) = giữ tên gốc và báo, để thư viện không trộn tmdbid/tvdbid."),
    Field("UseDonorAudio", True, "mux", "Ghép audio từ file phim khác", ""),
    Field("DonorLangs", ["cs"], "mux", "Ngôn ngữ audio cần lấy", "", "list"),
    Field("DonorSkipIfPresent", True, "mux", "Phim đã có audio ngôn ngữ đó thì không lấy thêm", ""),
    Field("DonorSetDefault", False, "mux", "Đặt audio ghép làm Default", ""),
    Field("DonorMinVotes", 20, "mux", "Số mốc phụ đề khớp tối thiểu", "Để tin độ lệch đo được."),
    Field("DonorAcceptDurationMatch", True, "mux", "Không có phụ đề chung nhưng thời lượng khớp → vẫn ghép", "Độ lệch 0."),
    Field("DonorMaxDurationDiff", 1.0, "mux", "Chênh thời lượng tối đa (giây)", "Coi là 'khớp'."),
    Field("DonorOffsetMs", {}, "mux", "Độ lệch chỉnh tay (ms)", "Ghi đè kết quả đo. Khoá: tên file, 'S01E03', 'tap #643' hoặc '643'.", "dict"),
)

FIELD_BY_KEY: dict[str, Field] = {f.key: f for f in FIELDS}
DEFAULTS: dict[str, Any] = {f.key: f.default for f in FIELDS}


# ==========================================================================
#  ĐỌC / GHI config.json
# ==========================================================================


def app_config_dir() -> Path:
    """``%APPDATA%\\MuxStudio`` (biến ``MUXSTUDIO_CONFIG_DIR`` ghi đè, dùng cho test)."""
    env = os.environ.get("MUXSTUDIO_CONFIG_DIR")
    if env:
        return Path(env)
    appdata = os.environ.get("APPDATA")
    if appdata:
        return Path(appdata) / "MuxStudio"
    return Path.home() / ".config" / "MuxStudio"


def config_path() -> Path:
    return app_config_dir() / "config.json"


def coerce(key: str, value: Any) -> Any:
    """Ép ``value`` về đúng kiểu của khoá. Sai kiểu → ``ValueError``."""
    f = FIELD_BY_KEY.get(key)
    if f is None:
        return value
    d = f.default
    if isinstance(d, bool):
        if isinstance(value, bool):
            return value
        if isinstance(value, str) and value.strip().lower() in ("true", "false", "1", "0"):
            return value.strip().lower() in ("true", "1")
        raise ValueError(f"{key}: cần True/False")
    if isinstance(d, int):
        if isinstance(value, bool):
            raise ValueError(f"{key}: cần số nguyên")
        if isinstance(value, int):
            return value
        if isinstance(value, float) and value.is_integer():
            return int(value)
        if isinstance(value, str) and re.fullmatch(r"-?\d+", value.strip()):
            return int(value.strip())
        raise ValueError(f"{key}: cần số nguyên")
    if isinstance(d, float):
        if isinstance(value, bool):
            raise ValueError(f"{key}: cần số")
        if isinstance(value, (int, float)):
            return float(value)
        if isinstance(value, str):
            try:
                return float(value.strip().replace(",", "."))
            except ValueError:
                pass
        raise ValueError(f"{key}: cần số")
    if isinstance(d, str):
        if isinstance(value, (str, int, float)) and not isinstance(value, bool):
            v = str(value)
            if f.choices and v not in f.choices:
                raise ValueError(f"{key}: chỉ nhận {', '.join(f.choices)}")
            return v
        raise ValueError(f"{key}: cần chữ")
    if isinstance(d, list):
        if isinstance(value, str):
            return [value] if value else []
        if isinstance(value, (list, tuple)):
            return [str(x) for x in value]
        raise ValueError(f"{key}: cần danh sách")
    if isinstance(d, dict):
        if isinstance(value, dict):
            out: dict[str, float | int] = {}
            for k, v in value.items():
                if isinstance(v, bool) or not isinstance(v, (int, float)):
                    raise ValueError(f"{key}: giá trị '{k}' cần là số")
                out[str(k)] = v
            return out
        raise ValueError(f"{key}: cần bảng khoá → giá trị")
    return value


class Config(MutableMapping[str, Any]):
    """Cấu hình đang dùng. Khoá thiếu → giá trị mặc định. Không chứa bí mật."""

    def __init__(self, data: dict[str, Any] | None = None, path: Path | None = None):
        self.path = path
        self._data: dict[str, Any] = {}
        self.warnings: list[str] = []
        for k, v in (data or {}).items():
            self[k] = v

    # MutableMapping
    def __getitem__(self, key: str) -> Any:
        if key in self._data:
            return self._data[key]
        if key in DEFAULTS:
            return copy.deepcopy(DEFAULTS[key])
        raise KeyError(key)

    def __setitem__(self, key: str, value: Any) -> None:
        if key in SECRET_KEYS:
            raise KeyError(f"{key} là bí mật — lưu bằng core.secrets, không lưu trong config.json")
        self._data[key] = coerce(key, value)

    def __delitem__(self, key: str) -> None:
        del self._data[key]

    def __iter__(self) -> Iterator[str]:
        seen = set()
        for k in DEFAULTS:
            seen.add(k)
            yield k
        for k in self._data:
            if k not in seen:
                yield k

    def __len__(self) -> int:
        return len(set(DEFAULTS) | set(self._data))

    def __getattr__(self, key: str) -> Any:
        if key.startswith("_") or key in ("path", "warnings"):
            raise AttributeError(key)
        try:
            return self[key]
        except KeyError as e:
            raise AttributeError(key) from e

    def reset_defaults(self, keys: Iterable[str] | None = None) -> None:
        if keys is None:
            extra = {k: v for k, v in self._data.items() if k not in DEFAULTS}
            self._data = extra
        else:
            for k in keys:
                self._data.pop(k, None)

    def to_json_dict(self) -> dict[str, Any]:
        out: dict[str, Any] = {"_version": CONFIG_VERSION}
        for k in self:
            if k in SECRET_KEYS or k == "_version":
                continue
            out[k] = self[k]
        return out


def load_config(path: Path | None = None) -> Config:
    p = path or config_path()
    cfg = Config(path=p)
    if not p.exists():
        return cfg
    try:
        raw = json.loads(p.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as e:
        cfg.warnings.append(f"Không đọc được {p.name} ({e}) → dùng cấu hình mặc định.")
        return cfg
    if not isinstance(raw, dict):
        cfg.warnings.append(f"{p.name} không đúng định dạng → dùng cấu hình mặc định.")
        return cfg
    for k, v in raw.items():
        if k == "_version":
            continue
        if k in SECRET_KEYS:
            cfg.warnings.append(f"Bỏ '{k}' trong {p.name}: bí mật chỉ lưu trong Windows Credential Manager.")
            continue
        try:
            cfg[k] = v
        except ValueError as e:
            cfg.warnings.append(f"{e} → dùng giá trị mặc định.")
    for c in cfg["Languages"]:
        naming.register_language(c)
    return cfg


def save_config(cfg: Config, path: Path | None = None) -> Path:
    p = path or cfg.path or config_path()
    p.parent.mkdir(parents=True, exist_ok=True)
    data = json.dumps(cfg.to_json_dict(), ensure_ascii=False, indent=2)
    fd, tmp = tempfile.mkstemp(prefix=".config.", suffix=".tmp", dir=str(p.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(data)
        os.replace(tmp, p)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
    cfg.path = p
    return p


# ==========================================================================
#  NHẬP CẤU HÌNH TỪ FILE .bat
# ==========================================================================


class _Unparsed(Exception):
    pass


def detect_bat_kind(text: str) -> str | None:
    """Nhận diện file theo nội dung (không theo tên)."""
    if re.search(r"(?m)^\$SubSourceApiKey\s*=", text):
        return "subfetch"
    if re.search(r"(?m)^\$AlassArgsAudioMode\s*=", text):
        return "subprep"
    if re.search(r"(?m)^\$DonorFolder\s*=", text):
        return "muxtool"
    return None


def _ps_part(text: str) -> tuple[str, int]:
    """Phần PowerShell sau dòng ``#>`` của khối ``<# : ... #>`` đầu file; trả (văn bản, số dòng bù)."""
    i = text.find("#>")
    if text.lstrip().startswith("<#") and i >= 0:
        return text[i + 2:], text[: i + 2].count("\n")
    return text, 0


@dataclass
class Assignment:
    name: str
    value: Any
    line: int  # so dong trong file .bat (dem tu 1)
    raw: str  # doan van ban cua gia tri
    ok: bool


class _PsScanner:
    """Đọc các dòng gán ``$Ten = giá trị`` ở cấp ngoài cùng. KHÔNG thông dịch PowerShell."""

    NAME = re.compile(r"\$([A-Za-z_][A-Za-z0-9_]*(?::[A-Za-z_][A-Za-z0-9_]*)?)\s*=(?!=)")

    def __init__(self, text: str, line_offset: int, wanted: set[str]):
        self.s = text
        self.n = len(text)
        self.off = line_offset
        self.wanted = {w.lower() for w in wanted}
        self.out: list[Assignment] = []

    def line_of(self, pos: int) -> int:
        return self.s.count("\n", 0, pos) + 1 + self.off

    # ---- bo qua chuoi / comment ----
    def skip_single(self, i: int) -> int:  # i tai dau '
        i += 1
        while i < self.n:
            if self.s[i] == "'":
                if i + 1 < self.n and self.s[i + 1] == "'":
                    i += 2
                    continue
                return i + 1
            i += 1
        return i

    def skip_double(self, i: int) -> int:
        i += 1
        while i < self.n:
            c = self.s[i]
            if c == "`":
                i += 2
                continue
            if c == '"':
                if i + 1 < self.n and self.s[i + 1] == '"':
                    i += 2
                    continue
                return i + 1
            i += 1
        return i

    _HERE_START = re.compile(r"@['\"][ \t]*\r?\n")

    def is_here_start(self, i: int) -> bool:
        """``@'`` hoặc ``@"`` rồi xuống dòng = mở here-string."""
        return bool(self._HERE_START.match(self.s, i))

    def skip_here(self, i: int) -> int:  # i tai '@'
        q = self.s[i + 1]
        m = re.compile(r"(?m)^" + re.escape(q) + "@").search(self.s, i + 2)
        return m.end() if m else self.n

    def skip_line_comment(self, i: int) -> int:
        j = self.s.find("\n", i)
        return self.n if j < 0 else j

    def skip_block_comment(self, i: int) -> int:
        j = self.s.find("#>", i + 2)
        return self.n if j < 0 else j + 2

    def skip_ws(self, i: int, newlines: bool) -> int:
        while i < self.n:
            c = self.s[i]
            if c in " \t" or (newlines and c in "\r\n"):
                i += 1
            elif c == "#" and newlines:
                i = self.skip_line_comment(i)
            elif c == "<" and self.s.startswith("<#", i) and newlines:
                i = self.skip_block_comment(i)
            else:
                break
        return i

    # ---- doc gia tri ----
    def parse_scalar(self, i: int) -> tuple[Any, int]:
        s = self.s
        if i >= self.n:
            raise _Unparsed
        c = s[i]
        if c == "'":
            j = self.skip_single(i)
            return s[i + 1: j - 1].replace("''", "'"), j
        if c == '"':
            j = self.skip_double(i)
            body = s[i + 1: j - 1]
            if re.search(r"(?<!`)\$[A-Za-z_({]", body):
                raise _Unparsed  # chuoi co bien -> khong tu tinh
            body = body.replace('""', '"')
            body = re.sub(r"`(.)", lambda m: {"t": "\t", "n": "\n", "r": "\r", "0": "\0"}.get(m.group(1), m.group(1)), body)
            return body, j
        m = re.compile(r"\$(true|false|null)\b", re.I).match(s, i)
        if m:
            w = m.group(1).lower()
            return (True if w == "true" else False if w == "false" else None), m.end()
        m = re.compile(r"-?\d+(?:\.\d+)?(?![\w.])").match(s, i)
        if m:
            t = m.group(0)
            return (float(t) if "." in t else int(t)), m.end()
        if s.startswith("@(", i):
            return self.parse_array(i + 2)
        if s.startswith("@{", i):
            return self.parse_hash(i + 2)
        raise _Unparsed

    def parse_array(self, i: int) -> tuple[list[Any], int]:
        items: list[Any] = []
        while True:
            i = self.skip_ws(i, True)
            if i >= self.n:
                raise _Unparsed
            if self.s[i] == ")":
                return items, i + 1
            if self.s[i] == ",":
                i += 1
                continue
            v, i = self.parse_scalar(i)
            items.append(v)

    def parse_hash(self, i: int) -> tuple[dict[str, Any], int]:
        out: dict[str, Any] = {}
        key_re = re.compile(r"[A-Za-z0-9_]+")
        while True:
            i = self.skip_ws(i, True)
            if i >= self.n:
                raise _Unparsed
            c = self.s[i]
            if c == "}":
                return out, i + 1
            if c == ";":
                i += 1
                continue
            if c in "'\"":
                k, i = self.parse_scalar(i)
            else:
                m = key_re.match(self.s, i)
                if not m:
                    raise _Unparsed
                k, i = m.group(0), m.end()
            i = self.skip_ws(i, False)
            if i >= self.n or self.s[i] != "=":
                raise _Unparsed
            i = self.skip_ws(i + 1, False)
            v, i = self.parse_scalar(i)
            out[str(k)] = v

    def parse_value(self, i: int) -> tuple[Any, int]:
        i = self.skip_ws(i, False)
        v, j = self.parse_scalar(i)
        k = self.skip_ws(j, False)
        if k < self.n and self.s[k] not in "#\r\n;":
            raise _Unparsed  # bieu thuc phuc tap: 'a' + 'b', Join-Path ...
        return v, j

    # ---- quet ----
    def run(self) -> list[Assignment]:
        s = self.s
        i = 0
        depth = 0
        at_stmt_start = True
        while i < self.n:
            c = s[i]
            if c == "\n":
                at_stmt_start = True
                i += 1
                continue
            if c in " \t\r":
                i += 1
                continue
            if c == "#":
                i = self.skip_line_comment(i)
                continue
            if c == "<" and s.startswith("<#", i):
                i = self.skip_block_comment(i)
                continue
            if c == "@" and self.is_here_start(i):
                i = self.skip_here(i)
                at_stmt_start = False
                continue
            if c == "'":
                i = self.skip_single(i)
                at_stmt_start = False
                continue
            if c == '"':
                i = self.skip_double(i)
                at_stmt_start = False
                continue
            if c == "{":
                depth += 1
                i += 1
                at_stmt_start = True
                continue
            if c == "}":
                depth = max(0, depth - 1)
                i += 1
                at_stmt_start = False
                continue
            if c == ";":
                at_stmt_start = True
                i += 1
                continue
            if c == "$" and depth == 0 and at_stmt_start:
                m = self.NAME.match(s, i)
                if m and m.group(1).lower() in self.wanted:
                    start = m.end()
                    try:
                        v, j = self.parse_value(start)
                        self.out.append(Assignment(m.group(1), v, self.line_of(i), s[start:j].strip(), True))
                        i = j
                        at_stmt_start = False
                        continue
                    except _Unparsed:
                        eol = s.find("\n", start)
                        raw = s[start: self.n if eol < 0 else eol].strip()
                        self.out.append(Assignment(m.group(1), None, self.line_of(i), raw, False))
            at_stmt_start = False
            i += 1
        return self.out


# Ten bien trong .bat -> khoa cau hinh (khi khac ten)
_ALIASES = {"UserMkvToolNix": "MkvToolNixDir", "ApiBase": "SubSourceApiBase"}
_LOGDIR_BY_KIND = {"subprep": "SubPrepLogDir", "muxtool": "MuxLogDir"}
#: Bien thuoc console / da thay bang nut tren GUI -> bo qua, co bao
IGNORED_VARS: dict[str, str] = {
    **{k: "phần console — GUI thay hết" for k in (
        "TableWidth", "TableMinWidth", "ConsoleCols", "ConsoleRows", "ConsoleScrollback", "UseColors", "UseGridView")},
    "UndoLastRun": "thay bằng nút \"Hoàn tác…\"",
    "DryRun": "thay bằng nút \"Xem lệnh\"",
    "PreviewFirst": "GUI luôn hiện bảng xem trước",
    "KnownLangTags": "thay bằng bảng ngôn ngữ chung",
}
#: Khoa dang danh sach: nhieu file khac nhau -> gop (hop) thay vi bao xung dot
_UNION_KEYS = {"VideoExts"}
_LANG_SWITCH = re.compile(r"^Lang_([a-z]{2,3})$", re.I)


@dataclass
class ImportItem:
    key: str
    value: Any
    source: str  # ten file .bat
    line: int


@dataclass
class BatImport:
    files: list[tuple[str, str | None]] = field(default_factory=list)  # (duong dan, loai)
    values: dict[str, ImportItem] = field(default_factory=dict)
    conflicts: dict[str, list[ImportItem]] = field(default_factory=dict)
    secrets: dict[str, ImportItem] = field(default_factory=dict)
    secret_conflicts: dict[str, list[ImportItem]] = field(default_factory=dict)
    ignored: list[tuple[str, str, str]] = field(default_factory=list)  # (file, bien, ly do)
    problems: list[tuple[str, str, int, str]] = field(default_factory=list)  # (file, bien, dong, ly do)


#: Khoa cau hinh KHONG doc truc tiep tu .bat (trong .bat la bien tinh ra, vd $Languages = @(...))
_DERIVED_KEYS = {"Languages"}


def _wanted_names() -> set[str]:
    names = (set(FIELD_BY_KEY) - _DERIVED_KEYS) | set(SECRET_KEYS) | set(_ALIASES) | set(IGNORED_VARS)
    names |= {"LogDirName", "LangExtra"} | {f"Lang_{c}" for c in naming.FETCH_LANG_SWITCHES}
    return names


def parse_bat_text(text: str) -> list[Assignment]:
    ps, off = _ps_part(text)
    wanted = _wanted_names()
    # Lang_xx cua ngon ngu ngoai danh sach co san cung nhan
    wanted |= {m.group(1) for m in re.finditer(r"(?m)^\$(Lang_[A-Za-z]{2,3})\s*=", ps)}
    return _PsScanner(ps, off, wanted).run()


def _add(imp: BatImport, item: ImportItem, secret: bool) -> None:
    vals = imp.secrets if secret else imp.values
    confl = imp.secret_conflicts if secret else imp.conflicts
    if item.key in confl:
        if all(x.value != item.value for x in confl[item.key]):
            confl[item.key].append(item)
        return
    prev = vals.get(item.key)
    if prev is None:
        vals[item.key] = item
        return
    if prev.value == item.value:
        return
    if not secret and item.key in _UNION_KEYS and isinstance(prev.value, list):
        merged = list(prev.value) + [x for x in item.value if x not in prev.value]
        vals[item.key] = ImportItem(item.key, merged, f"{prev.source} + {item.source}", prev.line)
        return
    del vals[item.key]
    confl[item.key] = [prev, item]


def import_from_bat(paths: Iterable[str | os.PathLike[str]]) -> BatImport:
    """Đọc cấu hình ở đầu các file .bat. Không ghi gì — xem kết quả rồi gọi ``apply_bat_import``."""
    imp = BatImport()
    for p in paths:
        p = Path(p)
        try:
            text = p.read_bytes().decode("utf-8", errors="replace")
        except OSError as e:
            imp.problems.append((p.name, "", 0, f"không đọc được file: {e}"))
            continue
        kind = detect_bat_kind(text)
        imp.files.append((str(p), kind))
        if kind is None:
            imp.problems.append((p.name, "", 0, "không nhận ra là SubFetch / SubPrep / MuxTool"))
            continue
        lang_sw: dict[str, bool] = {}
        lang_extra: list[str] = []
        lang_line = 0
        for a in parse_bat_text(text):
            name = a.name
            if name in IGNORED_VARS:
                imp.ignored.append((p.name, name, IGNORED_VARS[name]))
                continue
            if not a.ok:
                imp.problems.append((p.name, name, a.line, f"giá trị không đọc được: {a.raw[:60]}"))
                continue
            m = _LANG_SWITCH.match(name)
            if m:
                if isinstance(a.value, bool):
                    lang_sw[m.group(1).lower()] = a.value
                    lang_line = lang_line or a.line
                else:
                    imp.problems.append((p.name, name, a.line, "cần $true/$false"))
                continue
            if name == "LangExtra":
                vals = a.value if isinstance(a.value, list) else [a.value]
                lang_extra = [str(x).strip().lower() for x in vals if str(x).strip()]
                continue
            if name in SECRET_KEYS:
                if isinstance(a.value, str) and a.value.strip():
                    _add(imp, ImportItem(name, a.value.strip(), p.name, a.line), secret=True)
                continue
            if name == "LogDirName":
                key = _LOGDIR_BY_KIND.get(kind or "")
                if not key:
                    continue
            else:
                key = _ALIASES.get(name, name)
            if key not in FIELD_BY_KEY:
                continue
            try:
                v = coerce(key, a.value)
            except ValueError as e:
                imp.problems.append((p.name, name, a.line, str(e)))
                continue
            _add(imp, ImportItem(key, v, p.name, a.line), secret=False)
        if lang_sw:
            order = [c for c in naming.FETCH_LANG_SWITCHES if lang_sw.get(c)]
            order += [c for c in lang_sw if lang_sw[c] and c not in order]
            order += [c for c in lang_extra if c not in order]
            _add(imp, ImportItem("Languages", order, p.name, lang_line), secret=False)
    return imp


def apply_bat_import(
    cfg: Config,
    imp: BatImport,
    resolutions: dict[str, Any] | None = None,
    secret_store: Any = None,
) -> list[str]:
    """Ghi kết quả nhập vào ``cfg`` (và bí mật vào ``secret_store``). Khoá xung đột chỉ ghi khi có
    trong ``resolutions``. Bí mật rỗng KHÔNG xoá bí mật đang có. Trả danh sách khoá đã ghi."""
    resolutions = resolutions or {}
    done: list[str] = []
    for k, it in imp.values.items():
        cfg[k] = it.value
        done.append(k)
    for k, v in resolutions.items():
        if k in imp.conflicts:
            cfg[k] = v
            done.append(k)
    if secret_store is not None:
        for k, it in imp.secrets.items():
            secret_store.set(k, it.value)
            done.append(k)
        for k, v in resolutions.items():
            if k in imp.secret_conflicts and v:
                secret_store.set(k, v)
                done.append(k)
    for c in cfg["Languages"]:
        naming.register_language(c)
    return done
