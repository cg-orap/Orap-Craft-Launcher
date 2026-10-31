# 扩展功能方案（第二批：稳定性 / 资源管理 / 空间治理 / 存档）

本文是**方案**，不含实现。四个方向的共同点是：现有启动器已经"能装、能启动、能修前置"，
但**装好之后的管理几乎是空白**——模组打架没法查、资源包装了没法启停、磁盘满了没法定位、存档和截图只能开文件夹找。

四个方向按"用户痛感 / 实现风险"排序，建议按顺序做。

| # | 方向 | 痛感 | 风险 | 预估工作量 |
|---|------|------|------|-----------|
| 1 | 模组冲突检测 | 高（启动即崩，报错看不懂） | 低（纯离线读 jar） | 1.5–2 天 |
| 2 | 资源包 / 光影 / 数据包管理 | 高（装了不生效，得进游戏翻菜单） | 中（要改 options.txt / iris.properties） | 2–3 天 |
| 3 | 实例磁盘占用分析 + 清理 | 中高（"为什么我的盘少了 200 GB"） | 中（删除操作必须有回收站兜底） | 1.5–2 天 |
| 4 | 存档 / 截图管理 | 中（多世界玩家刚需） | 中（要读 NBT 头，写保护） | 2–2.5 天 |

合计 7–9.5 天。四个都能在现有架构里做增量，不需要动启动链路。

---

## 1. 模组冲突检测

### 现状

- `ModScanner` 已经能读出 `ModId / Name / Version / Loader / Dependencies / Enabled / Size`，
  但**没有"把整个 mods 目录当成一张图"看**。
- 上一轮加的 `ModDependencyScanner` 只解决"缺"，不解决"重复"和"打架"。
- 实际遇到的例子：`create-aeronautics-bundled.jar` 里内嵌三个模块（已被支持）；
  `Sinytra Connector` 是 Fabric-only 却塞进 NeoForge 包；同一个 mod 存在两个不同版本 jar。

### 要检测的冲突类型（按可判定性排序）

| 类型 | 判定方式 | 置信度 |
|------|----------|--------|
| **C1 重复 modId** | 多个 jar 的 `modId` 相同（含 JarJar 内嵌） | 确定 |
| **C2 重复 jar 名** | 文件名去掉版本号后相同（`xxx-1.0.jar` + `xxx-1.1.jar`） | 确定 |
| **C3 加载器不匹配** | 实例是 NeoForge/Forge，但 jar 只有 `fabric.mod.json`；反之亦然 | 确定 |
| **C4 版本范围互斥** | A 需要 `B [1.0,)`，装的是 0.9；或 A 需要 `B [1.0,2.0)`，装的是 2.1 | 确定（已有 `VersionRange`） |
| **C5 不兼容声明** | `mods.toml` 里 `type = "incompatible"` 的 modId 恰好装着 | 确定（但 NeoForge 只记日志不阻断，故列为 Warning） |
| **C6 同 mixin 配置文件** | 两个 jar 的 `*.mixins.json` 声明同名 mixin 包 | 中（同名不必然冲突，标"可能"） |
| **C7 核心库版本错配** | `kotlinforforge` / `architectury` / `geckolib` 等被多个模组依赖且范围无交集 | 中 |

C1–C4 直接给"必须处理"，C5–C7 给"建议检查"（C5 虽然能确定声明存在，但实测 NeoForge 不因此拒绝启动）。**不做**启发式猜测（比如"这两个模组功能重叠"），
那种只能靠人工，写死在代码里会误报。

### 实现要点

新增 `src/Orion.Core/Mods/ModConflictScanner.cs`：

```csharp
public enum ConflictSeverity { Error, Warning, Info }

public sealed record ModConflict(
    string Code,                  // "C1" ... "C7"
    ConflictSeverity Severity,
    string Title,                 // "重复的 modId：create"
    IReadOnlyList<string> Files,  // 涉及的 jar（相对 mods 路径）
    string Detail,                // 人话说明 + 建议
    string? SuggestedAction);     // "保留较新版本" / "禁用 xxx" / null

public static class ModConflictScanner
{
    public static IReadOnlyList<ModConflict> Scan(
        string modsDirectory, LoaderKind loader, string? gameVersion);
}
```

- 数据来源复用 `ModDependencyScanner.ReadJar`（已含 JarJar 内嵌），
  额外读 `*.mixins.json` 文件名列表（只读 zip 条目名，不解压）。
- 版本比较用现成的 `VersionRange` / `NumericVersionComparer`。
- 加载器判定：实例 `LoaderKind` 已知，jar 里有 `fabric.mod.json` 且无 `mods.toml` → Fabric-only。
- `SuggestedAction` 只给"能机械执行"的建议（保留新版 / 禁用低版本），不自动动手。

