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
已通过! - 失败: 0，通过: 115，已跳过: 0，总计: 115
```

覆盖范围（每项均为真实断言，不是「编译通过」）：

| 测试类 | 数量 | 覆盖内容 |
| --- | --- | --- |
| `VersionLogicTests` | 24 | 规则顺序覆盖、OS/arch/features 判定、正则容错、Maven 坐标与分类器架构过滤、继承合并（去重/替换/参数拼接/legacy 字段）、log4j 格式化、参数替换与命令行拆分、类路径选择（含 NeoForge `ignoreList` 分支） |
| `DownloadEngineTests` | 13 | 本地真实 HTTP：Range 续传、服务器忽略 Range、摘要不符、503 重试、404 快速失败、跳过已存在、批内去重、损坏修复、取消、进度/速率、不可达主机、镜像映射 |
| `AppBehaviourTests` | 22 | 离线 UUID 规范值、账户 DPAPI 加密存储（断言磁盘无明文）、授权码解析、实例隔离、移除进回收站、`.minecraft` 导入、Prism 导入、INI 解析、设置往返与损坏容错、TOML Mod 解析、Mod 兼容性、崩溃分析、JSON 容错、版本号数值排序、日志脱敏 |
| `MicrosoftCallbackParsingTests` | 20 | 回调粘贴的各种真实形态：完整/无协议头链接、带引号换行、百分号编码、裸授权码、`error=` 回调识别、参数名误判防护、授权 URL 参数 |
| `MicrosoftAuthFlowTests` | 11 | 用桩 HTTP 跑完整认证链（OAuth→XBL→XSTS→Minecraft→profile）、请求体形状（`d=<token>`、`XBL3.0 x=<uhs>;<xsts>`、Bearer）、令牌刷新、`invalid_grant`（授权码过期）、`invalid_client`、未拥有游戏、无刷新令牌、错误文案映射 |
| `MrPackTests` | 4 | 导出内容与「不包含存档」、可选包含存档、Fabric 包索引解析、非整合包返回 null |
| 合计 | **115** | 全部通过 |

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
> orap launch 1.21.1 --offline Tester --memory 2048 --kill-after 45
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
> orap launch neoforge-21.1.264 --offline Tester --memory 3072 --kill-after 75
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
dist/win-x64/OrapCraftLauncher.exe   59.05 MB   self-contained, single-file
dist/win-x64/cli/orap.exe        self-contained, single-file
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

### 3.10 微软登录回调粘贴（UI 自动化验证）

微软登录在浏览器中完成，回调链接必须能可靠地粘回启动器。用 UI 自动化
（UIAutomation + 真实键盘/剪贴板）实测两条路径：

| 操作 | 结果 |
| --- | --- |
| 在回调输入框内按 **Ctrl+V** | 输入框内容等于粘贴的完整回调链接（`Ctrl+V LANDED: True`） |
| 清空后点击「粘贴回调链接」按钮 | 输入框内容等于剪贴板中的完整回调链接（`BUTTON PASTE LANDED: True`） |

同时覆盖的解析与提示行为（19 项单元测试）：

- 完整链接、无协议头链接、`code=…` 片段、单独的授权码；
- 被引号/尖括号/换行包裹的粘贴内容；
- 百分号编码的 code（`%2B`、`%3D`）会被正确还原；
- `error=access_denied&error_description=…` 会给出可读失败原因而不是“无法识别授权码”；
- 其它参数值里出现 `code` 字样不会被误判为授权码；
- 粘贴后输入框下方实时显示「已识别授权码：M.C50A_BAY.2.U…（共 N 个字符）」。

> 本轮修复针对的问题：早期版本的回调输入框只在「浏览器登录」成功后才显示，
> 且文本框没有右键菜单，导致用户无法把地址栏链接粘贴进来。现在输入框常驻、
> 支持右键粘贴/按钮粘贴/Ctrl+V，并可用「复制登录链接」把授权链接复制到任意浏览器。

### 3.11 授权码过期（`invalid_grant`）的处置

首次实测正版登录时，微软返回：

```
invalid_grant: The provided value for the 'code' parameter is not valid. The code has expired.
```

授权码是**一次性且只有几分钟有效期**的，因此「登录 → 复制 → 点击」的延迟和
「失败后用同一个码重试」都必然失败。已改为：

- 点击「在浏览器中登录」后，启动器**每秒侦测剪贴板**（最多 4 分钟）：
  一旦复制到含授权码的回调链接就**立即自动提交**，无需再点按钮；
- 提供一键「从剪贴板登录」（读取 + 提交一步完成）；
- 记录已使用的授权码，重复提交会被本地拒绝并提示重新登录；
- `invalid_grant`（授权码过期/已用）被翻译成可操作文案，并自动清空已失效的输入，
  避免用户反复点击同一个失效码。

UI 自动化实测（真实网络，使用无效码触发 `invalid_grant`）：

| 场景 | 结果 |
| --- | --- |
| 粘贴回调链接 → 点「从剪贴板登录」 | 显示「登录已超时：授权码只能使用一次且只有几分钟有效期…请重新点击「在浏览器中登录」」；输入框被清空 |
| 点「在浏览器中登录」→ 仅复制回调链接 | 无需任何点击，启动器自动提交并给出同一提示（`auto submit worked: True`） |

同一验证在**已发布的单文件 EXE** 上重跑，结果一致。

> 说明：上述验证使用的是伪造授权码，目的就是让微软返回真实的 `invalid_grant`，
> 从而验证错误处置与自动侦测链路；因此这**不等于**「真实账户登录成功」已通过。

### 3.12 改用主流登录方式（设备代码流 / 回环回调）

旧版 `login.live.com` 流程对第三方应用已明显弱化（授权码换令牌一直被拒绝），
因此新增与主流启动器等价的两条**不需要复制链接**的登录链路（详见
[`MICROSOFT-LOGIN.md`](MICROSOFT-LOGIN.md)）：

| 机制 | 端点 | 验证状态 |
| --- | --- | --- |
| 设备代码流 | `login.microsoftonline.com/consumers/oauth2/v2.0/devicecode` + `/token` | 请求形状已用**真实端点**验证（返回结构化 `AADSTS700016`，说明 URL 与请求体正确，只缺一个已注册的应用 ID）；轮询状态机（`authorization_pending` / `slow_down` / 成功 / `authorization_declined` / `expired_token`）由桩 HTTP 全覆盖 |
| 回环回调 | `/oauth2/v2.0/authorize` + `http://localhost:<随机端口>` 本地监听 | **端到端本地实测**：启动监听器 → 用真实 HTTP 请求访问回调地址 → 正确解析 `code`/`state`、返回中文落地页、`state` 校验与超时路径均有测试 |
| 旧版粘贴（备用） | `login.live.com` | 保留；新增「解码/原始」两种授权码形态各尝试一次 |

