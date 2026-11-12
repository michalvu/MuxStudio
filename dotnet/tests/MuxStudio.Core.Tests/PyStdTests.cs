using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>Hành vi thư viện chuẩn Python mà <c>tools.py</c> / <c>probe.py</c> dựa vào (<c>PyStd.cs</c>), đối chiếu với Python.</summary>
[Collection(GlobalStateCollection.Name)] // Which_… đổi biến môi trường PATH
public class PyStdTests
{
    [Fact]
    public void Pure_path_same_as_python() =>
        PyCases.Check("pure_path", x => PyData.L(Py.PurePath((string)x!), Py.PathJoin((string)x!, "f.mkv")));

    [Fact]
    public void Os_path_join_and_splitroot_same_as_python()
    {
        PyCases.Check("join", x => Py.Join((string)((List<object?>)x!)[0]!, (string)((List<object?>)x!)[1]!));
        PyCases.Check("splitroot", x =>
        {
            var (d, r, t) = Py.SplitRoot((string)x!);
            return PyData.L(d, r, t);
        });
    }

    [Fact]
    public void Round_ndigits_same_as_python() =>
        PyCases.Check("round", x =>
        {
            var a = (List<object?>)x!;
            return Py.Round(Py.AsDouble(a[0]!), (int)(long)a[1]!);
        });

    [Fact]
    public void Round_is_exact_unlike_math_round()
    {
        Assert.Equal(0.062, Py.Round(0.0625, 3)); // đúng giữa -> số chẵn
        Assert.Equal(1.0, Py.Round(1.0005, 3)); // 1.0005 thật ra là 1.000499999…
        Assert.Equal(2.001, Py.Round(2.0005, 3));
        Assert.True(double.IsNegative(Py.Round(-0.0001, 3))); // round(-0.0001, 3) == -0.0
        Assert.Equal(23.976, Py.Round(1e9 / 41708333, 3));
    }

    [Fact]
    public void Splitlines_and_strip_same_as_python()
    {
        PyCases.Check("splitlines", x => Py.SplitLines((string)x!).Cast<object?>().ToList());
        PyCases.Check("strip", x => Py.StripWs((string)x!));
    }

    [Fact]
    public void To_int_like_python()
    {
        Assert.Equal(3, Py.ToInt(3L));
        Assert.Equal(5, Py.ToInt(5.9));
        Assert.Equal(-5, Py.ToInt(-5.9));
        Assert.Equal(1, Py.ToInt(true));
        Assert.Equal(12, Py.ToInt(" +12 "));
        Assert.Equal(12, Py.ToInt("١٢"));
        Assert.Throws<FormatException>(() => Py.ToInt("1.5"));
        Assert.Throws<FormatException>(() => Py.ToInt(double.NaN));
        Assert.Throws<InvalidCastException>(() => Py.ToInt(null));
    }

    [Fact]
    public void Abspath_strips_trailing_dots_and_spaces_like_python()
    {
        // os.path.abspath dùng GetFullPathNameW: bỏ dấu cách / dấu chấm cuối tên (tools.is_under dựa vào điều này)
        Assert.Equal(@"C:\x\lib", Py.AbsPath(@"C:\x\lib. "));
        Assert.Equal(@"C:\x\y", Py.AbsPath("C:/x//y/./z/.."));
        Assert.Equal(@"c:\a\b", Py.NormCase("C:/A/b"));
    }

    [Fact]
    public void Which_finds_exe_in_path_with_pathext()
    {
        using var tmp = new TempDir();
        var exe = tmp["muxstudio-which-test.exe"];
        File.WriteAllBytes(exe, []);
        var old = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", @"C:\khong-co;" + tmp.Path);
            Assert.Equal(exe, Py.Which("muxstudio-which-test.exe"));
            Assert.Equal(Path.Combine(tmp.Path, "muxstudio-which-test.EXE"), Py.Which("muxstudio-which-test")); // đuôi theo PATHEXT
            Assert.Null(Py.Which("muxstudio-khong-co"));
            Environment.SetEnvironmentVariable("PATH", "");
            Assert.Null(Py.Which("muxstudio-which-test.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", old);
        }
    }
}
