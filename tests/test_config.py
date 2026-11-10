"""Cấu hình: config.json, nhập từ .bat, bí mật không bao giờ vào file."""
from __future__ import annotations

import json

import pytest

from conftest import REFERENCE
from core import config as C
from core.secrets import SECRET_KEYS, SecretStore

BATS = [REFERENCE / "SubFetch_v30.bat", REFERENCE / "SubPrep_v20.bat", REFERENCE / "MuxTool_v17.bat"]
need_ref = pytest.mark.skipif(not all(p.exists() for p in BATS), reason="thiếu reference/*.bat")


def test_defaults_cover_every_field_and_no_secret():
    for k in SECRET_KEYS:
        assert k not in C.DEFAULTS
    assert C.DEFAULTS["MinVerifyVotes"] == 20
    assert C.DEFAULTS["Languages"] == ["vi", "en", "cs"]
    assert set(C.GROUPS) == {f.group for f in C.FIELDS}


def test_config_path_uses_appdata(monkeypatch, tmp_path):
    monkeypatch.delenv("MUXSTUDIO_CONFIG_DIR", raising=False)
    monkeypatch.setenv("APPDATA", str(tmp_path / "Roaming"))
    assert C.config_path() == tmp_path / "Roaming" / "MuxStudio" / "config.json"


def test_save_load_roundtrip_keeps_unknown_keys(config_dir):
    cfg = C.load_config()
    cfg["MinVerifyVotes"] = 25
    cfg["DonorOffsetMs"] = {"S01E03": 1200, "tap #643": -500}
    cfg["KhoaLa"] = "giữ nguyên"
    p = C.save_config(cfg)
    assert p == config_dir / "config.json"
    again = C.load_config()
    assert again["MinVerifyVotes"] == 25
    assert again["DonorOffsetMs"] == {"S01E03": 1200, "tap #643": -500}
    assert again["KhoaLa"] == "giữ nguyên"
    assert again["ScanSubFolders"] is True  # mac dinh
    data = json.loads(p.read_text(encoding="utf-8"))
    assert data["_version"] == C.CONFIG_VERSION


def test_secrets_never_written_or_read_from_json(config_dir):
    cfg = C.load_config()
    with pytest.raises(KeyError):
        cfg["TmdbApiKey"] = "abc"
    config_dir.mkdir(parents=True, exist_ok=True)
    (config_dir / "config.json").write_text(json.dumps({"OsPassword": "lo", "ApiPauseMs": 900}), encoding="utf-8", newline="")
    cfg = C.load_config()
    assert "OsPassword" not in cfg.to_json_dict()
    assert any("OsPassword" in w for w in cfg.warnings)
    assert cfg["ApiPauseMs"] == 900
    C.save_config(cfg)
    assert "OsPassword" not in (config_dir / "config.json").read_text(encoding="utf-8")


def test_bad_values_fall_back_to_default(config_dir):
    config_dir.mkdir(parents=True, exist_ok=True)
    (config_dir / "config.json").write_text(json.dumps({"MinVerifyVotes": "nhieu", "ExistingRenameMode": "xoa"}), encoding="utf-8", newline="")
    cfg = C.load_config()
    assert cfg["MinVerifyVotes"] == 20
    assert cfg["ExistingRenameMode"] == "copy"
    assert len(cfg.warnings) == 2


def test_broken_json_uses_defaults(config_dir):
    config_dir.mkdir(parents=True, exist_ok=True)
    (config_dir / "config.json").write_text("{hong", encoding="utf-8", newline="")
    cfg = C.load_config()
    assert cfg["MinVerifyVotes"] == 20 and cfg.warnings


def test_reset_defaults():
    cfg = C.Config({"MinVerifyVotes": 30, "X": 1})
    cfg.reset_defaults(["MinVerifyVotes"])
    assert cfg["MinVerifyVotes"] == 20 and cfg["X"] == 1
    cfg["MaxShowCandidates"] = 3
    cfg.reset_defaults()
    assert cfg["MaxShowCandidates"] == 12 and cfg["X"] == 1


@need_ref
def test_import_from_reference_bats():
    imp = C.import_from_bat(BATS)
    assert [k for _, k in imp.files] == ["subfetch", "subprep", "muxtool"]
    v = {k: it.value for k, it in imp.values.items()}
    assert not imp.conflicts and not imp.problems
    assert v["Languages"] == ["vi", "en", "cs"]
    assert v["MkvToolNixDir"] == r"C:\Users\fenha\Desktop\mkvtoolnix"
    assert v["AlassDir"] == r"C:\Users\fenha\Desktop\alass-windows64\bin\alass"
    assert v["VideoExts"] == [".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov"]  # hop SF + SP
    assert v["RefExcludeFolders"][-1] == "Output"  # mang viet tren 2 dong
    assert v["AlassArgsAudioMode"] == ["--no-split", "--disable-fps-guessing"]
    assert v["SubPrepLogDir"] == "_subprep_logs" and v["MuxLogDir"] == "_muxtool_logs"
    assert v["SubSourceApiBase"] == "https://api.subsource.net/api/v1"
    assert v["DonorOffsetMs"] == {} and v["DonorMaxDurationDiff"] == 1.0
    assert v["ExistingMinScore"] == 0.35 and v["MaxShiftSeconds"] == 130
    assert not imp.secrets  # ban tham chieu da xoa bi mat
    ignored = {n for _, n, _ in imp.ignored}
    assert {"UseColors", "DryRun", "UndoLastRun", "ConsoleCols"} <= ignored
    # moi khoa cau hinh (tru khoa moi) deu doc duoc tu .bat
    new_keys = {"MkvmergePath", "MkvextractPath", "FfmpegPath", "FfprobePath", "AlassPath", "SubFetchLogDir", "UiScale",
                "AskTrashSourcesAfterMux", "SkipIfVideoHasSub", "UiLanguage", "UiAppearance", "UiBackdrop"}
    assert set(C.FIELD_BY_KEY) - set(v) == new_keys