真实端点探测记录：

```
POST https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode
  client_id=11111111-2222-3333-4444-555555555555 & scope=XboxLive.signin offline_access
→ HTTP 400 {"error":"unauthorized_client","error_description":"AADSTS700016: Application with identifier ... was not found ..."}
```

该响应正是「应用未注册」时应有的结果，证明启动器发出的请求格式、作用域与端点都正确；
把真实注册的客户端 ID 填入后即可获得设备代码。`AADSTS700016` / `AADSTS7000218` /
`consent` 等错误也都已被翻译成具体修复步骤（单测覆盖）。

界面侧：首页新增「设备代码登录」「浏览器自动回调登录」，未配置客户端 ID 时直接给出
可操作提示而不是抛错（UI 自动化实测按钮存在且提示正确）。

### 3.13 内嵌浏览器登录（与 HMCL / PCL 一致的免配置方式）

在启动器内嵌入 **Microsoft Edge WebView2**，打开微软授权页；当页面跳转到
`https://login.live.com/oauth20_desktop.srf?code=…` 时在 `NavigationStarting` 中取消跳转并取出授权码，
因此**用户无需注册 Azure 应用、无需复制任何链接**，选择账户并同意即自动登录。

实测记录（发布版单文件 EXE，未设置 `DOTNET_ROOT`）：

```
[auth] webview2-runtime version=155.0.4283.62
[auth] webview2-navigating host=login.live.com
[auth] webview2-navigation-ok
```

| 检查项 | 结果 |
| --- | --- |
| 本机 WebView2 运行时 | 已安装 `155.0.4283.62`（Windows 11 自带） |
| 内嵌窗口能否打开真实登录页 | ✅ 截图为微软官方「登录 / 继续使用 Minecraft」页面 |
| 单文件发布是否包含 WebView2 依赖 | ✅ `dist/win-x64` 只有一个 `OrapCraftLauncher.exe`（59.6 MB），原生加载器随单文件自解压 |
| 发布版 EXE 实测 | ✅ 窗口打开并成功导航（日志见上） |
| 重定向拦截逻辑 | ✅ 单元测试覆盖 `IsRedirectTo` 的正例/反例（同域不同路径、仿冒路径、大小写、尾斜杠） |

修复的问题：首版把 WebView2 控件初始设为 `Collapsed`，导致 `EnsureCoreWebView2Async`
拿不到窗口句柄而卡住；改为控件常驻可见、用遮罩层显示加载状态后正常。

同时补强了 **Xbox Live 环节的诊断**：实测日志显示曾有真实尝试通过令牌交换却在 XBL 被拒
（`xbl_failed`，微软返回 `400` 且响应体通常为空）。现在会把状态码与响应体（截断、无凭据）
写入日志与错误说明，并给出按序排查步骤（先到 xbox.com 创建 Xbox 档案、检查地区、家庭批准、
新档案生效延迟），同时有单元测试锁定该行为。

### 3.14 真实账号登录失败定位：XBL 401（旧版流程缺 Xbox 作用域）

用户用**正版账号**（在 PCL/HMCL 可正常登录）在本启动器登录后，界面提示 Xbox Live 拒绝了令牌。
日志给出了决定性证据：