### UI

版本详情页「模组」页签顶部加一条**冲突横幅**（复用现有 `Card` 样式）：

- 有 Error：红底描边卡片，`「发现 2 个必须处理的冲突」` + 展开列表 + 每行「禁用/移到 disabled」按钮；
- 只有 Warning：黄/中性描边卡片，可折叠；
- 无冲突：不显示（不占地方）。

同时「模组」页签加一个筛选芯片 `冲突`（现有 `SegmentedControl` 的 `ModFilters` 追加一项），
点进去只列冲突相关的 jar。

### 测试

- 纯离线单测（造 jar）：C1（两个 jar 同 modId）、C2（同名不同版本）、C3（fabric-only 塞进 NeoForge）、
  C4（范围互斥/过低）、C5（incompatible）、C7（两个模组要求不同大版本 kotlin）。
- 真实样本回归：Mechanomania 实例（177 个 jar，含 bundled）应该**零 Error**——
  这一条同时是"不误报"的验收标准。

### 风险

- 误报会让人不敢点"禁用"。缓解：C6/C7 一律 Warning + 措辞"可能"，且不给一键操作。

---

## 2. 资源包 / 光影 / 数据包管理

### 现状

- 只建了 `resourcepacks/`、`shaderpacks/` 目录（`Instance.cs:165`），没有任何界面；
- 下载中心装进来的资源包/光影**落地了但不知道有没有生效**；
- 「模组」页只管 `.jar`。

### 三类的"启用"机制完全不同（这是本项的主要难点）

| 类型 | 目录 | 启用方式 | 我们能不能安全写 |
|------|------|----------|------------------|
| 资源包 | `resourcepacks/` | `options.txt` 的 `resourcePacks:["vanilla","file/xxx.zip"]`（**顺序即优先级**） | ✅ 可以（纯文本、启动器已在写 `options.txt`） |
| 光影 | `shaderpacks/` | Iris/OptiFine 的 `config/iris.properties` 的 `shaderPack=`（Oculus 类似） | ✅ 可以，但要保留未知键 |
| 数据包 | `saves/<世界>/datapacks/` | 文件放进去 + 世界 `level.dat` 的 `DataPacks.Enabled` | ⚠️ 需要改 NBT，风险高 → **只做列出/删除/打开，启用交给游戏内** |

### 实现要点

新增 `src/Orion.Core/Mods/ContentInventory.cs`（或 `Content/ResourcePackInventory.cs`）：

```csharp
public sealed record ResourcePackEntry(string Path, string Name, string? Description,
    string? PackFormat, long Size, bool Enabled, int Priority);

public static class ResourcePackService
{
    static IReadOnlyList<ResourcePackEntry> Scan(string gameDirectory);
    static Task EnableAsync(string gameDirectory, IReadOnlyList<string> enabledFileNames, CancellationToken ct);
}
```

- **读**：扫 `resourcepacks/` 的 `.zip` 与目录，读 `pack.mcmeta` 的 `description` / `pack_format`；
  `options.txt` 里 `resourcePacks` 是 JSON 数组（`file/` 前缀表示 zip），解析出启用列表与顺序，
  剩下的标"已安装未启用"。
- **写**：只改 `options.txt` 里这一行，其余行逐字保留（换行符/未知键不动），写前备份
  `options.txt.orap.bak`（沿用现有 `GameOptionsTemplate` 的写法约定）。
- **光影**：读 `shaderpacks/` + `config/iris.properties` 的 `shaderPack`；
  写入时用"键值替换 + 保留注释"的方式，不重建整个 properties 文件。
- **数据包**：只枚举 `saves/*/datapacks/*`（含 `.zip`），显示"属于哪个世界"，操作为打开/删除/复制路径。

### UI

版本详情页新增页签 **「资源」**（放在「模组」之后），内部用现有 `SegmentedControl` 分三段：

```
资源包 | 光影 | 数据包
```

每段是一个两列列表：左侧名字+描述+大小，右侧 `启用` 开关 + `上移/下移`（仅资源包）+ `删除`。
顶部一排：`打开目录` · `重新扫描` · `全部启用/全部禁用`（仅资源包）。
数据包那一段顶部说明"启用/禁用请进游戏的世界数据包菜单"，只提供打开与删除。

### 测试

- `options.txt` 解析/写回的往返测试：给一段带注释、带无关键、CRLF 的样本，断言写回后**除目标行外逐字节相同**；
- `resourcePacks` 数组顺序 → 优先级映射测试；
- `iris.properties` 键保留测试（未知键、注释、Windows 换行）；
- 空目录 / 只有 vanilla / 有非 zip 目录的边界。

