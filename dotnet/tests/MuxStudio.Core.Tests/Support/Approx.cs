namespace MuxStudio.Core.Tests.Support;

/// <summary>Tương đương <c>pytest.approx</c>: sai số tương đối 1e-6, tuyệt đối 1e-12.</summary>
public static class Approx
{
    public static bool Close(double expected, double actual, double rel = 1e-6, double abs = 1e-12) =>
        expected == actual || Math.Abs(expected - actual) <= Math.Max(rel * Math.Abs(expected), abs);

    public static void Equal(double expected, double actual, double rel = 1e-6, double abs = 1e-12) =>
        Assert.True(Close(expected, actual, rel, abs), $"approx: expected {expected:R}, actual {actual:R}");
}
