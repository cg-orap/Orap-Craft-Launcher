# 测试与验收报告

本报告只记录**实际执行**过的验证。凡是无法在本环境验证的项目，一律在
[第 5 节](#5-未验证项) 标注为「未验证」，没有伪造通过记录。

---

## 1. 测试环境

| 项目 | 值 |
| --- | --- |
| 操作系统 | Windows 11 家庭版中文版，10.0.26200，x64 |
| 显示 | 1536×864，125% 缩放（DWM），NVIDIA GeForce RTX 4050 Laptop |
| CPU 架构 | AMD64 |
| 系统 Java | Oracle JDK **21.0.4**（`C:\Program Files\Java\jdk-21`）、JDK **17.0.11** |
| .NET | 系统仅有 6.0.16 / 8.0.13 运行时，**无 SDK**；为构建本地安装了 **.NET SDK 10.0.402**（`D:\dotnet`） |
| 其它工具 | 无 `git`、无 `node`；`github.com` 网页不可达（`codeload.github.com` 归档可用）；`api.nuget.org` 302 到 `nuget.azure.cn` |
| 数据目录 | `D:\gd\orion`（经 junction 映射，避免中文路径影响工具链） |

网络可达性实测（`curl`）：`launchermeta` 200、`piston-meta` 200、`piston-data` 200、
`resources/libraries.minecraft.net` 正常、`meta.fabricmc.net` 200、`meta.quiltmc.org` 200、
`maven.minecraftforge.net` 200、`api.modrinth.com` 200；
**`maven.neoforged.net` 不可达（连接超时）**，`maven.creeperhost.net` 与 BMCLAPI 的 `/maven` 可用。

---

## 2. 自动化测试

```
> dotnet test tests\Orion.Tests\Orion.Tests.csproj
已通过! - 失败: 0，通过: 84，已跳过: 0，总计: 84
```

覆盖范围（每项均为真实断言，不是「编译通过」）：

| 测试类 | 数量 | 覆盖内容 |
| --- | --- | --- |
| `VersionLogicTests` | 24 | 规则顺序覆盖、OS/arch/features 判定、正则容错、Maven 坐标与分类器架构过滤、继承合并（去重/替换/参数拼接/legacy 字段）、log4j 格式化、参数替换与命令行拆分、类路径选择（含 NeoForge `ignoreList` 分支） |
| `DownloadEngineTests` | 13 | 本地真实 HTTP：Range 续传、服务器忽略 Range、摘要不符、503 重试、404 快速失败、跳过已存在、批内去重、损坏修复、取消、进度/速率、不可达主机、镜像映射 |
| `AppBehaviourTests` | 22 | 离线 UUID 规范值、账户 DPAPI 加密存储（断言磁盘无明文）、授权码解析、实例隔离、移除进回收站、`.minecraft` 导入、Prism 导入、INI 解析、设置往返与损坏容错、TOML Mod 解析、Mod 兼容性、崩溃分析、JSON 容错、版本号数值排序、日志脱敏 |
| `MrPackTests` | 4 | 导出内容与「不包含存档」、可选包含存档、Fabric 包索引解析、非整合包返回 null |
| 合计 | **84** | 全部通过 |

---

## 3. 端到端验证（真实下载 + 真实启动）

### 3.1 原版 Minecraft 1.21.1 安装

```
> orion install 1.21.1 --concurrency 12
安装完成: 3945 个文件, 872.8 MB, 用时 83.4s
  根版本: 1.21.1 · 需要的 Java: 21 · 依赖库: 97
```

- 客户端 JAR（26.8 MB）、54 个依赖库、资源索引 `17`、3888 个资源对象、log4j 配置全部下载；
- 安装后再次执行 `orion verify 1.21.1`：**3968 个文件（含 3911 个资源），缺失 0，损坏 0，用时 0.5s**；
- 峰值速度 23.32 MB/s，平均约 10 MB/s（12 并发）。

### 3.2 原版 1.21.1 启动（离线账户）

```
> orion launch 1.21.1 --offline Tester --memory 2048 --kill-after 45
```
游戏日志（已由 log4j 格式化）关键行：
```
[09:11:41] [Render thread/INFO] [minecraft/Minecraft]: Setting user: Tester
[09:11:41] [Render thread/INFO] [minecraft/Minecraft]: Backend library: LWJGL version 3.3.3+5
[09:11:43] [Render thread/INFO] [minecraft/SoundEngine]: OpenAL initialized on device OpenAL Soft on 扬声器 (Realtek(R) Audio)
[09:11:43] [Render thread/INFO] [minecraft/SoundEngine]: Sound engine started
[09:11:43] [Render thread/INFO] [minecraft/TextureAtlas]: Created: 1024x512x4 minecraft:textures/atlas/blocks.png-atlas
...
```
→ **进入主菜单**（纹理图集、声音引擎、OpenAL、LWJGL 全部就绪）。
进程最终由启动器主动结束，故标记为「已由启动器结束」而不是崩溃。

### 3.3 NeoForge 21.1.264 for 1.21.1 安装

```
> orion install-loader neoforge 1.21.1
...
[安装] Processor: net.neoforged.installertools:binarypatcher
[安装] Processor: net.neoforged.installertools:binarypatcher: Patching input
[安装] Processor: net.neoforged.installertools:binarypatcher: Adding new files
[安装] Injecting profile
[安装] Successfully installed client into launcher.
安装成功: 版本 ID = neoforge-21.1.264
已创建实例: 1.21.1-neoforge-063067
```
生成的 `versions/neoforge-21.1.264/neoforge-21.1.264.json`：
`id=neoforge-21.1.264`、`inheritsFrom=1.21.1`、
`mainClass=cpw.mods.bootstraplauncher.BootstrapLauncher`、`libraries=47`。

> 说明：本机 `maven.neoforged.net` 不可达，首次尝试时 installer 自身下载失败
> （`SocketTimeoutException`）。改为由启动器**预下载 installer 依赖**后成功。这不是跳过步骤，
> installer 的处理器仍然完整执行并生成了版本配置。

### 3.4 NeoForge 1.21.1 启动

```
> orion launch neoforge-21.1.264 --offline Tester --memory 3072 --kill-after 75
```
关键日志：
```
[09:44:16] [main/INFO]: ModLauncher 11.0.5+main.901c6ea8 starting: java version 21.0.4 ...
[09:44:16] [pool-2-thread-1/INFO]: GL info: NVIDIA GeForce RTX 4050 Laptop GPU/PCIe/SSE2 GL version 4.6.0
[09:44:16] [main/INFO]: Mod List:
        Minecraft 1.21.1 (minecraft)
        NeoForge 21.1.264 (neoforge)
[09:44:22] [modloading-worker-0/INFO]: NeoForge mod loading, version 21.1.264, for MC 1.21.1
[09:44:21] [Render thread/INFO] [minecraft/Minecraft]: Setting user: Tester
[09:44:23] [Render thread/INFO] [minecraft/ReloadableResourceManager]: Reloading ResourceManager: vanilla, mod_resources, mod/neoforge
```
→ **进入主菜单**，加载器与模组资源均生效。

### 3.5 Java 检测

```
> orion java
检测到 2 个 Java 运行时:
  [ 21] Java 21 (21.0.4) [64-bit] · Oracle Corporation   C:\Program Files\Java\jdk-21\bin\java.exe  (JAVA_HOME)
  [ 17] Java 17 (17.0.11) [64-bit] · Oracle Corporation  C:\Program Files\Java\jdk-17\bin\java.exe  (scan)
Java 21 → C:\Program Files\Java\jdk-21
```
→ 1.21.1（要求 Java 21）自动选中 JDK 21；1.17 也被正确识别并保留。

### 3.6 实例隔离与导入

- `instances --create` 创建两个实例，各自拥有 `minecraft/{saves,mods,config,...}`，
  在一个实例写入 `saves/world/level.dat` 后另一个实例不可见（自动化测试断言）。
- `import "…\.minecraft"`：识别为 `Prism Launcher / MultiMC` 或 `.minecraft`，
  每个 `versions/<id>/<id>.json` 成为一个**外部实例**；断言源目录未被写入。
- `instances --remove <id>`：目录移动到 `trash/<id>-<时间戳>`，断言存档文件仍可访问。
- 真实导入：把导出的 `.mrpack` 重新导入，创建实例 `导入测试包-faff8a`，
  版本 `neoforge-21.1.264`，覆盖文件 3 个——**导出→导入闭环成功**。

### 3.7 桌面 UI

发布版与调试版均逐页截图验证（7 个页面全部使用真实数据渲染）：

| 页面 | 观察到的真实内容 |
| --- | --- |
| 首页 | 实例「1.21.1 NeoForge」、`Java 21 (21.0.4)`、`继承链 neoforge-21.1.264 ← 1.21.1`、账户区、实时日志区 |
| 游戏下载 | 920 个版本、按类型/关键字过滤、加载器下拉、实例名、安装/取消按钮 |
| 实例管理 | 实例列表、内存/分辨率/JVM 参数、检查完整性、导出整合包、移除实例 |
| Mod 管理 | 实例上下文、Mod 列表（启用/禁用/移除）、Modrinth 搜索 |
| 下载管理 | 任务列表、进度条、速率、并发设置、错误报告入口 |
| 设置 | 数据目录、Java 运行时列表（21/17）、内存、下载源、代理、主题 |
| 日志 | 启动器日志、游戏输出、崩溃分析区（有结论时自动展开） |

截图见 `docs/images/`。

### 3.8 发布产物

```
dist/win-x64/OrionLauncher.exe   59.05 MB   self-contained, single-file
dist/win-x64/cli/orion.exe        self-contained, single-file
```
验证方式：**清空 `DOTNET_ROOT`/`DOTNET_MULTILEVEL_LOOKUP` 后直接双击运行**，
进程正常启动、标题正确、并在空目录下自动创建完整数据目录结构
（`assets/ cache/ instances/ libraries/ logs/ runtime/ versions/` 与 `cache/version_manifest_v2.json`）。

### 3.9 Modrinth 集成（真实 API）

```
> orion mods 1.21.1-neoforge-063067 --search jei --limit 5
Modrinth 搜索 “jei”: 共 86 个结果
  Just Enough Items (JEI) (jei) — 85,903,167 次下载
  ...
```
→ 按实例的 MC 版本与加载器过滤生效。

---

## 4. 故障场景验证

| 场景 | 验证方式 | 结果 |
| --- | --- | --- |
| 下载超时 / 主机不可达 | 单测 `FailsFastOnUnresolvableHostAndRecordsDiagnostics`；实测 `maven.neoforged.net` | 记录 `ExceptionType` 与消息，失败报告可定位；主机被打入「不健康」名单 |
| 服务器忽略 Range | 单测 `HandlesServerThatIgnoresRangeHeader` | 从头重写，文件未损坏 |
| 文件损坏 | 单测 `RejectsFileWithWrongDigestAndReportsFailure` / `RepairsCorruptExistingFile` | 删除并重下；`verify --repair` 实测可将缺失/损坏文件补齐 |
| 瞬态 5xx | 单测 `RetriesTransientServerErrorsThenSucceeds` | 指数退避后成功 |
| 404 | 单测 `Reports404WithoutRetryingForever` | 不无限重试，立即失败并记录状态码 |
| 安装取消 | 单测 `CancellationLeavesPartialFileForResume` + 安装器取消路径 | 保留 `.part`，可继续；报告为「已取消」 |
| 磁盘空间不足 | **未测试**（需构造满盘环境） | 见未验证项 |
| 配置/JSON 损坏 | 单测 `SettingsSurviveCorruptFile`、`JsonUtilIgnoresUnknownFieldsAndAcceptsComments` | 回落默认值并记录警告 |
| 存档保护 | 单测 `RemovingInstanceMovesFilesToTrashInsteadOfDeleting` | 移动而非删除 |

---

## 5. 未验证项

以下项目在本环境**无法**验证，如实列出（不是「通过」）：

1. **微软正版登录的真实联调**：没有可用于测试的 Microsoft 账户，也不应在自动化流程中创建账户。
   已实现完整流程（OAuth → Xbox Live → XSTS → Minecraft → profile）与令牌刷新，
   并对授权码解析、授权 URL 构造等纯逻辑做了单测；但**「用真实账户登录成功」未验证**。
2. **Forge / Fabric / Quilt 的端到端安装与启动**：三个 provider 均已实现，
   Forge/Quilt/Fabric 的元数据接口可达且版本列表可获取（`orion loaders` 实测），
   但**只有 NeoForge 1.21.1 完成了完整的安装 + 启动验证**。
3. **CurseForge 整合包**：需要授权 API Key，未接入，因而未测试。
4. **PCL2 / HMCL 真实实例的导入**：本机没有安装 PCL2/HMCL，
   导入逻辑用**构造的目录结构**做了单测（`.minecraft/versions/...`、Prism `instance.cfg` + `mmc-pack.json`），
   但**没有对真实 PCL2/HMCL 目录**做过导入验证。
5. **`maven.neoforged.net` 官方源直连**：本机不可达，实际使用 creeperhost 镜像；
   官方源路径的代码分支未在本环境命中。
6. **磁盘空间不足**、**多显示器/多 DPI 切换**、**系统时间异常** 等环境型故障未构造。
7. **Modrinth Mod 的实际安装**（下载文件到 `mods/`）：搜索已验证，但未对具体 Mod 执行安装下载；
   相关下载/校验/写入路径由下载引擎与 `.mrpack` 流程间接覆盖。
8. **NeoForge 实例中真实加载第三方 Mod**：仅加载了 NeoForge 自身，未安装外部 Mod 验证。

---

## 6. 已知问题

1. 在 1536×864 @125% 这类高 DPI 小屏上，早期版本固定窗口尺寸会超出屏幕；
   现已改为**默认最大化**并按显示器工作区夹取，但更小的屏幕仍可能显得拥挤。
2. 安装 Forge/NeoForge 时，安装器输出的中文路径在个别情况下可能显示为乱码
   （已为 Java ≥19 注入 `-Dstdout.encoding=UTF-8` 缓解）。
3. `verify` 默认按「大小 + 必要时摘要」校验资源，`--deep` 才会对全部资源做 SHA-1。
4. 未实现 Mojang 官方 Java 运行时自动下载，需要用户自备 Java。