### 风险

- 手写 `options.txt` 一旦破坏，游戏会重置设置。缓解：写前备份 + 只改一行 + 有往返测试。
- 光影配置文件各整合包不同（Iris / Oculus / OptiFine 三家），需要按存在性选择目标文件，
  找不到就只做"列出 + 打开目录"，不猜。

---

## 3. 实例磁盘占用分析 + 一键清理

### 现状

- 侧边栏已有"备份占用 199 B / 8 GB"（`BackupUsage`）；
- 但没有"整个数据目录 6.4 GB 里，模组占多少、日志占多少、缓存占多少"的视图。

### 统计口径

```
数据目录 D:\gd\orion
├─ instances/<每个实例>/minecraft/
│   ├─ mods             ← 可删（但要用户勾选）
│   ├─ resourcepacks / shaderpacks / saves   ← 不可批量删
│   ├─ logs / crash-reports                  ← 可安全清理（按天数）
│   ├─ .orap-cache（前置补齐的临时 jar）      ← 可安全清理
│   └─ config / defaultconfigs                ← 不可删
├─ cache/（版本 JSON、整合包解压、图标）      ← 可清理（下次自动重下）
├─ assets/ + libraries/ + versions/           ← 共享，只统计不删
├─ backups/                                   ← 已有配额
└─ trash/（回收站）                            ← 可清空
```

### 实现要点

新增 `src/Orion.Core/Instances/DiskUsageService.cs`：

```csharp
public sealed record UsageSlice(string Key, string Label, long Bytes, int FileCount, bool Cleanable);

public sealed record InstanceUsage(
    string InstanceId, string Name, long TotalBytes,
    IReadOnlyList<UsageSlice> Slices);

public static class DiskUsageService
{
    static InstanceUsage Measure(Instance instance, CancellationToken ct);
    static long MeasureShared(string dataRoot, CancellationToken ct);   // assets/libraries/versions
    static CleanupPreview PreviewCleanup(Instance instance, CleanupOptions options);
    static Task<CleanupResult> CleanAsync(Instance instance, CleanupOptions options, CancellationToken ct);
}
```

- 目录遍历用 `FileSystemEnumerable` + `Size`（不读文件内容），单实例 177 个 jar + 资源通常 < 1 s；
  大文件（> 2 GB 的 assets）用 `FileInfo.Length` 累加即可。
- 清理策略全部**白名单**：只允许删 `logs/*.log`（保留最近 N 天 & 当前这次启动）、
  `crash-reports/*`、`cache/**`、`.orap-cache/**`、`mods/*.jar.disabled` 里用户勾选的项。
  **saves / mods / resourcepacks 一律不进白名单**（只统计，要删只能用户自己开文件夹）。
- 删除走现有 `RecycleBin`（已有，删除版本时用过），保证可恢复。
- 清理前生成 `CleanupPreview`（将删多少文件、释放多少字节），UI 二次确认后执行。

### UI

新增侧边栏页面 **「空间」**（或并入「工具箱」，取决于你偏好；建议独立，因为它是高频排查入口）：

- 顶部：一条堆叠条形图（每个类别一段色），下面跟一张表：类别 / 大小 / 文件数 / 可清理标记；
- 每个实例一行可展开，展示实例内部分类；
- 右侧：`清理选项`（勾选：日志 >7 天 / 崩溃报告 / 缓存 / 临时 jar / 回收站）+ `预览` + `执行清理`；
- 底部：`打开数据目录`。

侧边栏"备份占用"那个小条保持不变，空间页做深的那一层。

### 测试

- 用临时目录造结构（不同大小文件、0 字节、深层嵌套），断言分类累加正确、总计 = 各部分之和；
- 清理白名单测试：saves 里放文件，勾选全部清理，断言 saves 一个没少；
- 保留 N 天逻辑用可控 `DateTime` 参数注入（不依赖系统时钟的测试漂移）。

### 风险

- 删除是不可逆的用户操作 → 全部走回收站 + 预览 + 白名单三重保护；
- 大目录遍历可能卡 UI → 后台线程 + 进度上报（复用现有 `Activities`）。

---

## 4. 存档 / 截图管理

### 现状

版本详情页已有「存档」页签，但内容只是"存档在 saves 目录里"的说明 + 打开文件夹按钮。
截图完全没有界面。

### 存档列表要读什么

`<世界>/level.dat` 是 **gzip 压缩的 NBT**。要么写一个最小 NBT 读取器，要么退化成"文件夹名 + 修改时间"。

