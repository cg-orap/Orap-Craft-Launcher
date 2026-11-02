using System.Runtime.InteropServices;
using System.Text;

namespace Orion.Core.World;

/// <summary>
/// cubiomes 原生引擎的 P/Invoke 封装（OrapCubiomes.dll，C 侧见 src/Orion.Core/Native/orap_cubiomes.c）。
///
/// 这是"真地图"：群系由 cubiomes 按原版算法生成（1.6.4 – 1.21.x），与 Chunkbase/Amidst 同源。
/// DLL 不存在时 <see cref="Available"/> 为 false，调用方应回退到 <see cref="SimplifiedBiomeMap"/>。
/// </summary>
public static class CubiomesNative
{
    private const string Dll = "OrapCubiomes";

    private static readonly object Gate = new();
    private static bool _probed;
    private static bool _available;
    private static string? _error;
    private static string[] _versions = [];

    // 维度常量与 cubiomes 保持一致
    public const int DimOverworld = 0;
    public const int DimNether = -1;
    public const int DimEnd = 1;

    public static bool Available
    {
        get
        {
            EnsureProbed();
            return _available;
        }
    }

    public static string? LoadError
    {
        get
        {
            EnsureProbed();
            return _error;
        }
    }

    /// <summary>支持的版本标识（新→旧），如 "1.21.4"、"1.18.2"。</summary>
    public static IReadOnlyList<string> Versions
    {
        get
        {
            EnsureProbed();
            return _versions;
        }
    }