BAT_SNIPPET = r"""<# :
@echo off
powershell ...
exit /b
#>
param([string]$targetDir)
function Set-ConsoleSize([int]$cols) { $x = 1 }
$SubSourceApiKey = 'sk_fake_key_1234567890abcdef'   # comment
$TmdbApiKey      = "eyJfake.token.value"
$OsUsername      = 'nguoidung'
$OsPassword      = ''
$Lang_vi = $true      # Tieng Viet
$Lang_en = $false
$Lang_cs = $true
$Lang_ja = $true
$LangExtra = @('pt', 'RU')
$MinVerifyVotes  = 22
$RefExcludeFolders = @('srt_pool',
                       'Output')
$DonorOffsetMs = @{ 'S01E03' = 1200; 'tap #643' = -500 }
$ExistingMinScore = 0.4
$NoiseWords = @'
{ khong phai khoi lenh $MinVerifyVotes = 99
'@ -split '\s+'
$Languages = @()
foreach ($sw in @(1,2)) {
    $MinVerifyVotes = 77
}
$ApiPauseMs = 1000 + 100
"""


def test_import_snippet_parsing_and_secrets_to_keyring(tmp_path, memory_keyring):
    p = tmp_path / "[Judas] SubFetch_moi.bat"
    p.write_text(BAT_SNIPPET, encoding="utf-8", newline="")
    imp = C.import_from_bat([p])
    v = {k: it.value for k, it in imp.values.items()}
    assert v["Languages"] == ["vi", "cs", "ja", "pt", "ru"]
    assert v["MinVerifyVotes"] == 22  # khong lay 99 (here-string) hay 77 (trong khoi { })
    assert v["RefExcludeFolders"] == ["srt_pool", "Output"]
    assert v["DonorOffsetMs"] == {"S01E03": 1200, "tap #643": -500}
    assert v["ExistingMinScore"] == 0.4
    assert "ApiPauseMs" not in v and any(n == "ApiPauseMs" for _, n, _, _ in imp.problems)
    assert set(imp.secrets) == {"SubSourceApiKey", "TmdbApiKey", "OsUsername"}  # OsPassword rong -> bo qua
    assert imp.values["MinVerifyVotes"].line == 17

    store = SecretStore()
    store.set("OsPassword", "matkhau-cu")
    cfg = C.Config()
    C.apply_bat_import(cfg, imp, secret_store=store)
    assert cfg["MinVerifyVotes"] == 22
    assert store.get("SubSourceApiKey") == "sk_fake_key_1234567890abcdef"
    assert store.get("TmdbApiKey") == "eyJfake.token.value"
    assert store.get("OsPassword") == "matkhau-cu"  # bi mat rong KHONG xoa bi mat dang co
    dumped = json.dumps(cfg.to_json_dict())
    assert "sk_fake" not in dumped and "eyJfake" not in dumped and "nguoidung" not in dumped


def test_import_conflicts_need_resolution(tmp_path):
    a = tmp_path / "a.bat"
    b = tmp_path / "b.bat"
    a.write_text("<# :\n#>\n$AlassArgsAudioMode = @()\n$MkvToolNixDir = 'C:\\A'\n", encoding="utf-8", newline="")
    b.write_text("<# :\n#>\n$DonorFolder = 'x'\n$UserMkvToolNix = \"C:\\B\"\n", encoding="utf-8", newline="")
    imp = C.import_from_bat([a, b])
    assert "MkvToolNixDir" in imp.conflicts
    cfg = C.Config()
    C.apply_bat_import(cfg, imp)
    assert cfg["MkvToolNixDir"] == C.DEFAULTS["MkvToolNixDir"]  # chua chon -> khong ghi
    C.apply_bat_import(cfg, imp, resolutions={"MkvToolNixDir": "C:\\B"})
    assert cfg["MkvToolNixDir"] == "C:\\B"


def test_import_unknown_file(tmp_path):
    p = tmp_path / "khac.bat"
    p.write_text("@echo off\n", encoding="utf-8", newline="")
    imp = C.import_from_bat([p])
    assert imp.files[0][1] is None and imp.problems