建议：写最小 NBT 读取器（约 150 行，只处理 TAG_Compound/TAG_String/TAG_Long/TAG_Int/列表跳过），
读 `Data.LevelName`、`Data.LastPlayed`、`Data.Version.Name`、`Data.GameType`。
读失败就退化为目录名 + mtime（**不报错**，界面上标注"信息不可读"）。

### 实现要点

新增 `src/Orion.Core/Instances/WorldService.cs` 与 `ScreenshotService.cs`：

```csharp
public sealed record WorldEntry(string Directory, string Name, string? GameVersion,
    DateTime LastPlayed, long SizeBytes, int Seed?, string? GameType, bool HasDatapacks);

public static class WorldService
{
    static IReadOnlyList<WorldEntry> Scan(string gameDirectory);
    static Task<string> BackupAsync(string gameDirectory, string worldName, string backupRoot, CancellationToken ct);
    static Task RestoreAsync(string gameDirectory, string backupZip, CancellationToken ct);
    static Task DeleteAsync(string gameDirectory, string worldName, CancellationToken ct); // → 回收站
}

public sealed record ScreenshotEntry(string Path, DateTime TakenAt, long SizeBytes, int Width, int Height, string? World);

public static class ScreenshotService
{
    static IReadOnlyList<ScreenshotEntry> Scan(string gameDirectory);
    static Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct); // → 回收站
}
```

截图尺寸/世界信息：PNG 尺寸可只读文件头（IHDR，24 字节）——比 `BitmapDecoder` 快得多，
列表 200 张也不会卡；缩略图在 UI 侧用 `BitmapImage.DecodePixelWidth=320` + `CacheOption=OnLoad` 懒加载。

### UI

「存档」页签改成真正的列表（卡片网格，2–4 列，随窗口宽度自适应）：

- 每个世界一张卡：世界名 / 版本 / 最后游玩 / 大小 / 种子（可读时）；
- 卡片右上角 `⋯` 菜单：`备份此世界`（打成 zip 进 backups/）、`删除`（回收站）、`打开文件夹`；
- 页面顶部：`新建世界`（只做说明，实际进游戏）、`全部备份`、`恢复备份…`、`打开 saves`。

新增页签 **「截图」**（或放进「存档」页签的第二段）：
缩略图网格，点击用系统看图器打开，右键 `复制路径 / 删除`，顶部 `打开截图目录`。

### 测试

- NBT 读取器：用一个真实 `level.dat`（或测试里手工 gzip+构造的最小 NBT）断言 LevelName/LastPlayed 解析；
- 损坏 `level.dat` → 退化路径不抛异常（这条必须有测试，否则用户一个坏存档会让整页崩）；
- 世界备份 → 恢复往返（备份后改文件，恢复后内容回到备份时）；
- 截图 PNG 尺寸读取：手工构造 1x1 PNG 头断言 1x1。

### 风险

- 恢复世界会覆盖现有同名世界 → 恢复前自动"另存一份当前世界"（沿用快照的「恢复前」思路）；
- 删除世界包含大量文件 → 回收站 + 二次确认 + 显示将删除的字节数。

---

## 实施顺序与验收

建议一个方向一个提交，每个都走 `build\verify.ps1`（构建 + 320+ 单测 + publish + 启动冒烟）并出截图：

| 顺序 | 交付 | 完成判据 |
|------|------|----------|
| 1 | 冲突检测 Core + 单测 | 177 个 jar 的真实实例 **零 Error、零误报** |
| 2 | 冲突横幅 + 「冲突」筛选 | 故意放两个同 modId jar，界面能指出具体文件 |
| 3 | 资源包/光影 读写 + 单测 | `options.txt` 往返逐字节一致 |
| 4 | 「资源」页签 | 启用/排序后进游戏，资源包确实按顺序生效 |
| 5 | 磁盘占用 Core + 单测 | 总计 = 各部分之和；saves 永不被清 |
| 6 | 「空间」页 | 预览数字与实际删除量一致 |
| 7 | 存档/截图 Core + 单测 | 坏 `level.dat` 不崩；备份恢复往返一致 |
| 8 | 存档/截图 UI | 卡片网格 + 缩略图，删除进回收站 |

## 明确不做（与本项目既有约束冲突或收益不足）

- **AI 相关功能**：无（你的硬性要求）；
- **自动下载 / 自动安装 Java**：无（你的硬性要求，只做"检测 + 引导"）；
- **静默更新启动器**：不做，更新只检查、必须问过才下载；
- **"功能重叠"类冲突的自动判定**：不做，误报代价太高；
- **NBT 写入**（改世界数据包启用状态、改游戏规则）：风险高、收益低，交给游戏内。
