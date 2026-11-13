using System.Globalization;
using System.Text.RegularExpressions;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

/// <summary>
/// OpenSubtitles.com API (§6.11) — nguồn dự phòng, TỐN QUOTA nên để sau cùng. Port <c>core/opensubs.py</c>
/// (<c>OpenSubtitlesClient</c>): thay <c>Invoke-Os</c>, <c>Get-ErrBody</c>, <c>Connect-OpenSubtitles</c>, <c>Test-OsQuota</c>,
/// <c>Get-OsCandidates</c>, <c>Test-OsUsable</c>, <c>Save-OsSubtitle</c> (SubFetch).
/// <list type="bullet">
/// <item>Gốc <c>https://api.opensubtitles.com/api/v1</c>, header <c>Api-Key</c>. User-Agent PHẢI nằm trong danh sách cho phép
/// (<c>MyApp v1.0.0</c> chạy được; nếu không: 403 <c>kong-user-agent-block</c>).</item>
/// <item><c>POST /login</c> → <c>token</c> + <c>base_url</c>. VIP được chuyển sang <c>vip-api.opensubtitles.com</c> và MỌI request sau
/// đó phải gửi kèm token. 401 → đăng nhập lại, tối đa 2 lần.</item>
/// <item>Quota: cảnh báo khi còn ≤ <c>OsWarnRemaining</c> (50), ngừng khi còn ≤ <c>OsStopRemaining</c> (5).</item>
/// </list>
/// </summary>
public sealed partial class OpenSubtitlesClient
{
    public const string DefaultHost = "api.opensubtitles.com";

    private readonly Action<double> _sleep;

    public OpenSubtitlesClient(string? apiKey, string? username = "", string? password = "", string? userAgent = "MyApp v1.0.0",
                               int pauseMs = 1100, int warnRemaining = 50, int stopRemaining = 5, Http? http = null,
                               Action<double>? sleep = null, Job? job = null, string? tempDir = null)
    {
        ApiKey = Py.StripWs(apiKey ?? "");
        Username = Py.StripWs(username ?? "");
        Password = password ?? "";
        UserAgent = string.IsNullOrEmpty(userAgent) ? "MyApp v1.0.0" : userAgent;
        Pause = Math.Max(0, pauseMs) / 1000.0;
        WarnRemaining = warnRemaining;
        StopRemaining = stopRemaining;
        HttpClient = http ?? Http.Shared;
        Job = job ?? Job.Null;
        _sleep = sleep ?? (s => Http.Sleep(s, Job.Token));
        TempDir = string.IsNullOrEmpty(tempDir) ? null : tempDir;
    }

    public string ApiKey { get; }
    public string Username { get; }
    public string Password { get; }
    public string UserAgent { get; }
    public double Pause { get; }
    public int WarnRemaining { get; }
    public int StopRemaining { get; }
    public Http HttpClient { get; }
    public Job Job { get; set; }
    public string? TempDir { get; }
    public string? Token { get; private set; }
    public string Host { get; private set; } = DefaultHost;
    public int Remaining { get; set; } = -1;
    public int Allowed { get; set; } = -1;
    public bool? Vip { get; private set; }
    public string Level { get; private set; } = "";
    public bool Disabled { get; private set; }
    public string ResetMsg { get; private set; } = "";
    public int Relogins { get; private set; }
    public int LastStatus { get; private set; }

    // ------------------------------------------------------------------ goi API
    private string Url(string path) => $"https://{Host}/api/v1{path}";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRe();

