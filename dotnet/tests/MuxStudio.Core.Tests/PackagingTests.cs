using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MuxStudio.Core.Tests.Support;
using MuxStudio.Core.Ui;

namespace MuxStudio.Core.Tests;

/// <summary>
/// Giai đoạn 6 — đóng gói: số phiên bản, tự kiểm tra <c>--kiem-tra</c>, <c>loi_khoi_dong.txt</c>, icon, publish profile, <c>build.bat</c>.
/// Port <c>tests/test_packaging.py</c> (phần PyInstaller / keyring dự phòng không có ở bản .NET). Không chạm Credential Manager thật: trong
/// tiến trình test dùng <see cref="MemorySecretBackend"/>, tiến trình con <c>MuxStudio.exe</c> dùng backend <c>null</c>.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class PackagingTests
{
    private static string DotnetDir => Path.Combine(RepoPaths.Root, "dotnet");

    private static string AppDir => Path.Combine(DotnetDir, "src", "MuxStudio.App");

    // ==========================================================================
    //  phiên bản
    // ==========================================================================

    [Fact]
    public void Version_same_everywhere()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+\z", AppInfo.Version);
        var props = XDocument.Load(Path.Combine(DotnetDir, "Directory.Build.props"));
        Assert.Equal(AppInfo.Version, props.Descendants("Version").Single().Value);
        Assert.Equal($"MuxStudio {AppInfo.Version}", AppInfo.Title);
    }

    [Fact]
    public void Build_info_source_vs_exe()
    {
        var s = AppInfo.BuildInfo(false, "WPF-UI 4.3.0");
        Assert.StartsWith($"{AppInfo.Title} (chạy từ mã nguồn) | .NET {Environment.Version.Major}.", s, StringComparison.Ordinal);
        Assert.EndsWith(" | WPF-UI 4.3.0", s, StringComparison.Ordinal);
        Assert.StartsWith($"{AppInfo.Title} (bản .exe)", AppInfo.BuildInfo(true, ""), StringComparison.Ordinal);
        Assert.False(AppInfo.IsFrozen); // test chay tu ban dung, khong phai ban mot file
    }

    [Theory]
    [InlineData("10.0.22631", "Windows 11 (10.0.22631)")] // Windows 11 van bao "10"
    [InlineData("10.0.19045", "Windows 10 (10.0.19045)")]
    [InlineData("10.0.26100.4652", "Windows 11 (10.0.26100)")]
    public void Os_name_windows(string ver, string want) => Assert.Equal(want, AppInfo.OsName(Version.Parse(ver)));

    // ==========================================================================
    //  tự kiểm tra (--kiem-tra)
    // ==========================================================================

    [Fact]
    public void Self_check_args()
    {
        Assert.True(SelfCheck.IsSelfCheck(["--kiem-tra", "x.txt"], out var p));
        Assert.Equal("x.txt", p);
        Assert.True(SelfCheck.IsSelfCheck(["--self-check"], out p));
        Assert.Null(p);
        Assert.False(SelfCheck.IsSelfCheck([@"D:\Phim\Tên phim"], out _)); // thu muc phim
        Assert.False(SelfCheck.IsSelfCheck([], out _));
    }

    [Fact]
    public void Report_pass_fail_lines()
    {
        CheckItem[] ok = [new("A", "ok", "x"), new("B", "warn", "y")];
        var t = SelfCheck.Report(ok, "dòng bản", @"C:\MuxStudio", new DateTime(2026, 11, 13, 9, 5, 7));
        Assert.StartsWith($"{AppInfo.Title} — tự kiểm tra\ndòng bản\nThư mục chương trình: C:\\MuxStudio\nLúc: 2026-11-13 09:05:07\n\n", t,
                          StringComparison.Ordinal);
        Assert.Contains("[OK]   A: x\n", t, StringComparison.Ordinal);
        Assert.Contains("[!]    B: y\n", t, StringComparison.Ordinal);
        Assert.EndsWith("\nKẾT QUẢ: ĐẠT — chương trình chạy được (1 mục [!] nên xem).\n", t, StringComparison.Ordinal);
        t = SelfCheck.Report([.. ok, new("C", "fail", "z")], "", "", DateTime.Now);
        Assert.Contains("[LỖI]  C: z", t, StringComparison.Ordinal);
        Assert.Contains("KẾT QUẢ: KHÔNG ĐẠT — 1 mục [LỖI], 1 mục [!]. Gửi file này khi báo lỗi.", t, StringComparison.Ordinal);
        Assert.Equal("[OK]   D", new CheckItem("D", "ok").Line);
    }

    [Fact]
    public void Run_check_turns_exception_into_fail_or_warn()
    {
        var items = new List<CheckItem>();
        var zero = 0;
        SelfCheck.Run(items, "hỏng", () => (1 / zero).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SelfCheck.Run(items, "phụ", () => Array.Empty<string>()[1], critical: false);
        SelfCheck.Run(items, "cảnh báo", () => CheckResult.Warn("nên xem"));
        SelfCheck.Run(items, "đạt", () => "chi tiết");
        Assert.Equal(
            new[] { ("fail", "DivideByZeroException"), ("warn", "IndexOutOfRangeException"), ("warn", "nên xem"), ("ok", "chi tiết") },
            items.Select(i => (i.Status, i.Detail.Split(':')[0])).ToList());
    }

    [Fact]
    public void Self_check_without_window()
    {
        using var cfg = new ConfigDirScope();
        var outp = cfg.Tmp["[báo cáo] kiểm tra.txt"];
        var rc = SelfCheck.Execute(outp, AppInfo.BuildInfo(false, ""), ui: null, backend: new MemorySecretBackend(), cfg: new Config());
        var raw = File.ReadAllBytes(outp);
        Assert.False(raw.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF])); // UTF-8 khong BOM
        var text = Encoding.UTF8.GetString(raw);
        Assert.Equal(text.Split('\n').Length - 1, Regex.Matches(text, "\r\n").Count); // CRLF
        var items = text.Split("\r\n").Where(l => l.StartsWith('[')).ToDictionary(l => l[7..].Split(':')[0], l => l[..6].Trim());
        foreach (var name in new[] { "Thư viện lõi (core)", "Thùng rác", "Kết nối HTTPS", "Thư mục cấu hình", "Chạy công cụ ngoài" })
            Assert.True(items.GetValueOrDefault(name) == "[OK]", text);
        Assert.Equal("[!]", items["Windows Credential Manager"]); // backend gia cua test
        Assert.Contains("MemorySecretBackend — đọc được, nhưng KHÔNG phải Windows Credential Manager", text, StringComparison.Ordinal);
        Assert.Contains("Công cụ ngoài (theo Cài đặt hiện tại)", items.Keys);
        Assert.DoesNotContain("Cửa sổ chính", items.Keys); // khong co giao dien
        Assert.Equal(text.Contains("KẾT QUẢ: ĐẠT", StringComparison.Ordinal), rc == 0);
        Assert.Equal(0, rc);
        Assert.Empty(Directory.GetFiles(cfg.Dir, "kiem_tra_*")); // file ghi thu da xoa
        Assert.False(File.Exists(Path.Combine(cfg.Dir, "config.json"))); // khong ghi cau hinh
    }

    [Fact]
    public void Self_check_default_report_in_config_dir()
    {
        using var cfg = new ConfigDirScope();
        Assert.Equal(Path.Combine(cfg.Dir, "kiem_tra.txt"), SelfCheck.DefaultReportPath());
    }

    [Fact]
    public void Startup_error_file_has_version()
    {
        using var cfg = new ConfigDirScope();
        var info = AppInfo.BuildInfo(true, "WPF-UI 4.3.0");
        var p = AppInfo.WriteStartupError("System.InvalidOperationException: thử\n   at MuxStudio.App.App.OnStartup()", info);
        Assert.Equal(Path.Combine(cfg.Dir, "loi_khoi_dong.txt"), p);
        var t = File.ReadAllText(p, Encoding.UTF8);
        Assert.StartsWith(info + "\r\nLúc: ", t, StringComparison.Ordinal);
        Assert.Contains("\r\n\r\nSystem.InvalidOperationException: thử\r\n   at MuxStudio.App.App.OnStartup()", t, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_secret_backend_from_env()
    {
        const string env = Secrets.BackendEnv;
        var oldEnv = Environment.GetEnvironmentVariable(env);
        var old = Secrets.Backend;
        try
        {
            Environment.SetEnvironmentVariable(env, "null");
            Secrets.Backend = null!; // tao lai theo bien moi truong
            var b = Assert.IsType<NullSecretBackend>(Secrets.Backend);
            b.SetPassword("MuxStudio", "TmdbApiKey", "x");
            Assert.Null(b.GetPassword("MuxStudio", "TmdbApiKey"));
            b.DeletePassword("MuxStudio", "TmdbApiKey");
            Environment.SetEnvironmentVariable(env, null);
            Secrets.Backend = null!;
            Assert.IsType<WinVaultBackend>(Secrets.Backend); // mac dinh (khong doc / ghi gi o day)
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, oldEnv);
            Secrets.Backend = old;
        }
    }

    /// <summary>
    /// <c>MuxStudio.exe --kiem-tra báo_cáo.txt</c> chạy THẬT (tiến trình con, từ thư mục khác, cấu hình tạm, kho mật khẩu <c>null</c>): dựng
    /// được cửa sổ chính (ngoài màn hình), không ghi cấu hình — = <c>test_entry_point_script</c> + <c>test_self_check_builds_window</c>.
    /// </summary>
    [Fact]
    public void Exe_self_check_builds_window()
    {
        var cfgName = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name; // Debug / Release
        var exe = Path.Combine(AppDir, "bin", cfgName, "net10.0-windows", "MuxStudio.exe");
        Assert.True(File.Exists(exe), exe);
        using var tmp = new TempDir();
        var cfgDir = tmp["[cfg] MuxStudio"];
        var outp = tmp["kt.txt"];
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = tmp.Path, UseShellExecute = false };
        psi.ArgumentList.Add("--kiem-tra");
        psi.ArgumentList.Add(outp);
        psi.Environment["MUXSTUDIO_CONFIG_DIR"] = cfgDir;
        psi.Environment[Secrets.BackendEnv] = "null"; // khong cham Credential Manager that
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(180_000))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail("MuxStudio.exe --kiem-tra chạy quá 180 s");
        }
        var text = File.ReadAllText(outp, Encoding.UTF8);
        Assert.StartsWith($"{AppInfo.Title} — tự kiểm tra\r\n{AppInfo.Title} (chạy từ mã nguồn) | .NET ", text, StringComparison.Ordinal);
        Assert.Contains("NullSecretBackend", text, StringComparison.Ordinal);
        Assert.True(p.ExitCode == 0, text);
        Assert.Contains("KẾT QUẢ: ĐẠT", text, StringComparison.Ordinal);
        Assert.Matches(@"\[OK\]   Giao diện \(WPF \+ WPF-UI\): WPF \d+\.\d+\.\d+, WPF-UI ", text);
        Assert.Matches($@"\[OK\]   Cửa sổ chính: dựng được 5 thẻ trong \d+\.\d s, tiêu đề ""{Regex.Escape(AppInfo.Title)}""", text);
        Assert.Contains("[OK]   File icon: muxstudio.ico (7 cỡ 16–256 px), muxstudio.png (256 px)", text, StringComparison.Ordinal);
        Assert.Contains("[OK]   Ảnh CAPTCHA (JPEG)", text, StringComparison.Ordinal);
        Assert.Contains("[OK]   Icon cửa sổ: đã đặt", text, StringComparison.Ordinal);
        Assert.Contains("Phông chữ tiếng Việt", text, StringComparison.Ordinal);
        // cau hinh: chi co file ghi thu (da xoa) - cua so dung thu ghi vao thu muc tam rieng, da xoa
        Assert.Empty(Directory.GetFiles(cfgDir));
    }

    // ==========================================================================
    //  icon
    // ==========================================================================

    [Fact]
    public void Icon_files_and_project()
    {
        var ico = File.ReadAllBytes(Path.Combine(AppDir, "Assets", "muxstudio.ico"));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        var n = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var sizes = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var e = ico.AsSpan(6 + 16 * i, 16);
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(e[6..])); // 32 bit (co kenh trong suot)
            var size = e[0] == 0 ? 256 : e[0];
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            Assert.True(size == 256 ? ico.AsSpan(off).StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G']) : ico[off] == 40);
            sizes.Add(size);
        }
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes);
        var png = File.ReadAllBytes(Path.Combine(AppDir, "Assets", "muxstudio.png"));
        Assert.Equal(256, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(256, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

        var proj = XDocument.Load(Path.Combine(AppDir, "MuxStudio.App.csproj"));
        Assert.Equal(@"Assets\muxstudio.ico", proj.Descendants("ApplicationIcon").Single().Value);
        var res = proj.Descendants("Resource").Select(r => (string)r.Attribute("Include")!).ToList();
        Assert.Contains(@"Assets\muxstudio.ico", res);
        Assert.Contains(@"Assets\muxstudio.png", res);
        // ban .NET khong dung file nao ngoai thu muc dotnet/ (ban Python se bi xoa)
        Assert.DoesNotContain(@"..\..\..\", File.ReadAllText(Path.Combine(AppDir, "MuxStudio.App.csproj")), StringComparison.Ordinal);
    }

    // ==========================================================================
    //  publish profile, build.bat, kiem_tra_MuxStudio.bat
    // ==========================================================================

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void Publish_profiles(string rid)
    {
        var x = XDocument.Load(Path.Combine(AppDir, "Properties", "PublishProfiles", rid + ".pubxml"));
        var p = x.Descendants("PropertyGroup").Single().Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
        Assert.Equal(rid, p["RuntimeIdentifier"]);
        Assert.Equal("Release", p["Configuration"]);
        foreach (var k in new[] { "SelfContained", "PublishSingleFile", "IncludeNativeLibrariesForSelfExtract", "PublishReadyToRun" })
            Assert.Equal("true", p[k]);
        Assert.EndsWith($@"\dist\{rid}\", p["PublishDir"], StringComparison.Ordinal);
        Assert.False(p.ContainsKey("PublishTrimmed")); // WPF khong ho tro cat bot
        Assert.False(p.ContainsKey("EnableCompressionInSingleFile")); // nen = khoi dong cham hon (da do)
    }

    private static string ReadBat(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.True(b.All(c => c < 0x80), $"{path}: chỉ chữ không dấu (cmd.exe)");
        var t = Encoding.ASCII.GetString(b);
        Assert.Equal(t.Count(c => c == '\n'), Regex.Matches(t, "\r\n").Count); // CRLF
        Assert.True(t.Count(c => c == '\n') > 10);
        var labels = Regex.Matches(t, @"^:(\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (Match m in Regex.Matches(t, @"goto :(\w+)"))
            Assert.Contains(m.Groups[1].Value, labels); // moi goto deu co nhan
        return t;
    }

    [Fact]
    public void Build_bat()
    {
        var t = ReadBat(Path.Combine(DotnetDir, "build.bat"));
        foreach (var need in new[]
                 {
                     "for %%R in (win-x64 win-arm64) do (", "dotnet publish src\\MuxStudio.App -c Release -p:PublishProfile=%%R",
                     "start \"\" /wait \"%EXE%\" --kiem-tra \"%BAOCAO%\"", "set \"EXE=dist\\win-%ARCH%\\MuxStudio.exe\"",
                     "if /i \"%PROCESSOR_ARCHITECTURE%\"==\"ARM64\" set \"ARCH=arm64\"", "findstr /b /c:\"10.\"", ":loi_sdk", ":loi_dangmo",
                     ":loi_build", ":loi_kiemtra", "pause",
                 })
            Assert.Contains(need, t, StringComparison.Ordinal);
        Assert.True(t.IndexOf("[1/4]", StringComparison.Ordinal) < t.IndexOf("[2/4]", StringComparison.Ordinal));
    }

    [Fact]
    public void Self_check_bat_next_to_exe()
    {
        var t = ReadBat(Path.Combine(AppDir, "kiem_tra_MuxStudio.bat"));
        Assert.Contains("start \"\" /wait \"%~dp0MuxStudio.exe\" --kiem-tra \"%BAOCAO%\"", t, StringComparison.Ordinal);
        Assert.Contains("notepad \"%BAOCAO%\"", t, StringComparison.Ordinal);
        Assert.Contains(@"set ""BAOCAO=%APPDATA%\MuxStudio\kiem_tra.txt""", t, StringComparison.Ordinal);
        // ban phat hanh: nam CANH MuxStudio.exe (khong nhet vao file .exe), ban dung thuong khong chep
        var proj = XDocument.Load(Path.Combine(AppDir, "MuxStudio.App.csproj"));
        var item = proj.Descendants("None").Single(e => (string?)e.Attribute("Include") == "kiem_tra_MuxStudio.bat");
        Assert.Equal("PreserveNewest", (string?)item.Attribute("CopyToPublishDirectory"));
        Assert.Equal("true", (string?)item.Attribute("ExcludeFromSingleFile"));
        Assert.Null(item.Attribute("CopyToOutputDirectory"));
    }

    [Fact]
    public void Gitignore_build_outputs()
    {
        var g = File.ReadAllLines(Path.Combine(RepoPaths.Root, ".gitignore"));
        foreach (var need in new[] { "dist/", "kiem_tra_exe.txt", "bin/", "obj/" })
            Assert.Contains(need, g);
    }
}
