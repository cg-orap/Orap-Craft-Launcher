// Orap Craft Launcher ↔ cubiomes 桥接层
//
// 目的：把 cubiomes（C，MIT）的世界生成能力包成一个极小的 C ABI，供 .NET 通过 P/Invoke 调用。
// 只暴露四件事：版本表、渲染群系瓦片、单点群系、出生点。所有坐标/种子语义与 cubiomes 一致。
//
// 编译（MinGW-w64）：
//   gcc -O2 -fwrapv -shared -o OrapCubiomes.dll orap_cubiomes.c ../third-party/cubiomes/*.c \
//       -I ../third-party/cubiomes -lm
//
// 注意：-fwrapv 必须加，cubiomes 依赖 Java 式的二补数溢出。

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "generator.h"
#include "finders.h"
#include "biomes.h"
#include "util.h"

static void apply_color_overrides(unsigned char colors[256][3])
{
    colors[14][0] = 0xA8; colors[14][1] = 0x8C; colors[14][2] = 0x9E;   // 蘑菇岛：灰紫
    colors[15][0] = 0xB8; colors[15][1] = 0xA1; colors[15][2] = 0xAC;   // 蘑菇岛岸：浅灰紫

    // the_void(127) 在 cubiomes 里是纯黑 0x000000，
    // 画出来就是一块"像洞一样的黑区"。换成深空紫，和真正的空地能区分开。
    colors[127][0] = 0x1A; colors[127][1] = 0x14; colors[127][2] = 0x28;
}


#if defined(_WIN32)
#define ORAP_API __declspec(dllexport)
#else
#define ORAP_API
#endif

typedef struct
{
    int mc;          // cubiomes MCVersion
    const char *key; // 我们的版本标识（与启动器版本列表对齐）
} OrapVersion;

// 从新到旧：启动器默认取第一个可用项。
static const OrapVersion ORAP_VERSIONS[] = {
    {MC_1_21_WD, "1.21.4"},
    {MC_1_21_3, "1.21.3"},
    {MC_1_21_1, "1.21.1"},
    {MC_1_20_6, "1.20.6"},
    {MC_1_19_4, "1.19.4"},
    {MC_1_18_2, "1.18.2"},
    {MC_1_17_1, "1.17.1"},
    {MC_1_16_5, "1.16.5"},
    {MC_1_15_2, "1.15.2"},
    {MC_1_14_4, "1.14.4"},
    {MC_1_13_2, "1.13.2"},
    {MC_1_12_2, "1.12.2"},
    {MC_1_11_2, "1.11.2"},
    {MC_1_10_2, "1.10.2"},
    {MC_1_9_4, "1.9.4"},
    {MC_1_8_9, "1.8.9"},
    {MC_1_7_10, "1.7.10"},
    {MC_1_6_4, "1.6.4"},
};

static const int ORAP_VERSION_COUNT = (int)(sizeof(ORAP_VERSIONS) / sizeof(ORAP_VERSIONS[0]));

static int orap_mc_from_index(int index)
{
    if (index < 0 || index >= ORAP_VERSION_COUNT) return ORAP_VERSIONS[0].mc;
    return ORAP_VERSIONS[index].mc;
}

ORAP_API int orap_version_count(void)
{
    return ORAP_VERSION_COUNT;
}

// 返回版本标识（静态字符串，调用方不要释放）
ORAP_API const char *orap_version_key(int index)
{
    if (index < 0 || index >= ORAP_VERSION_COUNT) index = 0;
    return ORAP_VERSIONS[index].key;
}

ORAP_API int orap_engine_abi(void)
{
    return 1;
}

static int run_generator(Generator *g, int version_index, int large_biomes, int dim, uint64_t seed)
{
    setupGenerator(g, orap_mc_from_index(version_index), large_biomes ? LARGE_BIOMES : 0);
    applySeed(g, dim, seed);
    return 0;
}

/**
 * 渲染一块群系瓦片。
 *
 * version_index: 版本表下标（0 = 最新）
 * dim:           cubiomes 维度常量（DIM_OVERWORLD=0 / DIM_NETHER=-1 / DIM_END=1）
 * centerX/centerZ: 以方块坐标表示的中心（不是瓦片左上角）
 * scale:         每格多少方块（1/4/16/64/256）
 * width/height:  输出像素尺寸
 * pixPerCell:    每个群系格放大到多少像素（1 = 一格一像素）
 * outRgb:        width*height*3 字节，RGB
 *
 * 返回 0 成功，负数失败。
 */
