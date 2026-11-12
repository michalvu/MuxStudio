using System.Collections;
using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>
/// Thư mục cấu hình tạm (fixture <c>config_dir</c>): đặt <c>MUXSTUDIO_CONFIG_DIR</c> = <c>tmp/[cfg] MuxStudio</c>, trả lại khi xong.
/// Đồng thời đưa bảng ngôn ngữ về mặc định (load_config đăng ký ngôn ngữ lạ).
/// </summary>
public sealed class ConfigDirScope : IDisposable
{
    private readonly string? _oldEnv = Environment.GetEnvironmentVariable("MUXSTUDIO_CONFIG_DIR");

    public TempDir Tmp { get; } = new();

    public string Dir { get; }

    public ConfigDirScope()
    {
        Dir = Tmp["[cfg] MuxStudio"];
        Environment.SetEnvironmentVariable("MUXSTUDIO_CONFIG_DIR", Dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MUXSTUDIO_CONFIG_DIR", _oldEnv);
        Naming.ResetLanguages();
        Tmp.Dispose();
    }
}

/// <summary>Đường dẫn trong repo (thư mục <c>reference/</c> của bản Python).</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Reference => Path.Combine(Root, "reference");

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !(Directory.Exists(Path.Combine(d.FullName, "core")) && Directory.Exists(Path.Combine(d.FullName, "dotnet"))))
            d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("không tìm thấy gốc repo");
    }

    public static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);
}

/// <summary>Cấu hình: config.json, nhập từ .bat, bí mật không bao giờ vào file. Port <c>tests/test_config.py</c>.</summary>
[Collection(GlobalStateCollection.Name)]
public class ConfigTests
{
    private static readonly string[] Bats =
        ["SubFetch_v30.bat", "SubPrep_v20.bat", "MuxTool_v17.bat"];

    private static string[] BatPaths => Bats.Select(b => Path.Combine(RepoPaths.Reference, b)).ToArray();

