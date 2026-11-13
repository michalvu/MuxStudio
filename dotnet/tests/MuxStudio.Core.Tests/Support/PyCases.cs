using System.Collections;

namespace MuxStudio.Core.Tests.Support;

/// <summary>
/// Ca đối chiếu do Python xuất (<c>Data/python_*_cases.json</c>, sinh bằng <c>tools/gen_dotnet_*.py</c>):
/// <c>{nhóm: [[đầu vào, kết quả Python], ...]}</c>. <see cref="Check"/> chạy hàm C# trên từng đầu vào và so kết quả.
/// </summary>
public static class PyCases
{
    private static readonly Dictionary<string, OrderedDictionary<string, object?>> Files = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>Nội dung một file dữ liệu Python (đọc một lần).</summary>
    public static OrderedDictionary<string, object?> Load(string file)
    {
        lock (Gate)
        {
            if (!Files.TryGetValue(file, out var d))
                Files[file] = d = PyData.J(File.ReadAllText(RepoPaths.Data(file), Py.Utf8Strict));
            return d;
        }
    }

    /// <summary>Chạy <paramref name="fn"/> trên mọi ca của nhóm; lệch ca nào thì báo (tối đa 15 ca) với đầu vào + hai kết quả.</summary>
    public static void Check(string group, Func<object?, object?> fn, string file = "python_probe_cases.json")
    {
        var cases = (List<object?>)Load(file)[group]!;
        Assert.NotEmpty(cases);
        var bad = new List<string>();
        foreach (var c in cases.Cast<List<object?>>())
        {
            object? got;
            try
            {
                got = fn(c[0]);
            }
            catch (Exception e)
            {
                got = $"<{e.GetType().Name}: {e.Message}>";
            }
            if (!Same(c[1], got) && bad.Count < 15)
                bad.Add($"{Show(c[0])}\n    Python: {Show(c[1])}\n    C#:     {Show(got)}");
        }
        Assert.True(bad.Count == 0, $"{group}: {bad.Count}+ ca khác Python (trên {cases.Count}):\n" + string.Join("\n", bad));
    }

    public static string Show(object? x)
    {
        try
        {
            return PyJson.Dumps(Normalize(x), ensureAscii: true);
        }
        catch (ArgumentException)
        {
            return x?.ToString() ?? "None";
        }
    }

    private static object? Normalize(object? x) => x switch
    {
        null or string or bool or long or int or double => x,
        IDictionary => x,
        IList l => l.Cast<object?>().Select(Normalize).ToList(),
        _ => x.ToString(),
    };

    /// <summary>So như JSON: bool khác số; số nguyên/thực so theo giá trị (thực: bằng tuyệt đối); danh sách / bảng so sâu.</summary>
    public static bool Same(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a is bool || b is bool)
            return a is bool x && b is bool y && x == y;
        if (Py.IsNumber(a) && Py.IsNumber(b))
            return Py.AsDouble(a).Equals(Py.AsDouble(b)) && (Py.AsDouble(a) != 0 || double.IsNegative(Py.AsDouble(a)) == double.IsNegative(Py.AsDouble(b)));
        if (a is string sa)
            return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is IDictionary)
            return Py.Equal(a, b);
        if (a is IList la && b is IList lb)
        {
            if (la.Count != lb.Count)
                return false;
            for (var i = 0; i < la.Count; i++)
            {
                if (!Same(la[i], lb[i]))
                    return false;
            }
            return true;
        }
        return false;
    }
}