/**
 * 诊断用：和 orap_render_biomes 完全相同的区间计算，但输出**群系 id**而不是颜色。
 * 用来在 C# 侧逐格比对"两个不同起点的渲染是否错位"，并确定错位量。
 */
static int g_dbg_range[8];

/** 诊断用：取最近一次 render/debug 调用里实际传给 genBiomes 的 r 值。 */
ORAP_API int orap_debug_last_range(int *out, int capacity)
{
    if (!out || capacity < 6) return -2;
    for (int i = 0; i < 6; i++) out[i] = g_dbg_range[i];
    return 0;
}

/**
 * 诊断用：把 genBiomes 填好的缓存原样拷出来（含 getMinCacheSize 得到的真实长度），
 * 同时给出 getBiomeAt 的逐点真值。C# 侧用它在各种 stride 下搜索真实布局。
 */
/**
 * 验证"Range.x/z 是 **scale 单位**"的假设（finders.c:678 里就是 (x-radius)>>2）。
 * 这里按 scale 单位算起点，再逐格用 getBiomeAt 对照。
 */
ORAP_API int orap_debug_ids_scaled(uint64_t seed, int version_index, int large_biomes, int dim,
                                   int centerX, int centerZ, int scale,
                                   int width, int height,
                                   int *outIds, int *outProbe)
{
    if (!outIds || !outProbe || width <= 0 || height <= 0) return -2;
    if (scale != 1 && scale != 4 && scale != 16 && scale != 64 && scale != 256) scale = 4;

    Range r;
    r.scale = scale;
    // 关键差异：起点用 scale 单位（方块坐标 / scale）
    r.x = centerX / scale - width / 2;
    r.z = centerZ / scale - height / 2;
    r.sx = width;
    r.sz = height;
    r.y = (scale == 1) ? 63 : 15;
    r.sy = 1;

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, large_biomes, dim, seed) != 0) return -3;

    int *ids = allocCache(&g, r);
    if (!ids) return -4;

    int err = genBiomes(&g, ids, r);
    if (err) { free(ids); return err; }

    for (int z = 0; z < height; z++)
    {
        for (int x = 0; x < width; x++)
        {
            outIds[z * width + x] = ids[z * r.sx + x];

            int blockX = (r.x + x) * scale;
            int blockZ = (r.z + z) * scale;

            outProbe[z * width + x] = (scale == 1)
                ? getBiomeAt(&g, 1, blockX, 63, blockZ)
                : getBiomeAt(&g, 4, blockX / 4, 15, blockZ / 4);
        }
    }

    free(ids);
    return 0;
}

ORAP_API int orap_debug_raw_cache(uint64_t seed, int version_index, int large_biomes, int dim,
                                  int centerX, int centerZ, int scale,
                                  int width, int height,
                                  int *outRaw, int rawCapacity, int *outInfo,
                                  int *outProbe)
{
    if (!outRaw || !outInfo || !outProbe) return -2;
    if (width <= 0 || height <= 0) return -2;
    if (scale != 1 && scale != 4 && scale != 16 && scale != 64 && scale != 256) scale = 4;

    Range r;
    r.scale = scale;
    r.x = centerX / scale - width / 2;
    r.z = centerZ / scale - height / 2;
    r.sx = width;
    r.sz = height;
    r.y = (scale == 1) ? 63 : 15;
    r.sy = 1;

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, large_biomes, dim, seed) != 0) return -3;

    int minSize = getMinCacheSize(&g, scale, r.sx, r.sy, r.sz);

    int *ids = allocCache(&g, r);
    if (!ids) return -4;

    int err = genBiomes(&g, ids, r);
    if (err) { free(ids); return err; }

    int copy = minSize < rawCapacity ? minSize : rawCapacity;
    for (int i = 0; i < copy; i++) outRaw[i] = ids[i];

    for (int z = 0; z < height; z++)
    {
        for (int x = 0; x < width; x++)
        {
            int blockX = (r.x + x) * scale;
            int blockZ = (r.z + z) * scale;

            outProbe[z * width + x] = (scale == 1)
                ? getBiomeAt(&g, 1, blockX, 63, blockZ)
                : getBiomeAt(&g, 4, blockX / 4, 15, blockZ / 4);
        }
    }

    outInfo[0] = minSize;      // 真实缓存长度
    outInfo[1] = r.x;
    outInfo[2] = r.z;
    outInfo[3] = scale * 4;    // 参考

    free(ids);
    return copy;
}

