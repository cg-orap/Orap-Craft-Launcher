namespace Orion.Core.World;

/// <summary>
/// 一维/二维梯度噪声（Perlin），带固定置换表与 SplitMix64 种子扩散。
///
/// 这是"简化版"地图的底层：不追求复刻原版 noise router，只要求同一个种子在同一个坐标上永远给出
/// 相同结果（可复现），并且分布看起来有大陆、海洋、山脉的层次。
/// </summary>
public sealed class BiomeNoise
{
    private readonly int[] _perm = new int[512];

    private BiomeNoise(long seed)
    {
        // SplitMix64 扩散，避免相邻 index 的种子产生高度相关的置换表。
        var state = unchecked((ulong)seed);
        ulong Next()
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        var table = new int[256];
        for (var i = 0; i < 256; i++) table[i] = i;
        for (var i = 255; i > 0; i--)
        {
            var j = (int)(Next() % (ulong)(i + 1));
            (table[i], table[j]) = (table[j], table[i]);
        }

        for (var i = 0; i < 512; i++) _perm[i] = table[i & 255];
    }

    public static BiomeNoise Create(long seed, int channel) =>
        new(unchecked(seed + channel * 0x9E3779B1L));

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Grad(int hash, double x, double y)
    {
        return (hash & 7) switch
        {
            0 => x + y,
            1 => x - y,
            2 => -x + y,
            3 => -x - y,
            4 => x,
            5 => -x,
            6 => y,
            _ => -y,
        };
    }

    /// <summary>单层噪声，返回 −1..1。</summary>
    public double Sample(double x, double y)
    {
        var xi = (int)Math.Floor(x) & 255;
        var yi = (int)Math.Floor(y) & 255;
        var xf = x - Math.Floor(x);
        var yf = y - Math.Floor(y);
        var u = Fade(xf);
        var v = Fade(yf);

        var aa = _perm[_perm[xi] + yi];
        var ab = _perm[_perm[xi] + yi + 1];
        var ba = _perm[_perm[xi + 1] + yi];
        var bb = _perm[_perm[xi + 1] + yi + 1];

        var x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        var x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Math.Clamp(Lerp(x1, x2, v), -1, 1);
    }

    /// <summary>多倍频叠加（fBm），返回 −1..1。</summary>
    public double Fbm(double x, double y, int octaves = 4, double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0, amplitude = 1, frequency = 1, norm = 0;
        for (var i = 0; i < octaves; i++)
        {
            sum += Sample(x * frequency, y * frequency) * amplitude;
            norm += amplitude;
            amplitude *= gain;
            frequency *= lacunarity;
        }

        return norm <= 0 ? 0 : Math.Clamp(sum / norm, -1, 1);
    }
}
