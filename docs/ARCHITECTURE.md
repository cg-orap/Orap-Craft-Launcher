# 架构与关键技术决策

本文记录架构选型、模块划分，以及那些「看起来能跑但很容易做错」的协议细节究竟如何实现。
每一条关键结论都对应 `src/Orion.Core` 中的实现与 `tests/Orion.Tests` 中的测试。

---

## 1. 技术栈决策（阶段 A）

### 1.1 候选方案比较

| 方案 | 优点 | 缺点 | 结论 |
| --- | --- | --- | --- |
| **C# / .NET 10 + WPF** | UI 框架随 .NET 提供，零第三方 UI 依赖；自包含单文件发布成熟；Windows 原生 DPI / 输入 / 文件系统支持最好；调试与打包链路最短 | 仅 Windows（本项目的目标就是 Windows） | **采用** |
| C# / .NET 10 + Avalonia | 跨平台、XAML 风格现代 | 需要引入多个 NuGet 包与额外运行时资产，单文件发布体积更大、踩坑更多，而本项目无跨平台需求 | 备选（保留迁移可能，Core 不依赖 UI） |
| Java + JavaFX（HMCL 路线） | 与 Minecraft 同生态，可直接用 Java 库 | 需要 JDK 工具链与 jpackage 打包，UI 生态较弱，无法复用 Windows 原生能力 | 否决 |
| Electron / Tauri（XMCL 路线） | Web 技术栈 UI 丰富 | 体积大、内存高、需要 Node/WebView2 运行时，与「高性能、单文件」目标冲突 | 否决 |

**决策**：`.NET 10` + `WPF`，发布 `win-x64` self-contained single-file。
理由：目标平台单一，WPF 是**成本最低且长期可维护**的路径；核心库 `Orion.Core`
是纯 `net10.0` 程序集，不含任何 WPF 引用，因此未来若要换 Avalonia 只需重写 `Orion.App`。

### 1.2 依赖策略

- 运行时**零第三方依赖**：只用 BCL（`System.Net.Http`、`System.Text.Json`、
  `System.IO.Compression`、`crypt32.dll` P/Invoke）。
- 测试期依赖：xUnit + `Microsoft.NET.Test.Sdk`（MIT / Apache-2.0），不进入发布产物。
- 不引入任何第三方 Minecraft 启动库：现有成熟库要么是 GPLv3（HMCL、Prism），
  要么是非自由许可（PCL），见 [RESEARCH-LICENSES.md](RESEARCH-LICENSES.md)。

---

## 2. 模块划分

```text
Orion.Core (net10.0, 无 UI 依赖)
├── Json/        版本清单与版本元数据模型（含 arguments 多态转换器）
├── Game/        规则求值、继承合并、仓库访问、库/本地库规划、本地库解压
├── Net/         HttpClient 工厂、镜像与容灾、下载引擎、速率统计
├── Install/     版本安装器、完整性校验与修复
├── Loaders/     NeoForge/Forge（安装器驱动）、Fabric/Quilt（profile 驱动）
├── Auth/        Microsoft OAuth → Xbox Live → XSTS → Minecraft；DPAPI 账户存储
├── Launch/      启动参数生成、进程托管、log4j 格式化、崩溃分析
├── Instances/   实例模型、目录隔离、导入
├── Mods/        Mod 描述符解析、Modrinth 客户端、.mrpack 导入导出
├── Java/        Java 检测与版本匹配
└── LauncherContext  组合根：GUI 与 CLI 共用同一套服务装配
```

UI 与核心通过 `LauncherContext` 单向依赖：UI 只调用 Core，Core 不认识 UI。
GUI 与 CLI 复用同一个 `LauncherContext`，因此「命令行能跑通」在很大程度等价于
「GUI 能跑通」——这也是本项目的验收策略之一。

---

## 3. 启动链路的关键实现细节

### 3.1 规则（rules）求值

Mojang 的 `rules` 是**顺序覆盖**语义：初始 `allowed = false`，逐条判断，
条件匹配的规则把结果写成 `action == "allow"`。因此「先 allow 再 deny」会得到拒绝。
实现：`Game/RuleEvaluator.cs`，测试：`RuleEvaluator` 相关 6 项。

### 3.2 继承（inheritsFrom）合并

`Game/VersionResolver.cs`：

- 从子版本向上收集链，最大深度 20，检测循环；
- 从根到子逐层覆盖标量字段；
- `libraries` 按 **group:artifact[:classifier]** 合并（不是按完整版本号）：
  子版本声明 `org.ow2.asm:asm:9.10.1` 时必须**替换**父版本的同名库，
  否则同一个类路径上出现两个版本，是 `NoSuchMethodError` 的经典来源；
- `arguments.game` / `arguments.jvm` **根在前、子在后**拼接，保证父版本的
  `-cp ${classpath}` 仍然生效；
- 老版本（1.13 之前）用 `minecraftArguments` 字符串，子版本整体替换而非拼接；
- `jar` 字段（老 Forge / OptiFine 使用）优先决定用哪个客户端 JAR。