ORAP_API int orap_debug_ids_and_probe(uint64_t seed, int version_index, int large_biomes, int dim,
                                      int centerX, int centerZ, int scale,
                                      int width, int height,
                                      int *outIds, int *outProbe)
{
    if (!outIds || !outProbe || width <= 0 || height <= 0) return -2;
    if (scale != 1 && scale != 4 && scale != 16 && scale != 64 && scale != 256) scale = 4;

    Range r;
    r.scale = scale;
    r.x = centerX / scale - width / 2;
    r.z = centerZ / scale - height / 2;
    r.sx = width;
    r.sz = height;
    r.y = (scale == 1) ? 63 : 15;
    r.sy = 1;

    g_dbg_range[0] = r.x;
    g_dbg_range[1] = r.z;
    g_dbg_range[2] = r.sx;
    g_dbg_range[3] = r.sz;
    g_dbg_range[4] = r.scale;
    g_dbg_range[5] = r.y;

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, large_biomes, dim, seed) != 0) return -3;

    int *ids = allocCache(&g, r);
    if (!ids) return -4;

    int err = genBiomes(&g, ids, r);
    if (err) { free(ids); return err; }

    for (int z = 0; z < height; z++)
    {
        for (int x = 0; x < width; x++)
        {
            outIds[z * width + x] = ids[z * r.sx + x];

            int blockX = (r.x + x) * scale;
            int blockZ = (r.z + z) * scale;

            if (scale == 1)
                outProbe[z * width + x] = getBiomeAt(&g, 1, blockX, 63, blockZ);
            else
                outProbe[z * width + x] = getBiomeAt(&g, 4, blockX / 4, 15, blockZ / 4);
        }
    }

    free(ids);
    return 0;
}

ORAP_API int orap_debug_ids(uint64_t seed, int version_index, int large_biomes, int dim,
                            int centerX, int centerZ, int scale,
                            int width, int height, int *outIds)
{
    if (!outIds || width <= 0 || height <= 0) return -2;
    if (scale != 1 && scale != 4 && scale != 16 && scale != 64 && scale != 256) scale = 4;

    Range r;
    r.scale = scale;
    r.x = centerX / scale - width / 2;
    r.z = centerZ / scale - height / 2;
    r.sx = width;
    r.sz = height;
    r.y = (scale == 1) ? 63 : 15;
    r.sy = 1;

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, large_biomes, dim, seed) != 0) return -3;

    int *ids = allocCache(&g, r);
    if (!ids) return -4;

    int err = genBiomes(&g, ids, r);
    if (err) { free(ids); return err; }

    for (int i = 0; i < width * height; i++) outIds[i] = ids[i];

    free(ids);
    return 0;
}

