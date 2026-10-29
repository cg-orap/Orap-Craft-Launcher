namespace Orion.Core.World;

/// <summary>
/// 与 <c>java.util.Random</c> 逐位一致的伪随机数发生器。
///
/// 种子相关的原版算法（史莱姆区块、要塞分布等）都建立在这个 LCG 之上，差一位结果就全错，
/// 所以这里按 JDK 的实现原样复刻，并用真实 Java 输出做了黄金值测试。
/// </summary>
public sealed class JavaRandom
{
    private const long Multiplier = 0x5DEECE66DL;
    private const long Addend = 0xBL;
    private const long Mask = (1L << 48) - 1;

    private long _seed;

    public JavaRandom(long seed) => SetSeed(seed);

    public void SetSeed(long seed) => _seed = (seed ^ Multiplier) & Mask;

    private int Next(int bits)
    {
        _seed = (_seed * Multiplier + Addend) & Mask;
        return (int)((ulong)_seed >> (48 - bits));
    }

    public int NextInt() => Next(32);

    public int NextInt(int bound)
    {
        if (bound <= 0) throw new ArgumentOutOfRangeException(nameof(bound), "bound must be positive");

        // 2 的幂走移位分支，与 JDK 完全一致（否则分布和取值都会不同）。
        if ((bound & -bound) == bound)
        {
            return (int)((bound * (long)Next(31)) >> 31);
        }

        int bits, value;
        do
        {
            bits = Next(31);
            value = bits % bound;
        }
        while (bits - value + (bound - 1) < 0);

        return value;
    }

    public bool NextBoolean() => Next(1) != 0;

    public double NextDouble() => ((Next(26) << 27) + Next(27)) / (double)(1L << 53);
}