    private static void EnsureProbed()
    {
        if (_probed) return;

        lock (Gate)
        {
            if (_probed) return;
            _probed = true;

            try
            {
                if (orap_engine_abi() != 1)
                {
                    _error = "原生引擎 ABI 版本不匹配";
                    return;
                }

                var count = orap_version_count();
                var versions = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    var ptr = orap_version_key(i);
                    versions.Add(ptr == IntPtr.Zero ? $"#{i}" : Marshal.PtrToStringAnsi(ptr) ?? $"#{i}");
                }

                _versions = [.. versions];
                _available = _versions.Length > 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _error = ex.Message;
                _available = false;
            }
        }
    }

    /// <summary>把启动器的游戏版本映射到引擎版本下标（找不到时用最新）。</summary>
    public static int VersionIndexFor(string? gameVersion)
    {
        EnsureProbed();
        if (!_available || string.IsNullOrWhiteSpace(gameVersion)) return 0;

        var normalized = gameVersion.Trim();
        for (var i = 0; i < _versions.Length; i++)
        {
            if (string.Equals(_versions[i], normalized, StringComparison.OrdinalIgnoreCase)) return i;
        }

        // "1.21.11" 这类比引擎新的版本：按次版本号就近取第一个不高于它的。
        static (int Major, int Minor) Parse(string v)
        {
            var parts = v.Split('.');
            var major = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
            return (major, minor);
        }

        var (major, minor) = Parse(normalized);
        for (var i = 0; i < _versions.Length; i++)
        {
            var (vm, vn) = Parse(_versions[i]);
            if (vm < major || (vm == major && vn <= minor)) return i;
        }

        return 0;
    }

    /// <summary>
    /// 渲染一块群系瓦片，返回 RGBA 像素（width*height*4）。
    /// <paramref name="scale"/> 必须是 1/4/16/64/256（每格多少方块）。
    /// </summary>
    public static byte[]? RenderRgba(long seed, int versionIndex, int dimension, int centerBlockX,
        int centerBlockZ, int scale, int width, int height)
    {
        EnsureProbed();
        if (!_available || width <= 0 || height <= 0) return null;

        var rgb = new byte[width * height * 3];
        var handle = GCHandle.Alloc(rgb, GCHandleType.Pinned);
        try
        {
            var rc = orap_render_biomes(unchecked((ulong)seed), versionIndex, 0, dimension,
                centerBlockX, centerBlockZ, scale, width, height, 1, handle.AddrOfPinnedObject());
            if (rc != 0) return null;
        }
        finally
        {
            handle.Free();
        }

        var rgba = new byte[width * height * 4];
        for (int src = 0, dst = 0; src < rgb.Length; src += 3, dst += 4)
        {
            rgba[dst + 0] = rgb[src + 0];
            rgba[dst + 1] = rgb[src + 1];
            rgba[dst + 2] = rgb[src + 2];
            rgba[dst + 3] = 255;
        }

        return rgba;
    }

    /// <summary>
    /// 诊断用：按"中心 + 尺寸"取群系 id 数组（和 RenderRgba 同一套区间计算）。
    /// 用来验证两个不同起点的渲染是否错位、错位多少。
    /// </summary>
    public static int[]? DebugBiomeIds(long seed, int versionIndex, int dimension,
        int centerBlockX, int centerBlockZ, int scale, int width, int height)
    {
        EnsureProbed();
        if (!_available || width <= 0 || height <= 0) return null;

        var ids = new int[width * height];

        try
        {
            var rc = orap_debug_ids(unchecked((ulong)seed), versionIndex, 0, dimension,
                centerBlockX, centerBlockZ, scale, width, height, ids);

            return rc == 0 ? ids : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 诊断用：同时返回 genBiomes 数组与 getBiomeAt 逐点真值。
    /// </summary>
    /// <summary>最近一次诊断调用的返回码（0 = 成功）。</summary>
    public static int LastDebugRc { get; private set; }

    public static (int[] Ids, int[] Probe)? DebugIdsAndProbe(long seed, int versionIndex, int dimension,
        int centerBlockX, int centerBlockZ, int scale, int width, int height)
    {
        EnsureProbed();
        if (!_available || width <= 0 || height <= 0) return null;

        var ids = new int[width * height];
        var probe = new int[width * height];

        try
        {
            var rc = orap_debug_ids_and_probe(unchecked((ulong)seed), versionIndex, 0, dimension,
                centerBlockX, centerBlockZ, scale, width, height, ids, probe);

            LastDebugRc = rc;
            return rc == 0 ? (ids, probe) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            LastDebugRc = -999;
            return null;
        }
    }

    /// <summary>
    /// 诊断用：原样取出 genBiomes 的缓存（真实长度由 getMinCacheSize 给出），
    /// 附带 getBiomeAt 逐点真值，供上层搜索真实布局。
    /// </summary>
    public static (int[] Raw, int Length, int[] Info, int[] Probe)? DebugRawCache(long seed, int versionIndex,
        int dimension, int centerX, int centerZ, int scale, int width, int height)
    {
        EnsureProbed();
        if (!_available || width <= 0 || height <= 0) return null;

        const int capacity = 1 << 18;
        var raw = new int[capacity];
        var info = new int[8];
        var probe = new int[width * height];

        try
        {
            var n = orap_debug_raw_cache(unchecked((ulong)seed), versionIndex, 0, dimension,
                centerX, centerZ, scale, width, height, raw, capacity, info, probe);

            return n >= 0 ? (raw, n, info, probe) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>诊断用：最近一次渲染实际使用的区间 (x, z, sx, sz, scale, y)。</summary>
    public static int[]? DebugLastRange()
    {
        EnsureProbed();
        if (!_available) return null;

        var range = new int[6];

        try
        {
            return orap_debug_last_range(range, range.Length) == 0 ? range : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>单点群系 id；scale 同上。</summary>
    public static int BiomeAt(long seed, int versionIndex, int dimension, int scale, int blockX, int y, int blockZ)
    {
        EnsureProbed();
        if (!_available) return -1;
        return orap_biome_at(unchecked((ulong)seed), versionIndex, dimension, scale, blockX, y, blockZ);
    }

    /// <summary>结构类型（key 稳定，供界面映射图标与中文名）。</summary>
    public sealed record StructureType(string Key, int Dimension);

    private static StructureType[]? _structureTypes;

    public static IReadOnlyList<StructureType> StructureTypes
    {
        get
        {
            EnsureProbed();
            if (!_available) return [];

            if (_structureTypes is null)
            {
                var list = new List<StructureType>();
                var count = orap_structure_count();
                for (var i = 0; i < count; i++)
                {
                    var keyPtr = orap_structure_key(i);
                    var key = keyPtr == IntPtr.Zero ? $"#{i}" : Marshal.PtrToStringAnsi(keyPtr) ?? $"#{i}";
                    list.Add(new StructureType(key, orap_structure_dim(i)));
                }

                _structureTypes = [.. list];
            }

            return _structureTypes;
        }
    }

    /// <summary>在区块矩形内查找结构（已做群系校验）。</summary>
    public static IReadOnlyList<(string Key, int X, int Z)> FindStructures(long seed, int versionIndex,
        int dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ, int capacity = 4000)
    {
        EnsureProbed();
        if (!_available) return [];

        var buffer = new int[capacity * 3];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        int written;
        try
        {
            written = orap_find_structures(unchecked((ulong)seed), versionIndex, dimension,
                minChunkX, minChunkZ, maxChunkX, maxChunkZ, handle.AddrOfPinnedObject(), capacity, out var total);
            if (written < 0) return [];
            written = Math.Min(written, Math.Min(capacity, total));
        }
        finally
        {
            handle.Free();
        }

        var types = StructureTypes;
        var result = new List<(string, int, int)>(written);
        for (var i = 0; i < written; i++)
        {
            var typeIndex = buffer[i * 3];
            if (typeIndex < 0 || typeIndex >= types.Count) continue;
            result.Add((types[typeIndex].Key, buffer[i * 3 + 1], buffer[i * 3 + 2]));
        }

        return result;
    }

    /// <summary>要塞坐标（Overworld，最多 capacity 个）。</summary>
    /// <summary>末地船（End City 的 END_SHIP piece）位置，方块坐标。</summary>
    public static IReadOnlyList<(int X, int Z)> FindEndShips(long seed, int versionIndex,
        int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ, int capacity = 400)
    {
        EnsureProbed();
        if (!_available) return [];

        var buffer = new int[capacity * 2];

        try
        {
            var written = orap_find_end_ships(unchecked((ulong)seed), versionIndex,
                minChunkX, minChunkZ, maxChunkX, maxChunkZ, buffer, capacity);

            if (written <= 0) return [];

            var result = new List<(int X, int Z)>(written);
            for (var i = 0; i < written; i++) result.Add((buffer[i * 2], buffer[i * 2 + 1]));

            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    public static IReadOnlyList<(int X, int Z)> FindStrongholds(long seed, int versionIndex, int capacity = 128)
    {
        EnsureProbed();
        if (!_available) return [];

        var buffer = new int[capacity * 2];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        int written;
        try
        {
            written = orap_find_strongholds(unchecked((ulong)seed), versionIndex,
                handle.AddrOfPinnedObject(), capacity);
            if (written < 0) return [];
        }
        finally
        {
            handle.Free();
        }

        var result = new List<(int, int)>(written);
        for (var i = 0; i < written; i++) result.Add((buffer[i * 2], buffer[i * 2 + 1]));
        return result;
    }

    /// <summary>群系 id → 英文名（cubiomes 提供），用于映射到中文。</summary>
    public static string? BiomeName(int versionIndex, int biomeId)
    {
        EnsureProbed();
        if (!_available) return null;

        var ptr = orap_biome_name(versionIndex, biomeId);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
    }

    /// <summary>出生点估算（cubiomes 的统计近似）。</summary>
    public static (int X, int Z)? Spawn(long seed, int versionIndex)
    {
        EnsureProbed();
        if (!_available) return null;

        if (orap_spawn(unchecked((ulong)seed), versionIndex, out var x, out var z) != 0) return null;
        return (x, z);
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_engine_abi();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_version_count();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr orap_version_key(int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_debug_raw_cache(
        ulong seed, int versionIndex, int largeBiomes, int dimension,
        int centerX, int centerZ, int scale, int width, int height,
        int[] outRaw, int rawCapacity, int[] outInfo, int[] outProbe);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_debug_ids_and_probe(
        ulong seed, int versionIndex, int largeBiomes, int dimension,
        int centerX, int centerZ, int scale, int width, int height,
        int[] outIds, int[] outProbe);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_debug_last_range(int[] outRange, int capacity);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_debug_ids(
        ulong seed, int versionIndex, int largeBiomes, int dimension,
        int centerX, int centerZ, int scale, int width, int height, int[] outIds);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_render_biomes(ulong seed, int versionIndex, int largeBiomes, int dim,
        int centerX, int centerZ, int scale, int width, int height, int pixPerCell, IntPtr outRgb);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_biome_at(ulong seed, int versionIndex, int dim, int scale, int x, int y, int z);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr orap_biome_name(int versionIndex, int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_spawn(ulong seed, int versionIndex, out int outX, out int outZ);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_structure_count();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr orap_structure_key(int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_structure_dim(int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_find_structures(ulong seed, int versionIndex, int dim,
        int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ,
        IntPtr outBuf, int capacity, out int outTotal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_find_end_ships(
        ulong seed, int versionIndex, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ,
        int[] outBuf, int capacity);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int orap_find_strongholds(ulong seed, int versionIndex, IntPtr outBuf, int capacity);
}
