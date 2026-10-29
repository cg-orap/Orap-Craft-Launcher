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
    r.x = centerX - (cellsX / 2) * scale;
    r.z = centerZ - (cellsZ / 2) * scale;
    r.sx = cellsX;
    r.sz = cellsZ;
    // 地表取样高度：scale=1 时是方块坐标，否则是 1:4 的生物群系坐标
    r.y = (scale == 1) ? 63 : 15;
    r.sy = 1;

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
ORAP_API int orap_biome_at(uint64_t seed, int version_index, int dim, int scale, int x, int y, int z)
{
    if (scale != 1 && scale != 4 && scale != 16 && scale != 64 && scale != 256) scale = 4;

    Generator g;
    memset(&g, 0, sizeof(g));
    if (run_generator(&g, version_index, 0, dim, seed) != 0) return -1;
    return getBiomeAt(&g, scale, x, y, z);
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