测试：`VersionLogicTests`（合并、替换、拼接、legacy 字段）。

### 3.3 类路径与「谁提供游戏本体」

三种元数据形态都要处理：

1. **官方现代元数据**：`downloads.artifact` 显式给出 path/sha1/size/url；
2. **1.19+ 的本地库**：natives 不再用 `natives` 映射，而是作为普通库条目，
   Maven classifier 是 `natives-windows` / `natives-windows-x86` / `natives-windows-arm64`，
   而它们的 `rules` **只过滤 OS，不区分架构**。启动器必须自己按 CPU 架构挑选，
   否则 64 位 JVM 可能加载 32 位或 ARM 的原生库；`Game/NativeClassifier.cs` 负责此事；
3. **Maven 风格元数据**（Fabric/Quilt/部分 Forge 库）：只有 `name` + `url`，
   路径由坐标推导，摘要可选。

**现代 Forge/NeoForge 的特殊处理（踩坑记录）**：
NeoForge 的版本 JSON 传入 `-DignoreList=client-extra,${version_name}.jar`，
表示「启动器自己的客户端 JAR 不参与模块解析」，游戏本体由加载器提供的 patch 后
JAR 替代。如果仍把原版 `1.21.1.jar` 放进 classpath，会出现：

```text
java.lang.module.ResolutionException: Modules minecraft and _1._21._1
export package net.minecraft.util.profiling.jfr.event to minecraft
```

因此 `LaunchPlanner.LoaderManagesGameJar` 精确检测该 `ignoreList` 信号并省略启动器 JAR；
Fabric/Quilt 与 1.17 之前的 Forge 则仍然需要原版 JAR。
测试：`PlannerOmitsLauncherJarWhenLoaderOwnsTheGameJar`、`PlannerKeepsInheritedJarForFabricStyleProfiles`。

### 3.4 启动参数生成

`Launch/LaunchPlanner.cs` 是**纯函数**（无 IO、无进程启动），因此可以完整单测：

- 变量表覆盖 `auth_player_name`、`auth_uuid`、`auth_access_token`、`user_type`、
  `version_name`、`game_directory`、`assets_root`、`assets_index_name`、
  `natives_directory`、`launcher_name/version`、`classpath`、`library_directory`、
  `classpath_separator`、`resolution_width/height`、`quickPlay*` 等；
- 未知变量会被记录为警告并置空，而不是把 `${...}` 原样传给 Java；
- 功能开关（`is_demo_user`、`has_custom_resolution`、`has_quick_plays_support`）
  通过 `RuleContext.Features` 参与 `rules` 判定；
- 1.13 之前无 `arguments` 块时，补上历史上的 `-Djava.library.path` 与 `-cp`；
- log4j 配置通过 `${path}` 替换为本地文件路径。

### 3.5 进程托管与日志

- `GameLauncher` 启动 `java.exe`，重定向 stdout/stderr，逐行写入
  `<gameDir>/logs/orion-<version>-<时间>.log`；
- 启动器注入 `-Dfile.encoding=UTF-8`（Java ≥19 再加 `-Dstdout.encoding/-Dstderr.encoding`），
  避免中文路径/中文日志乱码；
- 1.12+ 的日志是 `<log4j:Event>` XML 片段，`GameLogFormatter` 把它折叠回
  `[HH:mm:ss] [thread/LEVEL]: message`（支持 CDATA 多行与 Throwable）；
- 进程退出后 `CrashAnalyzer` 结合退出码、日志特征与 `crash-reports/` 给出可读结论
  （内存不足、Java 版本过低、Mixin 失败、缺 DLL、显卡初始化失败……）。
- 主动结束的进程会标记 `KilledByLauncher`，不误报为崩溃。

---

## 4. 下载引擎（P3）

`Net/DownloadEngine.cs` 的设计要点：

| 需求 | 实现 |
| --- | --- |
| 有界并发 | `Parallel.ForEachAsync` + `MaxDegreeOfParallelism`（默认 8，可配 1–32） |
| Range 断点续传 | 先写 `<file>.part`；存在则发 `Range: bytes=N-`，服务器返回 206 时追加 |
| 不支持 Range 的服务器 | 返回 200 时**从头重写**该文件，绝不追加导致损坏 |
| 416 / 大小不符 | 删除 `.part` 后重试 |
| 指数退避 + 抖动 | `base * 2^attempt`，上限 15s，随机抖动 0–250ms；遵守 `Retry-After` |
| 永久错误快速失败 | 400/401/403/404/410 不再重试 |
| SHA-1 / SHA-256 校验 | 校验通过才 `File.Move (.part → target)`（同卷内原子替换） |
| 防止重复下载 | 同批内按目标路径去重，合并 URL 与摘要 |
| 主机健康度短路 | 传输层失败的主机被降权 5 分钟，避免每个文件都先等一次超时 |
| 可取消 / 可恢复 | 取消保留 `.part`，下次续传 |
| 失败可定位 | `DownloadReport.Failures` + 写入 `logs/download-failures-*.json` |