ORAP_API int orap_render_biomes(uint64_t seed, int version_index, int large_biomes, int dim,
                                int centerX, int centerZ, int scale,
                                int width, int height, int pixPerCell,
                                unsigned char *outRgb)
{
    if (!outRgb || width <= 0 || height <= 0) return -2;
    if (scale != 1 && scale != 4 && scale != 16 && scale != 64 && scale != 256) scale = 4;
    if (pixPerCell < 1) pixPerCell = 1;

    // 以像素为单位反推需要多少个群系格
    int cellsX = (width + pixPerCell - 1) / pixPerCell;
    int cellsZ = (height + pixPerCell - 1) / pixPerCell;

    Range r;
    r.scale = scale;
    // Range.x/z 的单位是 **scale 单位**（cubiomes 自己也是这么用的：
    // finders.c 里 int x1 = (x-radius) >> 2; Range r = {4, x1, z1, ...}）。
    // 以前这里直接放了方块坐标，等于把窗口扔到了 scale 倍远的地方。
    r.x = centerX / scale - cellsX / 2;
    r.z = centerZ / scale - cellsZ / 2;
    r.sx = cellsX;
    r.sz = cellsZ;
    // 地表取样高度：scale=1 时是方块坐标，否则是 1:4 的生物群系坐标
    r.y = (scale == 1) ? 63 : 15;
    r.sy = 1;

    g_dbg_range[0] = r.x;
    g_dbg_range[1] = r.z;
    g_dbg_range[2] = r.sx;
    g_dbg_range[3] = r.sz;
    g_dbg_range[4] = r.scale;
    g_dbg_range[5] = r.y;

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, large_biomes, dim, seed) != 0) return -3;

    int *ids = allocCache(&g, r);
    if (!ids) return -4;

    int err = genBiomes(&g, ids, r);
    if (err)
    {
        free(ids);
        return err;
    }

    unsigned char colors[256][3];
    initBiomeColors(colors);
    apply_color_overrides(colors);

    for (int py = 0; py < height; py++)
    {
        int cz = py / pixPerCell;
        if (cz >= cellsZ) cz = cellsZ - 1;
        for (int px = 0; px < width; px++)
        {
            int cx = px / pixPerCell;
            if (cx >= cellsX) cx = cellsX - 1;

            int id = ids[cz * r.sx + cx];
            if (id < 0 || id > 255) id = 1;
            unsigned char *dst = outRgb + 3 * (py * width + px);
            dst[0] = colors[id][0];
            dst[1] = colors[id][1];
            dst[2] = colors[id][2];
        }
    }

    free(ids);
    return 0;
}

/** 单点群系 id（scale 与上面一致；y 是方块高度，scale>1 时按 1:4 解释） */
/**
 * 单点群系查询。
 * x/y/z 一律传**方块坐标**；内部换算成 getBiomeAt 需要的单位：
 *   · getBiomeAt 的 scale 只接受 1 或 4（cubiomes 文档明确要求）
 *   · scale=4 时 x/z/y 都是 1:4 坐标，所以要 >>2
 * 以前直接把"每像素方块数(16/32/64)"当 scale 传进去，
 * 结果悬停显示的名字和地图颜色完全对不上。
 */
ORAP_API int orap_biome_at(uint64_t seed, int version_index, int dim, int scale, int x, int y, int z)
{
    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, 0, dim, seed) != 0) return -1;

    if (scale <= 1)
        return getBiomeAt(&g, 1, x, y, z);

    return getBiomeAt(&g, 4, x >> 2, y >> 2, z >> 2);
}

/**
 * 调色板覆盖：cubiomes 沿用 AMIDST 的传统配色，
 * 其中 mushroom_fields(14) / mushroom_field_shore(15) 是纯品红 0xFF00FF，
 * 容易被当成"渲染出错"。这里换成更符合直觉的蘑菇色（灰紫 / 灰米）。
 */

/** 群系 id → 调色板颜色（和渲染用的是同一张表，供测试核对"颜色与名字一致"） */
ORAP_API int orap_biome_color(int id, int *outRgb)
{
    if (!outRgb) return -2;
    if (id < 0 || id > 255) return -1;

    unsigned char colors[256][3];
    initBiomeColors(colors);
    apply_color_overrides(colors);

    outRgb[0] = colors[id][0];
    outRgb[1] = colors[id][1];
    outRgb[2] = colors[id][2];
    return 0;
}

/** 群系 id → 英文名（用于悬停提示；静态字符串） */
ORAP_API const char *orap_biome_name(int version_index, int id)
{
    return biome2str(orap_mc_from_index(version_index), id);
}

/** 出生点估算；返回 0 成功 */
ORAP_API int orap_spawn(uint64_t seed, int version_index, int *outX, int *outZ)
{
    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, 0, DIM_OVERWORLD, seed) != 0) return -3;

    Pos p = getSpawn(&g);
    if (outX) *outX = p.x;
    if (outZ) *outZ = p.z;
    return 0;
}

// ────────────────────────────── 结构 ──────────────────────────────
// 只暴露"值得在地图上标出来"的结构，并且按维度分组。

typedef struct
{
    int type;        // cubiomes StructureType
    int dim;         // DIM_OVERWORLD / DIM_NETHER / DIM_END
    const char *key; // 稳定标识，供界面做图标/中文名映射
} OrapStructure;