    private static OrderedDictionary<string, object?> D(params (string K, object? V)[] kv)
    {
        var d = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in kv)
            d[k] = v;
        return d;
    }

    [Fact]
    public void Defaults_cover_every_field_and_no_secret()
    {
        foreach (var k in Secrets.SecretKeys)
            Assert.False(ConfigSchema.Defaults.ContainsKey(k));
        Assert.Equal(20L, ConfigSchema.Defaults["MinVerifyVotes"]);
        Assert.Equal(["vi", "en", "cs"], (List<string>)ConfigSchema.Defaults["Languages"]!);
        Assert.Equal(ConfigSchema.Groups.Select(g => g.Key).ToHashSet(), ConfigSchema.Fields.Select(f => f.Group).ToHashSet());
    }

    [Fact]
    public void Config_path_uses_appdata()
    {
        using var scope = new ConfigDirScope();
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        try
        {
            Environment.SetEnvironmentVariable("MUXSTUDIO_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("APPDATA", scope.Tmp["Roaming"]);
            Assert.Equal(scope.Tmp["Roaming", "MuxStudio", "config.json"], ConfigSchema.ConfigPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
        }
    }

    [Fact]
    public void Save_load_roundtrip_keeps_unknown_keys()
    {
        using var scope = new ConfigDirScope();
        var cfg = ConfigSchema.LoadConfig();
        cfg["MinVerifyVotes"] = 25L;
        cfg["DonorOffsetMs"] = D(("S01E03", 1200L), ("tap #643", -500L));
        cfg["KhoaLa"] = "giữ nguyên";
        var p = ConfigSchema.SaveConfig(cfg);
        Assert.Equal(Path.Combine(scope.Dir, "config.json"), p);
        var again = ConfigSchema.LoadConfig();
        Assert.Equal(25L, again["MinVerifyVotes"]);
        Assert.True(Py.Equal(D(("S01E03", 1200L), ("tap #643", -500L)), again["DonorOffsetMs"]));
        Assert.Equal("giữ nguyên", again["KhoaLa"]);
        Assert.Equal(true, again["ScanSubFolders"]); // mặc định
        var data = (OrderedDictionary<string, object?>)PyJson.Loads(File.ReadAllText(p, Py.Utf8Strict))!;
        Assert.Equal((long)ConfigSchema.ConfigVersion, data["_version"]);
    }

    [Fact]
    public void Secrets_never_written_or_read_from_json()
    {
        using var scope = new ConfigDirScope();
        var cfg = ConfigSchema.LoadConfig();
        Assert.Throws<KeyNotFoundException>(() => cfg["TmdbApiKey"] = "abc");
        Directory.CreateDirectory(scope.Dir);
        File.WriteAllText(Path.Combine(scope.Dir, "config.json"),
            PyJson.Dumps(D(("OsPassword", "lo"), ("ApiPauseMs", 900L))), Py.Utf8NoBom);
        cfg = ConfigSchema.LoadConfig();
        Assert.False(cfg.ToJsonDict().ContainsKey("OsPassword"));
        Assert.Contains(cfg.Warnings, w => w.Contains("OsPassword", StringComparison.Ordinal));
        Assert.Equal(900L, cfg["ApiPauseMs"]);
        ConfigSchema.SaveConfig(cfg);
        Assert.DoesNotContain("OsPassword", File.ReadAllText(Path.Combine(scope.Dir, "config.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_values_fall_back_to_default()
    {
        using var scope = new ConfigDirScope();
        Directory.CreateDirectory(scope.Dir);
        File.WriteAllText(Path.Combine(scope.Dir, "config.json"),
            PyJson.Dumps(D(("MinVerifyVotes", "nhieu"), ("ExistingRenameMode", "xoa"))), Py.Utf8NoBom);
        var cfg = ConfigSchema.LoadConfig();
        Assert.Equal(20L, cfg["MinVerifyVotes"]);
        Assert.Equal("copy", cfg["ExistingRenameMode"]);
        Assert.Equal(2, cfg.Warnings.Count);
    }

    [Fact]
    public void Broken_json_uses_defaults()
    {
        using var scope = new ConfigDirScope();
        Directory.CreateDirectory(scope.Dir);
        File.WriteAllText(Path.Combine(scope.Dir, "config.json"), "{hong", Py.Utf8NoBom);
        var cfg = ConfigSchema.LoadConfig();
        Assert.Equal(20L, cfg["MinVerifyVotes"]);
        Assert.NotEmpty(cfg.Warnings);
    }

    [Fact]
    public void Reset_defaults()
    {
        var cfg = new Config(new Dictionary<string, object?> { ["MinVerifyVotes"] = 30L, ["X"] = 1L });
        cfg.ResetDefaults(["MinVerifyVotes"]);
        Assert.Equal(20L, cfg["MinVerifyVotes"]);
        Assert.Equal(1L, cfg["X"]);
        cfg["MaxShowCandidates"] = 3L;
        cfg.ResetDefaults();
        Assert.Equal(12L, cfg["MaxShowCandidates"]);
        Assert.Equal(1L, cfg["X"]);
    }

    [Fact]
    public void Import_from_reference_bats()
    {
        Assert.All(BatPaths, p => Assert.True(File.Exists(p), $"thiếu {p}"));
        var imp = BatImporter.ImportFromBat(BatPaths);
        Assert.Equal(["subfetch", "subprep", "muxtool"], imp.Files.Select(f => f.Kind));
        var v = imp.Values.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        Assert.Empty(imp.Conflicts);
        Assert.Empty(imp.Problems);
        Assert.Equal(["vi", "en", "cs"], (IEnumerable<string>)v["Languages"]!);
        Assert.Equal(@"C:\Users\fenha\Desktop\mkvtoolnix", v["MkvToolNixDir"]);
        Assert.Equal(@"C:\Users\fenha\Desktop\alass-windows64\bin\alass", v["AlassDir"]);
        Assert.Equal([".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov"], (IEnumerable<string>)v["VideoExts"]!); // hợp SF + SP
        Assert.Equal("Output", ((List<string>)v["RefExcludeFolders"]!)[^1]); // mảng viết trên 2 dòng
        Assert.Equal(["--no-split", "--disable-fps-guessing"], (IEnumerable<string>)v["AlassArgsAudioMode"]!);
        Assert.Equal("_subprep_logs", v["SubPrepLogDir"]);
        Assert.Equal("_muxtool_logs", v["MuxLogDir"]);
        Assert.Equal("https://api.subsource.net/api/v1", v["SubSourceApiBase"]);
        Assert.Empty((IDictionary)v["DonorOffsetMs"]!);
        Assert.Equal(1.0, v["DonorMaxDurationDiff"]);
        Assert.Equal(0.35, v["ExistingMinScore"]);
        Assert.Equal(130L, v["MaxShiftSeconds"]);
        Assert.Empty(imp.SecretValues); // bản tham chiếu đã xoá bí mật
        var ignored = imp.Ignored.Select(x => x.Name).ToHashSet();
        Assert.Subset(ignored, new HashSet<string> { "UseColors", "DryRun", "UndoLastRun", "ConsoleCols" });
        // mọi khoá cấu hình (trừ khoá mới) đều đọc được từ .bat
        var newKeys = new HashSet<string>
        {
            "MkvmergePath", "MkvextractPath", "FfmpegPath", "FfprobePath", "AlassPath", "SubFetchLogDir", "UiScale",
            "AskTrashSourcesAfterMux", "SkipIfVideoHasSub", "UiLanguage", "UiAppearance", "UiBackdrop", "UseTitulky", "TitulkyProbeTop",
        };
        Assert.Equal(newKeys, ConfigSchema.FieldByKey.Keys.Except(v.Keys).ToHashSet());
    }

    private const string BatSnippet = """
        <# :
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

        """;

    [Fact]
    public void Import_snippet_parsing_and_secrets_to_keyring()
    {
        using var tmp = new TempDir();
        var p = tmp["[Judas] SubFetch_moi.bat"];
        File.WriteAllText(p, BatSnippet.Replace("\r\n", "\n", StringComparison.Ordinal), Py.Utf8NoBom);
        var imp = BatImporter.ImportFromBat([p]);
        var v = imp.Values.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        Assert.Equal(["vi", "cs", "ja", "pt", "ru"], (IEnumerable<string>)v["Languages"]!);
        Assert.Equal(22L, v["MinVerifyVotes"]); // không lấy 99 (here-string) hay 77 (trong khối { })
        Assert.Equal(["srt_pool", "Output"], (IEnumerable<string>)v["RefExcludeFolders"]!);
        Assert.True(Py.Equal(D(("S01E03", 1200L), ("tap #643", -500L)), v["DonorOffsetMs"]));
        Assert.Equal(0.4, v["ExistingMinScore"]);
        Assert.False(v.ContainsKey("ApiPauseMs"));
        Assert.Contains(imp.Problems, x => x.Name == "ApiPauseMs");
        Assert.Equal(new HashSet<string> { "SubSourceApiKey", "TmdbApiKey", "OsUsername" }, imp.SecretValues.Keys.ToHashSet()); // OsPassword rỗng -> bỏ qua
        Assert.Equal(17, imp.Values["MinVerifyVotes"].Line);

        var store = new SecretStore(backend: new MemorySecretBackend());
        store.Set("OsPassword", "matkhau-cu");
        var cfg = new Config();
        try
        {
            BatImporter.ApplyBatImport(cfg, imp, secretStore: store);
        }
        finally
        {
            Naming.ResetLanguages();
        }
        Assert.Equal(22L, cfg["MinVerifyVotes"]);
        Assert.Equal("sk_fake_key_1234567890abcdef", store.Get("SubSourceApiKey"));
        Assert.Equal("eyJfake.token.value", store.Get("TmdbApiKey"));
        Assert.Equal("matkhau-cu", store.Get("OsPassword")); // bí mật rỗng KHÔNG xoá bí mật đang có
        var dumped = PyJson.Dumps(cfg.ToJsonDict());
        Assert.DoesNotContain("sk_fake", dumped, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJfake", dumped, StringComparison.Ordinal);
        Assert.DoesNotContain("nguoidung", dumped, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_conflicts_need_resolution()
    {
        using var tmp = new TempDir();
        var a = tmp["a.bat"];
        var b = tmp["b.bat"];
        File.WriteAllText(a, "<# :\n#>\n$AlassArgsAudioMode = @()\n$MkvToolNixDir = 'C:\\A'\n", Py.Utf8NoBom);
        File.WriteAllText(b, "<# :\n#>\n$DonorFolder = 'x'\n$UserMkvToolNix = \"C:\\B\"\n", Py.Utf8NoBom);
        var imp = BatImporter.ImportFromBat([a, b]);
        Assert.True(imp.Conflicts.ContainsKey("MkvToolNixDir"));
        var cfg = new Config();
        BatImporter.ApplyBatImport(cfg, imp);
        Assert.Equal(ConfigSchema.Defaults["MkvToolNixDir"], cfg["MkvToolNixDir"]); // chưa chọn -> không ghi
        BatImporter.ApplyBatImport(cfg, imp, resolutions: new Dictionary<string, object?> { ["MkvToolNixDir"] = "C:\\B" });
        Assert.Equal("C:\\B", cfg["MkvToolNixDir"]);
        Naming.ResetLanguages();
    }

    [Fact]
    public void Import_unknown_file()
    {
        using var tmp = new TempDir();
        var p = tmp["khac.bat"];
        File.WriteAllText(p, "@echo off\n", Py.Utf8NoBom);
        var imp = BatImporter.ImportFromBat([p]);
        Assert.Null(imp.Files[0].Kind);
        Assert.NotEmpty(imp.Problems);
    }

    // ================= thêm cho bản C#: đối chiếu với dữ liệu xuất từ Python =================

    private static OrderedDictionary<string, object?> PyData() =>
        (OrderedDictionary<string, object?>)PyJson.Loads(File.ReadAllText(RepoPaths.Data("python_config.json"), Py.Utf8Strict))!;

    [Fact]
    public void Field_table_matches_python()
    {
        var fields = (List<object?>)PyData()["fields"]!;
        Assert.Equal(fields.Count, ConfigSchema.Fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            var pf = (OrderedDictionary<string, object?>)fields[i]!;
            var cf = ConfigSchema.Fields[i];
            Assert.Equal(pf["key"], cf.Key);
            Assert.True(Py.Equal(pf["default"], cf.Default), cf.Key);
            var expectedType = pf["type"] switch
            {
                "bool" => typeof(bool),
                "int" => typeof(long),
                "float" => typeof(double),
                "str" => typeof(string),
                "list" => typeof(List<string>),
                _ => typeof(OrderedDictionary<string, object?>),
            };
            Assert.Equal(expectedType, cf.Default.GetType());
            Assert.Equal(pf["group"], cf.Group);
            Assert.Equal(pf["label"], cf.Label);
            Assert.Equal(pf["help"], cf.Help);
            Assert.Equal(pf["kind"], cf.Kind);
            Assert.True(Py.Equal(pf["choices"], cf.ChoiceList.ToList()), cf.Key);
            Assert.Equal(pf["advanced"], cf.Advanced);
        }
    }

    [Fact]
    public void Default_config_json_is_byte_identical_to_python()
    {
        var expected = (string)PyData()["default_config_json"]!;
        Assert.Equal(expected, PyJson.Dumps(new Config().ToJsonDict(), indent: 2, ensureAscii: false));
    }

    [Fact]
    public void Saved_file_is_byte_identical_and_lf_without_bom()
    {
        using var scope = new ConfigDirScope();
        var p = ConfigSchema.SaveConfig(new Config());
        var raw = File.ReadAllBytes(p);
        Assert.False(raw.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.DoesNotContain((byte)'\r', raw);
        Assert.Equal((string)PyData()["default_config_json"]!, Py.Utf8Strict.GetString(raw));
    }

    [Fact]
    public void Reference_bat_import_matches_python_completely()
    {
        var r = (OrderedDictionary<string, object?>)PyData()["bat_import_reference"]!;
        var imp = BatImporter.ImportFromBat(BatPaths);
        Assert.True(Py.Equal(r["kinds"], imp.Files.Select(f => (object?)f.Kind).ToList()));
        var pv = (OrderedDictionary<string, object?>)r["values"]!;
        Assert.Equal(pv.Keys.ToList(), imp.Values.Keys.ToList()); // cùng khoá, cùng thứ tự
        foreach (var (k, o) in pv)
        {
            var e = (OrderedDictionary<string, object?>)o!;
            var it = imp.Values[k];
            Assert.True(Py.Equal(e["value"], it.Value), $"{k}: Python {PyJson.Dumps(e["value"])} / C# {PyJson.Dumps(it.Value)}");
            Assert.Equal(e["source"], it.Source);
            Assert.Equal(e["line"], (long)it.Line);
        }
        Assert.Empty((IDictionary)r["conflicts"]!);
        Assert.Empty(imp.Conflicts);
        Assert.True(Py.Equal(r["secrets"], imp.SecretValues.Keys.Order(StringComparer.Ordinal).Cast<object?>().ToList()));
        Assert.True(Py.Equal(r["ignored"], imp.Ignored.Select(x => (object?)new List<object?> { x.File, x.Name, x.Reason }).ToList()));
        Assert.True(Py.Equal(r["problems"], imp.Problems.Select(x => (object?)new List<object?> { x.File, x.Name, (long)x.Line, x.Reason }).ToList()));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.35, "0.35")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(123456.789, "123456.789")]
    [InlineData(1.5e-300, "1.5e-300")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    public void Float_repr_matches_python(double x, string s) => Assert.Equal(s, Py.FloatRepr(x));

    [Fact]
    public void Coerce_rules()
    {
        Assert.Equal(true, ConfigSchema.Coerce("ScanSubFolders", " TRUE "));
        Assert.Equal(false, ConfigSchema.Coerce("ScanSubFolders", "0"));
        Assert.Throws<ConfigValueException>(() => ConfigSchema.Coerce("ScanSubFolders", 1L));
        Assert.Equal(5L, ConfigSchema.Coerce("MinVerifyVotes", 5.0));
        Assert.Equal(-7L, ConfigSchema.Coerce("MinVerifyVotes", " -7 "));
        Assert.Throws<ConfigValueException>(() => ConfigSchema.Coerce("MinVerifyVotes", 5.5));
        Assert.Throws<ConfigValueException>(() => ConfigSchema.Coerce("MinVerifyVotes", true));
        Assert.Equal(0.5, ConfigSchema.Coerce("ExistingMinScore", "0,5"));
        Assert.Equal(2.0, ConfigSchema.Coerce("ExistingMinScore", 2L));
        Assert.Equal("125", ConfigSchema.Coerce("UiScale", 125L));
        Assert.Throws<ConfigValueException>(() => ConfigSchema.Coerce("UiScale", "25")); // không có trong lựa chọn
        Assert.Equal("1.5", ConfigSchema.Coerce("SyncAudioLang", 1.5));
        Assert.Equal(new List<string> { "a" }, ConfigSchema.Coerce("VideoExts", "a"));
        Assert.Equal(new List<string>(), ConfigSchema.Coerce("VideoExts", ""));
        Assert.Equal(new List<string> { "1", "True", "None", "2.0" }, ConfigSchema.Coerce("VideoExts", new List<object?> { 1L, true, null, 2.0 }));
        Assert.Throws<ConfigValueException>(() => ConfigSchema.Coerce("DonorOffsetMs", D(("a", true))));
        Assert.Throws<ConfigValueException>(() => ConfigSchema.Coerce("DonorOffsetMs", D(("a", "1"))));
        Assert.Equal("giữ", ConfigSchema.Coerce("KhoaLa", "giữ"));
    }
}
