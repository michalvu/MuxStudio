namespace MuxStudio.Core.Tests.Support;

/// <summary>
/// Test đụng tới state tĩnh dùng chung (bảng ngôn ngữ, ngôn ngữ giao diện, biến môi trường) chạy TUẦN TỰ trong collection này.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalStateCollection
{
    public const string Name = "GlobalState";
}

/// <summary>Thư mục tạm cho mỗi test (thay <c>tmp_path</c> của pytest), tự xoá khi xong.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "muxstudio-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string this[params string[] parts] => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Thư mục phim có ký tự [ ] trong tên (§6.1) — fixture <c>movie_dir</c>.</summary>
    public string MovieDir()
    {
        var d = this["[YTS.MX] Phim Thử (2020)"];
        Directory.CreateDirectory(d);
        return d;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