static const OrapStructure ORAP_STRUCTURES[] = {
    {Village,        DIM_OVERWORLD, "village"},
    {Outpost,        DIM_OVERWORLD, "outpost"},
    {Monument,       DIM_OVERWORLD, "monument"},
    {Mansion,        DIM_OVERWORLD, "mansion"},
    {Desert_Pyramid, DIM_OVERWORLD, "desert_pyramid"},
    {Jungle_Temple,  DIM_OVERWORLD, "jungle_temple"},
    {Swamp_Hut,      DIM_OVERWORLD, "swamp_hut"},
    {Igloo,          DIM_OVERWORLD, "igloo"},
    {Ocean_Ruin,     DIM_OVERWORLD, "ocean_ruin"},
    {Shipwreck,      DIM_OVERWORLD, "shipwreck"},
    {Treasure,       DIM_OVERWORLD, "treasure"},
    {Ruined_Portal,  DIM_OVERWORLD, "ruined_portal"},
    {Ancient_City,   DIM_OVERWORLD, "ancient_city"},
    {Trail_Ruins,    DIM_OVERWORLD, "trail_ruins"},
    {Trial_Chambers, DIM_OVERWORLD, "trial_chambers"},
    {Fortress,       DIM_NETHER,    "fortress"},
    {Bastion,        DIM_NETHER,    "bastion"},
    {Ruined_Portal_N,DIM_NETHER,    "ruined_portal_nether"},
    {End_City,       DIM_END,       "end_city"},
    {End_Gateway,    DIM_END,       "end_gateway"},
};

static const int ORAP_STRUCTURE_COUNT = (int)(sizeof(ORAP_STRUCTURES) / sizeof(ORAP_STRUCTURES[0]));

ORAP_API int orap_structure_count(void)
{
    return ORAP_STRUCTURE_COUNT;
}

ORAP_API const char *orap_structure_key(int index)
{
    if (index < 0 || index >= ORAP_STRUCTURE_COUNT) return "";
    return ORAP_STRUCTURES[index].key;
}

ORAP_API int orap_structure_dim(int index)
{
    if (index < 0 || index >= ORAP_STRUCTURE_COUNT) return DIM_OVERWORLD;
    return ORAP_STRUCTURES[index].dim;
}

/**
 * 在区块矩形内查找结构。
 *
 * 输出为每个结构 3 个 int：结构表下标、方块 x、方块 z。
 * 返回写入个数（可能小于实际命中数，见 *outTotal），负数表示出错。
 *
 * 说明：先用 getStructurePos() 拿生成尝试点（快），再用 isViableStructurePos()
 * 做群系校验（较慢但准确）——只保留确实会生成的点。
 */
ORAP_API int orap_find_structures(uint64_t seed, int version_index, int dim,
                                  int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ,
                                  int *outBuf, int capacity, int *outTotal)
{
    if (outTotal) *outTotal = 0;
    if (!outBuf || capacity <= 0) return -2;

    int mc = orap_mc_from_index(version_index);

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, 0, dim, seed) != 0) return -3;

    if (minChunkX > maxChunkX) { int t = minChunkX; minChunkX = maxChunkX; maxChunkX = t; }
    if (minChunkZ > maxChunkZ) { int t = minChunkZ; minChunkZ = maxChunkZ; maxChunkZ = t; }

    int written = 0;
    int total = 0;

    for (int si = 0; si < ORAP_STRUCTURE_COUNT; si++)
    {
        const OrapStructure *entry = &ORAP_STRUCTURES[si];
        if (entry->dim != dim) continue;

        StructureConfig sconf;
        if (!getStructureConfig(entry->type, mc, &sconf)) continue;
        if (sconf.dim != dim) continue;
        if (sconf.regionSize <= 0) continue;

        int regX0 = minChunkX / sconf.regionSize - 1;
        int regX1 = maxChunkX / sconf.regionSize + 1;
        int regZ0 = minChunkZ / sconf.regionSize - 1;
        int regZ1 = maxChunkZ / sconf.regionSize + 1;

        // 区域太多就跳过该类型，避免拖一下卡死（正常视口远小于这个上限）
        long regions = (long)(regX1 - regX0 + 1) * (long)(regZ1 - regZ0 + 1);
        if (regions > 40000) continue;

        for (int rz = regZ0; rz <= regZ1; rz++)
        {
            for (int rx = regX0; rx <= regX1; rx++)
            {
                Pos p;
                if (!getStructurePos(entry->type, mc, seed, rx, rz, &p)) continue;

                int chunkX = p.x >> 4;
                int chunkZ = p.z >> 4;
                if (chunkX < minChunkX || chunkX > maxChunkX) continue;
                if (chunkZ < minChunkZ || chunkZ > maxChunkZ) continue;

                if (!isViableStructurePos(entry->type, &g, p.x, p.z, 0)) continue;

                total++;
                if (written < capacity)
                {
                    outBuf[written * 3 + 0] = si;
                    outBuf[written * 3 + 1] = p.x;
                    outBuf[written * 3 + 2] = p.z;
                    written++;
                }
            }
        }
    }

    if (outTotal) *outTotal = total;
    return written;
}