测试用 `TcpListener` 实现了一个受控 HTTP 服务（`tests/Orion.Tests/TestHttpServer.cs`），
覆盖：正常下载、Range 续传、忽略 Range、摘要不符、503 重试、404 快速失败、
跳过已存在文件、批内去重、损坏文件修复、取消、进度与速率、不可达主机。

---

## 5. 加载器安装（P1）

| 加载器 | 方式 | 判定「安装成功」的依据 |
| --- | --- | --- |
| **Fabric** | 抓取官方 profile JSON（`meta.fabricmc.net`）写入 `versions/<id>/<id>.json`，再下载其依赖 | profile JSON 落盘且能解析出 mainClass，依赖下载完成 |
| **Quilt** | 同上（`meta.quiltmc.org`） | 同上 |
| **NeoForge** | 下载官方 installer JAR，**预下载其 install_profile 里声明的全部依赖**，再执行 `--install-client <root>` | 安装器退出码 0 **且** 生成的版本 JSON 存在、含 mainClass，再补齐 assets |
| **Forge** | 同 NeoForge | 同上 |

**为什么必须预下载依赖**：NeoForge installer 自己单线程从 `maven.neoforged.net` 拉库，
在该主机不可达的网络里会直接失败（日志里是 `SocketTimeoutException: Connect timed out`）。
installer 的 `install_profile.json` 里带有每个库的 **SHA-1**，因此用我们自己的
并发+校验下载器先把它们放到 `libraries/` 下，installer 就只需执行处理器（binarypatcher 等）。
镜像只做传输替代，内容摘要不变。

installer 还要求目标目录「看起来像一个 Minecraft 安装」，否则报
`There is no minecraft launcher profile`；因此会写入一个最小的合法
`launcher_profiles.json`（只包含 profiles 容器与 settings，不伪造游戏数据）。

---

## 6. 账户与安全

- **微软登录**：OAuth 2.0 授权码 → Xbox Live 用户令牌 → XSTS → Minecraft 服务令牌 → 档案。
  XSTS 的 `XErr` 错误码会翻译成可操作的中文提示（无 Xbox 档案、地区不支持、需要成人验证、儿童账户）；
  refresh token 可换取新的成套令牌。
- **令牌落盘加密**：`Auth/AccountStore.cs` 用 Windows **DPAPI**（当前用户范围）加密
  access/refresh token，并在密文前加 `ORION1` 魔数避免把明文误当密文。
  测试会断言磁盘文件里**不包含**明文令牌。
- **日志脱敏**：启动命令行写入日志前会用 `GameLauncher.Redact` 去掉 access token。
- **离线账户**：UUID 使用 `MD5("OfflinePlayer:"+name)` 按 RFC 4122 v3 生成，
  与 Java `UUID.nameUUIDFromBytes` 的规范字符串**逐字符一致**（测试用独立计算出的
  已知值 `Notch → b50ad385-829d-3141-a216-7e7d7539ba7f` 断言）。
  离线账户的 `accessToken` 固定为 `0`，不会绕过在线模式验证。
- **TLS**：不存在任何关闭证书校验的开关或环境变量。

---

## 7. 实例与数据安全

- 共享 `assets/ libraries/ versions/`，每个实例独享 `instances/<id>/minecraft`
  （saves、mods、config、options.txt 互不干扰）。
- 导入现有 `.minecraft` / Prism 实例时**只注册、不写入**源目录，并标记 `IsExternal`；
  卸载外部实例只删除注册信息。
- 移除实例：整个目录 **移动** 到 `trash/<id>-<时间戳>`，从不 `Directory.Delete`；
  UI 会在包含存档时二次确认。
- 删除 Mod 同样移动到 `trash/mods/`。
- 整合包安装强制路径包含检查，`../` 越界条目会被跳过并记录警告。

---

## 8. 数据目录与配置

见 README 第 3 节。设置项包括：数据根目录、Java 路径、内存、并发、下载源、代理、
主题、语言、UTF-8 日志、微软客户端 ID、上次选中的账户与实例。

便携模式：EXE 同目录存在 `portable.txt` 时，数据根目录改为 `./data`。

---

## 9. 错误处理与可恢复性

| 场景 | 行为 |
| --- | --- |
| 网络中断 / 超时 | 重试 + 回退镜像；已下载部分保留，下次续传 |
| 文件损坏 | 摘要不符即删除重下；`verify --repair` 可全量校验并修复 |
| 安装取消 | 记录为「已取消」，`.part` 保留，重新安装继续 |
| 版本未安装就启动 | 首页/命令行都会先自动安装再启动 |
| Java 缺失 | 明确指出需要的版本并跳转到设置页 |
| 游戏崩溃 | 日志 + 退出码 + crash-report 三源分析，给出结论与证据 |
| 磁盘空间不足 | 下载失败会写入可定位的 JSON 报告（未做预检，见路线图） |
| 配置损坏 | 读取失败时回落到默认值并记录警告，不阻塞启动 |