```
2026-10-24 11:08:31 [INFO ] [auth] browser-login-captured length=584
2026-10-24 11:08:32 [WARN ] [auth] xbl-rejected status=401 detail=empty
2026-10-24 11:08:32 [WARN ] [auth] browser-login-rejected code=xbl_failed
```

- OAuth 授权码抓取成功（584 字符）→ 令牌交换成功（否则会是 `invalid_grant`）→
  **`user.auth.xboxlive.com` 返回 401**（票据完全无法验证），而不是 400（账号侧问题）。
- 结论：旧版 `login.live.com` 流程签发的令牌**缺少 Xbox Live 授权范围**；这与账号本身无关
  （同一账号在 HMCL/PCL 正常，因为它们使用 v2.0 流程 + `XboxLive.signin` 作用域）。

据此做了三件事：

1. **票据形态自愈**：`RpsTicket` 依次尝试 `d=<token>`（MSA）、`t=<token>`（AAD）、`<token>`，
   任一成功即用；每次尝试都记录前缀与状态码。
2. **作用域可见**：令牌响应中的 `scope` 现在会写入日志，因此“到底授予了什么权限”可直接看到。
3. **v2.0 搬进内嵌浏览器**：配置了应用 ID 后，「用浏览器登录」自动改走
   `login.microsoftonline.com` v2.0 + `XboxLive.signin` + `http://localhost:<随机端口>`
   （在 WebView2 内部拦截重定向，不需要真的监听端口），窗口与操作体验完全不变。
4. **错误分诊**：401 → 明确告知是旧版流程的范围限制并给出“填应用 ID 后同一个窗口即可”的路径；
   400 → 给出账号侧排查步骤；`AADSTS50011`（未登记 localhost 回调）等也有专门文案。

对应单元测试：401/400 两种状态的分诊文案、票据前缀回退、现代码交换路由。



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

1. **微软正版登录的真实账户联调**：没有可用于测试的 Microsoft 账户，也不应在自动化流程中创建账户。
   已用桩 HTTP 跑通**整条认证链**（OAuth→Xbox Live→XSTS→Minecraft→profile、令牌刷新、
   各类错误映射，共 11 项测试），并实测了回调粘贴与授权码过期/自动侦测链路；
   但**「用真实账户走到登录成功」仍未验证**。
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

### 3.15 登录与主界面重构（仅保留内嵌浏览器登录）

按需求把登录收敛为**一种**方式，并把界面结构调整为「一个居中的启动按钮 + 可自定义背景」：

| 变更 | 实现 | 验证 |
| --- | --- | --- |
| 只保留内嵌浏览器登录 | 删除设备代码流与回环回调（含其 UI、命令与测试）；登录窗口内嵌 WebView2 打开微软登录页并在 `NavigationStarting` 拦截回调 | UI 自动化实测：窗口打开 → 点击「用浏览器登录」→ 日志 `browser-login-start flow=legacy host=login.live.com` + `webview2-navigation-ok`，截图显示微软官方登录页 |
| 登录单独成窗 | 新 `LoginWindow`：左侧账户列表（含离线账户增删）、右侧内嵌浏览器、底部状态与操作；浏览器按需创建 | 截图 `docs/images/login-window.png` |
| 主界面仅留启动按钮 | `HomeView` 只剩背景、居中大按钮、状态行与「结束游戏」 | 截图 `docs/images/page-home.png` |
| 自定义背景图片 | `LauncherSettings.BackgroundImagePath` + 右键菜单 + 「设置 → 主界面外观」；图片以 `UniformToFill` 铺满并加暗色蒙版保证可读性 | 实测：写入一张 1600×900 测试图后启动，背景正确渲染（`docs/images/home-background.png`） |
| 账户置于侧栏首位 + 皮肤头像 | 侧栏第一项为账户卡片（皮肤头 + 名称 + 类型），点击打开登录窗口；头像由档案 `skinUrl` 下载皮肤并**本地裁出 8×8 头部**（叠加帽子层、最近邻放大），缓存到 `<数据目录>/cache/skins` | 实测缓存文件 `cache/skins/<uuid>.png`（243 B）生成，侧栏与登录列表均显示真实皮肤头 |

过程中修掉的两个真实缺陷：

1. `AccountStore` 的持久化记录没有映射 `skinUrl` / `authFlow` —— 皮肤 URL 与登录流程在保存后丢失
   （这也会导致重启后用错误的端点刷新令牌）。已补上映射，并有单元测试覆盖。
2. `BitmapImage` 使用 `StreamSource` 时若设置 `IgnoreImageCache`，`EndInit()` 会抛
   `ArgumentNullException("key")`（缓存键来自不存在的 URI），导致皮肤永远解码失败；
   且 `RenderTargetBitmap` 不能在工作线程使用。改为纯像素合成（BGRA 叠加 + `BitmapSource.Create`），
   线程安全且像素更锐利。

测试数从 138 调整为 **136**：删除了设备代码流与回环监听器的用例，新增 v2.0 端点换取授权码、
`skinUrl` 落库、授权码重定向 query 解析等用例。