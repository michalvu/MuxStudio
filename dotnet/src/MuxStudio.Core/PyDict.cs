using System.Collections;

namespace MuxStudio.Core;

/// <summary>
/// Đọc JSON kiểu Python (kết quả <see cref="PyJson.Loads"/>: bảng = <c>OrderedDictionary&lt;string, object?&gt;</c>,
/// danh sách = <c>List&lt;object?&gt;</c>) với đúng ngữ nghĩa các mẫu hay gặp trong lõi Python:
/// <c>d.get(k)</c>, <c>(d.get(k) or {})</c>, <c>(d.get(k) or [])</c>, <c>str(d.get(k) or "")</c>.
/// </summary>
internal static class PyDict
{
    /// <summary><c>d.get(key, dflt)</c> (<paramref name="d"/> = <c>null</c> → <paramref name="dflt"/>).</summary>
    public static object? Get(this OrderedDictionary<string, object?>? d, string key, object? dflt = null) =>
        d is not null && d.TryGetValue(key, out var v) ? v : dflt;

    /// <summary><c>(d.get(key) or {})</c>: không phải bảng → bảng rỗng (bản sao mới).</summary>
    public static OrderedDictionary<string, object?> GetDict(this OrderedDictionary<string, object?>? d, string key) =>
        AsDict(d.Get(key));

    /// <summary><c>(x or {})</c> cho một giá trị.</summary>
    public static OrderedDictionary<string, object?> AsDict(object? x) =>
        x as OrderedDictionary<string, object?> ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);

    /// <summary><c>(d.get(key) or [])</c>: không phải danh sách → rỗng.</summary>
    public static IEnumerable<object?> GetList(this OrderedDictionary<string, object?>? d, string key) =>
        d.Get(key) is IList l ? l.Cast<object?>() : [];

    /// <summary><c>str(d.get(key) or dflt)</c>.</summary>
    public static string GetStr(this OrderedDictionary<string, object?>? d, string key, string dflt = "") =>
        d.Get(key) is var v && Py.Truthy(v) ? Py.Str(v) : dflt;

    /// <summary><c>int(d.get(key) or 0)</c>.</summary>
    public static int GetInt(this OrderedDictionary<string, object?>? d, string key) =>
        d.Get(key) is var v && Py.Truthy(v) ? Py.ToInt(v) : 0;

    /// <summary><c>d.get(key) or None</c> rồi <c>str()</c>: để truyền tiếp cho hàm nhận <c>str | None</c>.</summary>
    public static string? GetStrOrNull(this OrderedDictionary<string, object?>? d, string key) =>
        d.Get(key) is var v && Py.Truthy(v) ? Py.Str(v) : null;
}
