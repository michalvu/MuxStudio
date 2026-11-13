using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MuxStudio.Core;

/// <summary>Lỗi mạng (không kết nối được, quá thời gian, đứt giữa chừng…) — = <c>requests.RequestException</c> mà các client
/// Python bắt (<c>_NET_ERR</c>).</summary>
public sealed class HttpNetworkException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Kết quả một lượt gọi HTTP — phần của <c>requests.Response</c> mà lõi dùng (<c>status_code</c>, <c>content</c>,
/// <c>text</c>, <c>json()</c>, <c>headers</c>, <c>url</c>).</summary>
public sealed class HttpResult
{
    private string? _text;

    public HttpResult(int statusCode, byte[] content, OrderedDictionary<string, string>? headers = null, string url = "")
    {
        StatusCode = statusCode;
        Content = content;
        Headers = headers ?? new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Url = url;
    }

    public int StatusCode { get; }

    /// <summary><c>r.content</c>.</summary>
    public byte[] Content { get; }

    /// <summary><c>r.headers</c> (tên không phân biệt hoa thường, giữ thứ tự và cách viết của máy chủ).</summary>
    public OrderedDictionary<string, string> Headers { get; }

    /// <summary><c>r.url</c>: địa chỉ cuối cùng (sau chuyển hướng HTTP).</summary>
    public string Url { get; }

    /// <summary><c>r.headers.get(name)</c> → "" nếu không có.</summary>
    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";

    /// <summary><c>r.text</c>: giải mã như <c>requests</c> — bảng mã trong <c>Content-Type</c>; không ghi thì <c>text/*</c> →
    /// ISO-8859-1, JSON → UTF-8; không có <c>Content-Type</c> → UTF-8 (thay cho đoán bảng mã của <c>charset_normalizer</c>).
    /// Byte sai → U+FFFD.</summary>
    public string Text => _text ??= Decode();

    private string Decode()
    {
        var ct = Header("Content-Type");
        var enc = EncodingFromContentType(ct);
        try
        {
            var e = enc is null ? Py.Utf8Replace : Encoding.GetEncoding(enc, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            return e.GetString(Content);
        }
        catch (ArgumentException)
        {
            return Py.Utf8Replace.GetString(Content); // ten bang ma la -> UTF-8
        }
    }

    /// <summary><c>requests.utils.get_encoding_from_headers</c> (+ <c>None</c> khi không có Content-Type).</summary>
    internal static string? EncodingFromContentType(string contentType)
    {
        if (contentType.Length == 0)
            return null;
        foreach (var part in contentType.Split(';').Skip(1))
        {
            var kv = part.Trim();
            if (kv.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
                return kv[8..].Trim().Trim('"', '\'');
        }
        if (contentType.Contains("text", StringComparison.OrdinalIgnoreCase))
            return "ISO-8859-1";
        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return "utf-8";
        return null;
    }

    /// <summary><c>r.json()</c>; JSON hỏng → <c>false</c> (= <c>except ValueError</c>).</summary>
    public bool TryJson(out object? value)
    {
        try
        {
            var t = Py.Utf8Replace.GetString(Content);
            value = PyJson.Loads(t.Length > 0 && t[0] == '﻿' ? t[1..] : t);
            return true;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    /// <summary><c>try: return r.json() except ValueError: return None</c>.</summary>
    public object? JsonOrNull() => TryJson(out var v) ? v : null;
}

/// <summary>
/// Một <see cref="HttpClient"/> dùng chung cho mọi nguồn mạng (TMDB, TVDB, SubSource, OpenSubtitles, Titulky) — thay
/// <c>requests.Session</c>. Đồng bộ (lõi chạy ở luồng phụ, như bản Python); huỷ được qua <see cref="CancellationToken"/>
/// của <see cref="Job"/> (bấm "Dừng ngay" → <see cref="Cancelled"/>). Giữ cookie (Titulky đăng nhập), tự giải nén gzip,
/// tự theo chuyển hướng HTTP.
/// <para>Test truyền <see cref="HttpMessageHandler"/> giả (không bao giờ gọi mạng thật). Hàm <see cref="Request"/> gắn kèm
/// địa chỉ gốc và chuỗi tham số vào <see cref="HttpRequestMessage.Options"/> để máy chủ giả thấy đúng như <c>fakehttp.py</c>
/// (<c>url</c> và <c>params</c> riêng).</para>
/// </summary>
public sealed class Http : IDisposable
{
    /// <summary>Địa chỉ đúng như nơi gọi truyền vào (chưa ghép tham số).</summary>
    public static readonly HttpRequestOptionsKey<string> UrlOption = new("MuxStudio.Url");

    /// <summary>Chuỗi tham số đã mã hoá (<c>a=1&amp;b=x+y</c>, "" = không có).</summary>
    public static readonly HttpRequestOptionsKey<string> QueryOption = new("MuxStudio.Query");

    /// <summary>User-Agent khi nơi gọi không đặt (requests gửi <c>python-requests/x.y</c>; bản .NET gửi tên thật của app).</summary>
    public static readonly string DefaultUserAgent =
        "MuxStudio/" + (typeof(Http).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1");

    private static readonly Lazy<Http> SharedLazy = new(() => new Http());

    private readonly HttpClient _client;

    /// <param name="handler">(cho test) máy chủ giả; bỏ trống = mạng thật (<see cref="SocketsHttpHandler"/>).</param>
    public Http(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 30, // requests: 30
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Phiên dùng chung cho cả app (tạo lần đầu khi cần).</summary>
    public static Http Shared => SharedLazy.Value;

    /// <summary>
    /// <c>session.request(method, url, params=…, headers=…, json=…, data=…, timeout=…)</c>.
    /// <paramref name="query"/>: giá trị <c>null</c> bị bỏ (như requests), "" vẫn gửi (<c>histstamp=</c>).
    /// Lỗi mạng / quá <paramref name="timeout"/> giây → <see cref="HttpNetworkException"/>; <paramref name="ct"/> bị huỷ →
    /// <see cref="Cancelled"/>.
    /// </summary>
    public HttpResult Request(string method, string url, IEnumerable<KeyValuePair<string, string?>>? query = null,
                              IEnumerable<KeyValuePair<string, string>>? headers = null, object? json = null,
                              IEnumerable<KeyValuePair<string, string>>? form = null, double timeout = 30,
                              CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var qs = query is null ? "" : PyUrl.Encode(query.Where(kv => kv.Value is not null).Select(kv => (kv.Key, kv.Value!)));
        var full = qs.Length == 0 ? url : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + qs;
        HttpRequestMessage req;
        try
        {
            req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), new Uri(full));
        }
        catch (UriFormatException e)
        {
            throw new HttpNetworkException($"Invalid URL '{full}': {e.Message}", e);
        }
        using (req)
        {
            req.Options.Set(UrlOption, url);
            req.Options.Set(QueryOption, qs);
            var hasUa = false;
            var hasAccept = false;
            foreach (var (k, v) in headers ?? [])
            {
                req.Headers.TryAddWithoutValidation(k, v);
                hasUa |= k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase);
                hasAccept |= k.Equals("Accept", StringComparison.OrdinalIgnoreCase);
            }
            if (!hasUa)
                req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            if (!hasAccept)
                req.Headers.TryAddWithoutValidation("Accept", "*/*");
            if (json is not null)
            {
                req.Content = new ByteArrayContent(Py.Utf8NoBom.GetBytes(PyJson.Dumps(json)));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            else if (form is not null)
            {
                req.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(PyUrl.Encode(form.Select(kv => (kv.Key, kv.Value)))));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.001, timeout)));
            try
            {
                using var resp = _client.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                using var ms = new MemoryStream();
                using (var s = resp.Content.ReadAsStream(cts.Token))
                    s.CopyTo(ms);
                var h = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in resp.Headers.NonValidated)
                    h[kv.Key] = string.Join(", ", kv.Value);
                foreach (var kv in resp.Content.Headers.NonValidated)
                    h[kv.Key] = string.Join(", ", kv.Value);
                var final = resp.RequestMessage?.RequestUri?.AbsoluteUri ?? full;
                return new HttpResult((int)resp.StatusCode, ms.ToArray(), h, final);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new Cancelled(ct);
            }
            catch (OperationCanceledException e)
            {
                throw new HttpNetworkException(string.Create(CultureInfo.InvariantCulture,
                    $"Read timed out. (read timeout={timeout})"), e);
            }
            catch (HttpRequestException e)
            {
                throw new HttpNetworkException(e.Message, e);
            }
            catch (IOException e)
            {
                throw new HttpNetworkException(e.Message, e);
            }
        }
    }

    /// <summary>Thay <c>time.sleep</c> mặc định của các client: chờ, nhưng bấm "Dừng ngay" thì thôi chờ ngay (không ném lỗi —
    /// lượt gọi tiếp theo sẽ ném <see cref="Cancelled"/>).</summary>
    public static void Sleep(double seconds, CancellationToken ct)
    {
        if (seconds <= 0)
            return;
        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Mã hoá địa chỉ kiểu <c>urllib.parse</c> (Python).</summary>
public static class PyUrl
{
    /// <summary><c>quote(s, safe=safe)</c>: giữ chữ, số, <c>_.-~</c> và ký tự trong <paramref name="safe"/>; còn lại → <c>%XX</c>
    /// (UTF-8, chữ hoa).</summary>
    public static string Quote(string s, string safe = "/") => QuoteCore(s, safe, plus: false);

    /// <summary><c>quote_plus(s, safe="")</c>: như <see cref="Quote"/> nhưng khoảng trắng → <c>+</c>.</summary>
    public static string QuotePlus(string s, string safe = "") => QuoteCore(s, safe, plus: true);

    private static string QuoteCore(string s, string safe, bool plus)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var b in Py.Utf8Replace.GetBytes(s))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' || safe.Contains(c, StringComparison.Ordinal)))
                sb.Append(c);
            else if (plus && c == ' ')
                sb.Append('+');
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary><c>urlencode(pairs)</c> (quote_plus cho cả khoá và giá trị).</summary>
    public static string Encode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join("&", pairs.Select(kv => QuotePlus(kv.Key) + "=" + QuotePlus(kv.Value)));

    /// <summary><c>parse_qsl(qs, keep_blank_values=True)</c>.</summary>
    public static List<KeyValuePair<string, string>> ParseQs(string qs)
    {
        var outp = new List<KeyValuePair<string, string>>();
        foreach (var part in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=', StringComparison.Ordinal);
            var k = i >= 0 ? part[..i] : part;
            var v = i >= 0 ? part[(i + 1)..] : "";
            outp.Add(new(Unquote(k), Unquote(v)));
        }
        return outp;
    }

    /// <summary><c>unquote_plus</c>.</summary>
    public static string Unquote(string s) => WebUtility.UrlDecode(s);

    /// <summary><c>urljoin(base, url)</c>.</summary>
    public static string Join(string @base, string url) => new Uri(new Uri(@base), url).AbsoluteUri;
}
