using System.Globalization;
using System.Numerics;

namespace MuxStudio.Core.Tests.Support;

/// <summary>
/// Bản sao CHÍNH XÁC của <c>random.Random</c> trong Python (Mersenne Twister MT19937, cách gieo seed số nguyên,
/// <c>random()</c>, <c>uniform</c>, <c>randrange</c>, <c>getrandbits</c>). Nhờ đó test C# chạy trên đúng cùng bộ dữ liệu
/// giả lập như test Python — cùng seed ra cùng từng số.
/// </summary>
public sealed class PyRandom
{
    private const int N = 624;
    private const int M = 397;
    private readonly uint[] _mt = new uint[N];
    private int _mti = N + 1;

    public PyRandom(long seed)
    {
        // Python: key = các khối 32 bit của |seed| (little-endian); seed 0 -> [0]
        var a = BigInteger.Abs(seed);
        var key = new List<uint>();
        while (a > 0)
        {
            key.Add((uint)(a & 0xFFFFFFFF));
            a >>= 32;
        }
        if (key.Count == 0)
            key.Add(0);
        InitByArray(key);
    }

    private void InitGenrand(uint s)
    {
        _mt[0] = s;
        for (_mti = 1; _mti < N; _mti++)
            _mt[_mti] = unchecked(1812433253u * (_mt[_mti - 1] ^ (_mt[_mti - 1] >> 30)) + (uint)_mti);
    }

    private void InitByArray(List<uint> key)
    {
        InitGenrand(19650218u);
        int i = 1, j = 0;
        var keyLength = key.Count;
        for (var k = Math.Max(N, keyLength); k > 0; k--)
        {
            _mt[i] = unchecked((_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1664525u)) + key[j] + (uint)j);
            i++;
            j++;
            if (i >= N)
            {
                _mt[0] = _mt[N - 1];
                i = 1;
            }
            if (j >= keyLength)
                j = 0;
        }
        for (var k = N - 1; k > 0; k--)
        {
            _mt[i] = unchecked((_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1566083941u)) - (uint)i);
            i++;
            if (i >= N)
            {
                _mt[0] = _mt[N - 1];
                i = 1;
            }
        }
        _mt[0] = 0x80000000u;
    }

    private uint GenrandUInt32()
    {
        uint y;
        uint[] mag01 = [0u, 0x9908b0dfu];
        if (_mti >= N)
        {
            int kk;
            for (kk = 0; kk < N - M; kk++)
            {
                y = (_mt[kk] & 0x80000000u) | (_mt[kk + 1] & 0x7fffffffu);
                _mt[kk] = _mt[kk + M] ^ (y >> 1) ^ mag01[y & 1u];
            }
            for (; kk < N - 1; kk++)
            {
                y = (_mt[kk] & 0x80000000u) | (_mt[kk + 1] & 0x7fffffffu);
                _mt[kk] = _mt[kk + (M - N)] ^ (y >> 1) ^ mag01[y & 1u];
            }
            y = (_mt[N - 1] & 0x80000000u) | (_mt[0] & 0x7fffffffu);
            _mt[N - 1] = _mt[M - 1] ^ (y >> 1) ^ mag01[y & 1u];
            _mti = 0;
        }
        y = _mt[_mti++];
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c5680u;
        y ^= (y << 15) & 0xefc60000u;
        y ^= y >> 18;
        return y;
    }

    /// <summary><c>random()</c>: số thực trong [0, 1) với 53 bit.</summary>
    public double Random()
    {
        var a = GenrandUInt32() >> 5;
        var b = GenrandUInt32() >> 6;
        return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
    }

    /// <summary><c>uniform(a, b)</c> = <c>a + (b - a) * random()</c>.</summary>
    public double Uniform(double a, double b) => a + (b - a) * Random();

    /// <summary><c>getrandbits(k)</c> với k ≤ 32.</summary>
    public uint GetRandBits(int k)
    {
        if (k is <= 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(k));
        return GenrandUInt32() >> (32 - k);
    }

    /// <summary><c>randrange(start, stop)</c> (bước 1).</summary>
    public int RandRange(int start, int stop)
    {
        var n = stop - start;
        if (n <= 0)
            throw new ArgumentException("empty range");
        var k = 32 - BitOperations.LeadingZeroCount((uint)n); // n.bit_length()
        var r = GetRandBits(k);
        while (r >= n)
            r = GetRandBits(k);
        return start + (int)r;
    }

    /// <summary><c>round(x, nd)</c> của Python: làm tròn đúng giá trị nhị phân chính xác.</summary>
    public static double Round(double x, int nd) =>
        double.Parse(x.ToString("F" + nd.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
}