    [GeneratedRegex("user-agent", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserAgentRe();

    [GeneratedRegex("^tt")]
    private static partial Regex TtRe();

    /// <summary><c>request</c> = <c>Invoke-Os</c>. Dựng lại URL ở MỖI lần thử (sau khi đăng nhập lại, máy chủ/token có thể đổi).
    /// Tham số theo thứ tự chữ cái (OpenSubtitles chuyển hướng 301 mọi request không đúng thứ tự → tốn thêm một lượt).</summary>
    public object? Request(string method, string path, IEnumerable<KeyValuePair<string, object?>>? parameters = null, JDict? body = null)
    {
        var q = new List<KeyValuePair<string, string?>>();
        foreach (var (k, v) in (parameters ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (v is not null && Py.Str(v) is { Length: > 0 } s)
                q.Add(new(k, s));
        }
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var headers = new List<KeyValuePair<string, string>> { new("Api-Key", ApiKey), new("Accept", "application/json"), new("User-Agent", UserAgent) };
            if (Token is not null)
                headers.Add(new("Authorization", $"Bearer {Token}"));
            HttpResult r;
            try
            {
                r = HttpClient.Request(method, Url(path), q.Count > 0 ? q : null, headers, json: body, timeout: 30, ct: Job.Token);
            }
            catch (HttpNetworkException e)
            {
                if (attempt < 3)
                    _sleep(2 * attempt);
                else
                    Job.Error($"    [!] OpenSubtitles lỗi mạng: {e.Message}");
                continue;
            }
            LastStatus = r.StatusCode;
            if (r.StatusCode == 200)
                return r.JsonOrNull();
            var msg = Py.Slice(WsRe().Replace(r.Text, " "), 0, 300);
            if (r.StatusCode == 403 && UserAgentRe().IsMatch(msg))
            {
                Job.Error("    [!] OpenSubtitles chặn User-Agent.");
                Job.Warn("        Đặt User-Agent theo dạng 'TênApp v1.0.0' (có đủ 3 số).");
                Job.Warn("        Nếu vẫn bị chặn, đăng ký tên app tại opensubtitles.com › API Consumers.");
                Disabled = true;
                return null;
            }
            if (r.StatusCode == 401)
            {
                Job.Warn($"    [!] OpenSubtitles từ chối xác thực (401) tại {path}");
                if (msg.Length > 0)
                    Job.Dim($"        Máy chủ nói: {msg}");
                if (path != "/login" && Relogins < 2)
                {
                    Relogins++;
                    Job.Warn(string.Create(CultureInfo.InvariantCulture, $"        Đang đăng nhập lại (lần {Relogins})..."));
                    Token = null;
                    _sleep(2);
                    if (Login(quiet: true))
                    {
                        Job.Dim("        Đã lấy token mới, thử lại yêu cầu.");
                        continue;
                    }
                }
                Job.Error("        Không khắc phục được → tắt nguồn OpenSubtitles.");
                Disabled = true;
                return null;
            }
            if (r.StatusCode == 429)
            {
                Job.Warn("    [!] OpenSubtitles giới hạn tần suất, chờ 5 giây...");
                _sleep(5);
                continue;
            }
            if (r.StatusCode is >= 400 and < 500)
            {
                if (msg.Length > 0)
                    Job.Warn(string.Create(CultureInfo.InvariantCulture, $"    [!] OpenSubtitles {r.StatusCode} : {msg}"));
                return null;
            }
            if (attempt < 3)
                _sleep(2 * attempt);
        }
        return null;
    }

    private static KeyValuePair<string, object?> P(string k, object? v) => new(k, v);

    // ------------------------------------------------------------------ dang nhap + quota

    /// <summary><c>login</c> = <c>Connect-OpenSubtitles</c>. Không có tài khoản → vẫn TÌM được, nhưng không TẢI được.</summary>
    public bool Login(bool quiet = false)
    {
        if (ApiKey.Length == 0)
        {
            if (!quiet)
                Job.Dim("[i] Chưa có OpenSubtitles API key → bỏ qua OpenSubtitles.");
            return false;
        }
        if (Username.Length == 0 || Password.Length == 0)
        {
            if (!quiet)
                Job.Warn("[!] Thiếu tài khoản OpenSubtitles → chỉ tìm được, KHÔNG tải được.");
            return false;
        }
        var r = Request("POST", "/login", body: new JDict(StringComparer.Ordinal) { ["username"] = Username, ["password"] = Password }) as JDict;
        if (r is null || r.Count == 0 || !Py.Truthy(r.Get("token")))
        {
            Job.Error("[!] Đăng nhập OpenSubtitles thất bại.");
            Disabled = true;
            return false;
        }
        Token = Py.Str(r["token"]);
        if (Py.Truthy(r.Get("base_url")))
            Host = Py.Str(r["base_url"]).Replace("https://", "", StringComparison.Ordinal).Replace("http://", "", StringComparison.Ordinal).Trim('/');
        var user = r.GetDict("user");
        if (user.Count > 0)
        {
            try
            {
                Allowed = Py.ToInt(user.Get("allowed_downloads"));
            }
            catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
            {
            }
            Vip = Py.Truthy(user.Get("vip"));
            Level = user.GetStr("level");
            if (!quiet)
            {
                var kind = Vip == true ? "VIP" : "thường";
                Job.Ok(string.Create(CultureInfo.InvariantCulture, $"[i] OpenSubtitles: đã đăng nhập ({kind}, mức '{Level}', {Allowed} lượt tải/ngày)"));
                Job.Dim($"    Máy chủ: {Host}");
            }
        }
        if (!quiet)
            RefreshQuota();
        return true;
    }

    /// <summary><c>refresh_quota</c>.</summary>
    public void RefreshQuota()
    {
        var data = PyDict.AsDict(Request("GET", "/infos/user")).GetDict("data");
        if (data.Get("remaining_downloads") is { } rem)
            Remaining = Py.ToInt(rem);
        if (data.Get("allowed_downloads") is { } all)
            Allowed = Py.ToInt(all);
        if (data.Count > 0)
            Job.Info(string.Create(CultureInfo.InvariantCulture, $"    Còn lại hôm nay: {Remaining}/{Allowed} lượt tải"));
    }

    /// <summary><c>quota_ok</c> = <c>Test-OsQuota</c>: chưa biết → cứ thử; ≤ ngưỡng ngừng → tắt nguồn; ≤ ngưỡng cảnh báo → báo.</summary>
    public bool QuotaOk()
    {
        if (Remaining < 0)
            return true;
        if (Remaining <= StopRemaining)
        {
            if (!Disabled)
            {
                Job.Error(string.Create(CultureInfo.InvariantCulture, $"[!!] OpenSubtitles chỉ còn {Remaining} lượt tải — DỪNG dùng nguồn này."));
                if (ResetMsg.Length > 0)
                    Job.Warn($"     {ResetMsg}");
                Disabled = true;
            }
            return false;
        }
        if (Remaining <= WarnRemaining)
            Job.Warn(string.Create(CultureInfo.InvariantCulture, $"    [!] Chú ý: OpenSubtitles chỉ còn {Remaining}/{Allowed} lượt tải hôm nay."));
        return true;
    }

    /// <summary><c>quota_level</c>: 'ok' | 'warn' | 'stop' | 'unknown' — để tô màu trên giao diện.</summary>
    public string QuotaLevel => Remaining < 0 ? "unknown" : Remaining <= StopRemaining ? "stop" : Remaining <= WarnRemaining ? "warn" : "ok";

    // ------------------------------------------------------------------ tim + tai
    private static List<JDict>? DataOf(object? r) =>
        r is JDict d && d.Count > 0 && Py.Truthy(d.Get("data")) ? d.GetList("data").OfType<JDict>().ToList() : null;

    /// <summary><c>search</c> = <c>Get-OsCandidates</c>: (1) có <c>order_by</c> → (2) bỏ sắp xếp → (3) theo tên + năm.</summary>
    public List<JDict> Search(string? imdb, int? season, int? episode, string lang, string title = "", int? year = null)
    {
        if (Disabled)
            return [];
        var p = new List<KeyValuePair<string, object?>> { P("languages", OpenSubs.OsLanguages(lang)) };
        var hasImdb = false;
        if (!string.IsNullOrEmpty(imdb))
        {
            p.Add(P("imdb_id", TtRe().Replace(imdb, "", 1)));
            hasImdb = true;
        }
        else if (!string.IsNullOrEmpty(title))
        {
            p.Add(P("query", title));
            if (year is { } y && y != 0)
                p.Add(P("year", y));
        }
        else
        {
            return [];
        }
        var noSeason = episode is not null && season is null or 0;
        if (episode is not null)
        {
            if (season is { } se && se != 0)
            {
                p.Add(P("season_number", se));
            }
            else if (!hasImdb)
            {
                // Do that 2026-11-09 (SPEC 7.E): query + episode_number KHONG kem season -> 0 ket qua -> khoi hoi
                Job.Dim("        (OpenSubtitles: tập đánh số tuyệt đối mà không rõ season → máy chủ không tìm được, bỏ qua)");
                return [];
            }
            p.Add(P("episode_number", episode));
        }
        if (DataOf(Request("GET", "/subtitles", [.. p, P("order_by", "download_count"), P("order_direction", "desc")])) is { } d1)
            return d1;
        if (Disabled)
            return [];
        _sleep(Pause);
        if (DataOf(Request("GET", "/subtitles", p)) is { } d2)
            return d2;
        if (hasImdb && !string.IsNullOrEmpty(title) && !Disabled && !noSeason)
        {
            _sleep(Pause);
            var p3 = p.Where(kv => kv.Key != "imdb_id").ToList();
            p3.Add(P("query", title));
            if (year is { } y3 && y3 != 0)
                p3.Add(P("year", y3));
            if (DataOf(Request("GET", "/subtitles", p3)) is { } d3)
            {
                Job.Dim("        (tìm theo tên thay vì IMDb)");
                return d3;
            }
        }
        return [];
    }

    /// <summary><c>download</c> = <c>Save-OsSubtitle</c>: <c>POST /download {file_id}</c> → link tạm → GET. Trả file tạm (chưa đặt
    /// vào đích).</summary>
    public string? Download(JDict item)
    {
        if (Disabled || !QuotaOk())
            return null;
        if (Token is null)
        {
            Job.Warn("        [!] Chưa đăng nhập → không tải được.");
            return null;
        }
        int fileId;
        try
        {
            var files = item.GetDict("attributes").Get("files");
            if (!Py.Truthy(files) || files is not System.Collections.IList fl || fl[0] is not JDict f0)
                return null;
            fileId = Py.ToInt(f0.Get("file_id"));
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
        _sleep(Pause);
        Job.Dim(string.Create(CultureInfo.InvariantCulture,
            $"        POST /download file_id={fileId} | máy chủ {Host} | có token ({Token.Length} ký tự)"));
        var d = Request("POST", "/download", body: new JDict(StringComparer.Ordinal) { ["file_id"] = (long)fileId }) as JDict;
        if (d is null || d.Count == 0 || !Py.Truthy(d.Get("link")))
        {
            if (d is { Count: > 0 } && Py.Truthy(d.Get("message")))
                Job.Warn($"        [!] {Py.Str(d["message"])}");
            return null;
        }
        if (d.Get("remaining") is { } rem)
        {
            try
            {
                Remaining = Py.ToInt(rem);
            }
            catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
            {
            }
        }
        if (Py.Truthy(d.Get("message")))
            ResetMsg = Py.Str(d["message"]);
        else if (Py.Truthy(d.Get("reset_time")))
            ResetMsg = $"Quota sẽ hồi lại sau {Py.Str(d["reset_time"])}";
        var tmpDir = TempDir ?? Directory.CreateTempSubdirectory("muxstudio_os_").FullName;
        Directory.CreateDirectory(tmpDir);
        var outp = Py.PathJoin(tmpDir, string.Create(CultureInfo.InvariantCulture, $"os_{fileId}.srt"));
        try
        {
            var r = HttpClient.Request("GET", Py.Str(d["link"]), headers: [new("User-Agent", UserAgent)], timeout: 60, ct: Job.Token);
            if (r.StatusCode != 200)
                throw new IOException(string.Create(CultureInfo.InvariantCulture, $"HTTP {r.StatusCode}"));
            File.WriteAllBytes(outp, r.Content);
        }
        catch (Exception e) when (e is IOException or HttpNetworkException or UnauthorizedAccessException)
        {
            Job.Error($"        [!] Tải file thất bại: {e.Message}");
            return null;
        }
        if (new FileInfo(outp).Length < 64)
        {
            File.Delete(outp);
            Job.Warn("        [!] File tải về rỗng.");
            return null;
        }
        Job.Dim(string.Create(CultureInfo.InvariantCulture, $"        (còn {Remaining}/{Allowed} lượt tải hôm nay)"));
        QuotaOk();
        return outp;
    }

    // ------------------------------------------------------------------ kiem tra ket noi

    /// <summary><c>check</c>.</summary>
    public ConnStatus Check()
    {
        if (ApiKey.Length == 0)
            return new ConnStatus(null, "OpenSubtitles: chưa điền key");
        if (Username.Length == 0 || Password.Length == 0)
        {
            var r = Request("GET", "/infos/formats");
            if (r is null)
                return new ConnStatus(false, string.Create(CultureInfo.InvariantCulture, $"OpenSubtitles: key/User-Agent bị từ chối ({LastStatus})"));
            return new ConnStatus(true, "OpenSubtitles: key hợp lệ (chưa có tài khoản → chỉ tìm, không tải)");
        }
        if (!Login(quiet: true))
            return new ConnStatus(false, string.Create(CultureInfo.InvariantCulture, $"OpenSubtitles: đăng nhập lỗi ({LastStatus})"));
        RefreshQuota();
        var kind = Vip == true ? "VIP" : "thường";
        var host = Host.Split('.')[0];
        var q = Remaining >= 0 ? string.Create(CultureInfo.InvariantCulture, $"còn {Remaining}/{Allowed} lượt tải") : "chưa rõ quota";
        return new ConnStatus(true, $"OpenSubtitles: đăng nhập {kind}, máy chủ {host}, {q}");
    }
}

/// <summary>Hàm không cần mạng của <c>core/opensubs.py</c>.</summary>
public static class OpenSubs
{
    /// <summary><c>OS_LANG_CODES</c>: mã ngôn ngữ OpenSubtitles khác mã 2 chữ — dò thật <c>GET /infos/languages</c> (2026-11-09):
    /// KHÔNG có <c>zh</c>, <c>pt</c>; có <c>zh-cn</c> / <c>zh-tw</c>, <c>pt-pt</c> / <c>pt-br</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> OsLangCodes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["zh"] = ["zh-cn", "zh-tw"],
        ["pt"] = ["pt-br", "pt-pt"],
    };

    /// <summary><c>os_languages</c>: mã ngôn ngữ của MuxStudio → tham số <c>languages</c> (<c>zh</c> → <c>zh-cn,zh-tw</c>).</summary>
    public static string OsLanguages(string? code)
    {
        var c = Py.StripWs(code ?? "").ToLowerInvariant();
        return string.Join(",", (OsLangCodes.TryGetValue(c, out var l) ? l : [c]).Order(StringComparer.Ordinal));
    }

    /// <summary><c>is_machine</c>.</summary>
    public static bool IsMachine(JDict item)
    {
        var a = item.GetDict("attributes");
        return a.Get("machine_translated") is true || a.Get("ai_translated") is true;
    }

    /// <summary><c>usable</c> = <c>Test-OsUsable</c>: bỏ SDH / Forced / dịch máy (nếu <c>OsExcludeMachine</c>) / không có file.</summary>
    public static bool Usable(JDict item, bool excludeSdh = true, bool excludeForced = true, bool excludeMachine = true)
    {
        if (item.Get("attributes") is not JDict a || a.Count == 0)
            return false;
        if (excludeSdh && a.Get("hearing_impaired") is true)
            return false;
        if (excludeForced && a.Get("foreign_parts_only") is true)
            return false;
        if (excludeMachine && IsMachine(item))
            return false;
        return Py.Truthy(a.Get("files"));
    }
}
