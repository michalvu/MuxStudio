using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MuxStudio.Core.Tests.Support;

/// <summary>Phản hồi giả — port <c>FakeResponse</c> của <c>tests/fakehttp.py</c>.</summary>
public sealed class FakeResponse(int status, byte[] content, IEnumerable<(string Key, string Value)>? headers = null)
{
    public int Status { get; } = status;
    public byte[] Content { get; } = content;
    public List<(string Key, string Value)> Headers { get; } = [.. headers ?? []];

    /// <summary><c>FakeResponse(status, json_data)</c>: nội dung = <c>json.dumps(json_data)</c>.</summary>
    public static FakeResponse Json(int status, object? data, params (string, string)[] headers) =>
        new(status, Encoding.UTF8.GetBytes(data is null ? "" : PyJson.Dumps(data)), headers);

    /// <summary><c>FakeResponse(status, text=…)</c>.</summary>
    public static FakeResponse Text(int status, string text, params (string, string)[] headers) =>
        new(status, Encoding.UTF8.GetBytes(text), headers);

    /// <summary><c>FakeResponse(status, content=…)</c>.</summary>
    public static FakeResponse Bytes(int status, byte[] content, params (string, string)[] headers) => new(status, content, headers);

    /// <summary><c>FakeResponse(status)</c>.</summary>
    public static FakeResponse Status_(int status) => new(status, []);
}

/// <summary>Một lượt gọi máy chủ giả đã ghi lại (= <c>Call</c> của <c>fakehttp.py</c>): <see cref="Url"/> là địa chỉ nơi gọi
/// truyền vào (chưa ghép <see cref="Params"/>); <see cref="Json"/> / <see cref="Data"/>: thân POST đã đọc lại.</summary>
public sealed record FakeCall(string Method, string Url, OrderedDictionary<string, string> Params,
                              Dictionary<string, string> Headers, object? Json, OrderedDictionary<string, string>? Data, string FullUrl)
{
    public OrderedDictionary<string, object?>? JsonDict => Json as OrderedDictionary<string, object?>;
}

/// <summary>
/// Máy chủ HTTP giả — port <c>FakeHTTP</c> của <c>tests/fakehttp.py</c>, dưới dạng <see cref="HttpMessageHandler"/> (truyền vào
/// <see cref="Http"/>). Test KHÔNG BAO GIỜ gọi mạng thật. Route thêm sau được xét trước; không route nào khớp → 404.
/// Handler có thể ném <see cref="HttpRequestException"/> để giả lỗi mạng.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private sealed class Route(string method, Regex pattern, Func<FakeCall, FakeResponse>? fn, List<FakeResponse>? seq)
    {
        public string Method { get; } = method;
        public Regex Pattern { get; } = pattern;
        public Func<FakeCall, FakeResponse>? Fn { get; } = fn;
        public List<FakeResponse>? Seq { get; } = seq;
        public int Hits { get; set; }
    }

    private readonly List<Route> _routes = [];
    private readonly List<FakeCall> _calls = [];
    private readonly Lock _lock = new();

    public FakeHttp() => Client = new Http(this);

    /// <summary>Phiên <see cref="Http"/> dùng máy chủ giả này.</summary>
    public Http Client { get; }

    public IReadOnlyList<FakeCall> Calls
    {
        get
        {
            lock (_lock)
                return [.. _calls];
        }
    }

    public FakeHttp Add(string method, string urlRegex, FakeResponse resp) => Add(method, urlRegex, _ => resp);

    /// <summary>Danh sách phản hồi: trả lần lượt, hết thì lặp lại cái cuối.</summary>
    public FakeHttp Add(string method, string urlRegex, params FakeResponse[] seq)
    {
        lock (_lock)
            _routes.Insert(0, new Route(method.ToUpperInvariant(), new Regex(urlRegex), null, [.. seq]));
        return this;
    }

    public FakeHttp Add(string method, string urlRegex, Func<FakeCall, FakeResponse> fn)
    {
        lock (_lock)
            _routes.Insert(0, new Route(method.ToUpperInvariant(), new Regex(urlRegex), fn, null));
        return this;
    }

    /// <summary><c>calls_to(url_regex, method)</c>.</summary>
    public List<FakeCall> CallsTo(string urlRegex, string? method = null)
    {
        var rx = new Regex(urlRegex);
        return Calls.Where(c => rx.IsMatch(c.Url) && (method is null || c.Method == method.ToUpperInvariant())).ToList();
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var full = request.RequestUri!.AbsoluteUri;
        var url = request.Options.TryGetValue(Http.UrlOption, out var u) ? u : full;
        var qs = request.Options.TryGetValue(Http.QueryOption, out var q) ? q : "";
        var ps = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in PyUrl.ParseQs(qs))
            ps[k] = v;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in request.Headers.NonValidated)
            headers[kv.Key] = string.Join(", ", kv.Value);
        object? json = null;
        OrderedDictionary<string, string>? data = null;
        if (request.Content is { } content)
        {
            var body = Encoding.UTF8.GetString(content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult());
            var ct = content.Headers.ContentType?.MediaType ?? "";
            if (ct == "application/json")
            {
                json = PyJson.Loads(body);
            }
            else if (ct == "application/x-www-form-urlencoded")
            {
                data = new OrderedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in PyUrl.ParseQs(body))
                    data[k] = v;
            }
        }
        var call = new FakeCall(request.Method.Method, url, ps, headers, json, data, full);
        Route? hit = null;
        lock (_lock)
        {
            _calls.Add(call);
            foreach (var r in _routes)
            {
                if (r.Method == call.Method && r.Pattern.IsMatch(url))
                {
                    r.Hits++;
                    hit = r;
                    break;
                }
            }
        }
        var resp = hit is null ? FakeResponse.Text(404, "khong co route")
            : hit.Seq is { } seq ? seq[Math.Min(hit.Hits - 1, seq.Count - 1)]
            : hit.Fn!(call);
        var msg = new HttpResponseMessage((HttpStatusCode)resp.Status) { RequestMessage = request, Content = new ByteArrayContent(resp.Content) };
        foreach (var (k, v) in resp.Headers)
        {
            if (!msg.Headers.TryAddWithoutValidation(k, v))
                msg.Content.Headers.TryAddWithoutValidation(k, v);
        }
        return msg;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));

    protected override void Dispose(bool disposing)
    {
        // Client giữ handler này; không giải phóng gì thêm
        base.Dispose(disposing);
    }
}

/// <summary>Thay <c>time.sleep</c>: ghi lại, không chờ thật — port <c>Sleeper</c> của <c>fakehttp.py</c>.</summary>
public sealed class Sleeper
{
    public List<double> Calls { get; } = [];
    public double Now { get; private set; }

    public void Sleep(double s)
    {
        Calls.Add(s);
        Now += s;
    }

    public double Clock() => Now;
}

public static class ZipData
{
    /// <summary><c>zip_bytes(files)</c>.</summary>
    public static byte[] Of(params (string Name, object Data)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                var e = z.CreateEntry(name, CompressionLevel.Fastest);
                using var s = e.Open();
                var b = data as byte[] ?? Encoding.UTF8.GetBytes((string)data);
                s.Write(b);
            }
        }
        return ms.ToArray();
    }
}