/**
 * 末地船：End City 是"结构 + 若干 piece"，船是其中一种 piece（END_SHIP）。
 * 所以先按 End_City 找结构点，再把它的 pieces 展开、只挑出船的位置。
 * 输出每个 2 个 int: x, z（方块坐标）。
 */
ORAP_API int orap_find_end_ships(uint64_t seed, int version_index,
                                 int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ,
                                 int *outBuf, int capacity)
{
    if (!outBuf || capacity <= 0) return -2;

    int mc = orap_mc_from_index(version_index);

    StructureConfig sconf;
    if (!getStructureConfig(End_City, mc, &sconf)) return -4;
    if (sconf.regionSize <= 0) return -4;

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, 0, DIM_END, seed) != 0) return -3;

    if (minChunkX > maxChunkX) { int t = minChunkX; minChunkX = maxChunkX; maxChunkX = t; }
    if (minChunkZ > maxChunkZ) { int t = minChunkZ; minChunkZ = maxChunkZ; maxChunkZ = t; }

    int regX0 = minChunkX / sconf.regionSize - 1;
    int regX1 = maxChunkX / sconf.regionSize + 1;
    int regZ0 = minChunkZ / sconf.regionSize - 1;
    int regZ1 = maxChunkZ / sconf.regionSize + 1;

    long regions = (long)(regX1 - regX0 + 1) * (long)(regZ1 - regZ0 + 1);
    if (regions > 40000) return -5;

    static Piece pieces[END_CITY_PIECES_MAX];
    int written = 0;

    for (int rz = regZ0; rz <= regZ1; rz++)
    {
        for (int rx = regX0; rx <= regX1; rx++)
        {
            Pos p;
            if (!getStructurePos(End_City, mc, seed, rx, rz, &p)) continue;

            int chunkX = p.x >> 4;
            int chunkZ = p.z >> 4;
            if (chunkX < minChunkX || chunkX > maxChunkX) continue;
            if (chunkZ < minChunkZ || chunkZ > maxChunkZ) continue;

            if (!isViableStructurePos(End_City, &g, p.x, p.z, 0)) continue;

            int count = getEndCityPieces(pieces, seed, chunkX, chunkZ);
            if (count <= 0) continue;

            for (int i = 0; i < count && i < END_CITY_PIECES_MAX; i++)
            {
                if (pieces[i].type != END_SHIP) continue;
                if (written >= capacity) return written;

                outBuf[written * 2 + 0] = pieces[i].pos.x;
                outBuf[written * 2 + 1] = pieces[i].pos.z;
                written++;
            }
        }
    }

    return written;
}

/** 要塞（含末地传送门）：只在下界之外的 Overworld，最多 limit 个。输出每个 2 个 int: x, z */
ORAP_API int orap_find_strongholds(uint64_t seed, int version_index, int *outBuf, int capacity)
{
    if (!outBuf || capacity <= 0) return -2;

    int mc = orap_mc_from_index(version_index);

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, 0, DIM_OVERWORLD, seed) != 0) return -3;

    StrongholdIter sh;
    initFirstStronghold(&sh, mc, seed);

    int written = 0;
    while (written < capacity && nextStronghold(&sh, &g) >= 0)
    {
        outBuf[written * 2 + 0] = sh.pos.x;
        outBuf[written * 2 + 1] = sh.pos.z;
        written++;
    }

    return written;
}
