# 参考项目许可证审计（阶段 A）

本文记录对 5 个参考仓库的**实际审计结果**：许可证文本、由此产生的义务，
以及本项目最终决定「能用什么、不能用什么」。

审计方式：下载各仓库源码归档（`codeload.github.com`，本机到 `github.com` 网页不可达但归档下载可用），
读取 `LICENSE` / `LICENCE` / `COPYING` 文件原文。PCL 的许可为 GBK 之外的中文文本，
已按 UTF-8 原文转存于 [`docs/research/pcl-licence-decoded.txt`](research/pcl-licence-decoded.txt)。

| 项目 | 许可证 | 是否可复制代码 | 本案处置 |
| --- | --- | --- | --- |
| **PCL2**（Meloong-Git/PCL） | 《PCL 分发有限许可》+《PCL 存储库合理使用指南》——**非开源** | ❌ 否 | **完全不参考其源码**，只把它当作「产品行为」层面的黑盒参照 |
| **HMCL**（HMCL-dev/HMCL） | **GPL-3.0** | ⚠️ 可以，但会传染：链接/衍生必须整体 GPLv3 并公开源码 | **不复制任何源码**，避免 GPL 传染 |
| **Prism Launcher** | **GPL-3.0**（`COPYING.md` 声明 GPLv3 + 混合第三方声明；`program_info/LICENSE` 为 CC BY-SA 4.0，仅限 logo/品牌） | ⚠️ 同上 | **不复制任何源码**，不复制其图标/品牌素材 |
| **XMCL**（Voxelum/x-minecraft-launcher） | **MIT** | ✅ 可以（保留版权与许可声明） | 许可证兼容，但它是 TypeScript/Electron 实现，对本 C# 项目无可直接复用代码；仅在概念层面参考（实例、任务模型） |
| **BMCLAPI**（BangBang93/BMCLAPI） | 仓库 `api.github.com/repos/BangBang93/BMCLAPI` 返回 **404**（未公开/已迁移），无法取得许可证文本 | ❓ 不明 | **不复用其代码**；仅把它当作**可选下载镜像**使用（公开 HTTP 服务），并保留「官方优先」策略 |

## 1. PCL2 的关键限制（原文要点）

`docs/research/pcl-licence-decoded.txt` 中的要点：

- 除《PCL 分发有限许可》明确授予的权利外，开发者保留全部权利；
- 「轻度使用」（仅参考极小部分内容）需：署名、不得与 PCL 混淆；
- **「重度使用」**明确定义为「参考存储库中较大量的内容，以制作或重新实现 PCL 的部分实质功能
  （例如启动或下载 Java 版 Minecraft，下载或管理 Mod 等）」——**正好就是本项目的目标**；
- 重度使用的义务包括：必须以 `Plain Craft Launcher (PCL)` 开头命名、公开源码、
  实现「正版购买劝导」等。这些义务与本项目「独立、原创命名、MIT 许可」的定位冲突。

**结论**：本项目**没有**阅读或参考 PCL 的源代码，也没有复制其任何素材。
凡是 PCL 也有类似功能（版本下载、启动、Mod 管理），我们的实现均来自
Mojang/Microsoft 公开协议文档与本项目的独立设计。

## 2. HMCL / Prism（GPLv3）的处置

GPLv3 对「链接/衍生」有传染性。为了保证本项目可以用 MIT 许可发布、且不强迫使用者开源，
我们：

- **不复制** 任何 HMCL / Prism 源文件（包括改名、翻译、改写结构）；
- **不链接** 其任何库；
- 协议细节（版本 JSON 字段、`arguments` 结构、`inheritsFrom` 语义、`-DignoreList` 行为等）
  来自 **Mojang 的版本元数据本身**（数据格式不是代码），以及可观察的运行时行为；
- UI 布局与视觉全部原创（配色、组件、排版均由本项目自行设计）。

## 3. 第三方服务条款

| 服务 | 用途 | 条款要点与本项目做法 |
| --- | --- | --- |
| `launchermeta.mojang.com` / `piston-meta` / `piston-data` | 版本清单与元数据、客户端 JAR | Mojang 公开的启动器接口；只读取，不高频轮询（清单缓存 15 分钟） |
| `libraries.minecraft.net` / `resources.download.minecraft.net` | 依赖库与资源 | 同上，按元数据中的 SHA-1 校验 |
| Microsoft 身份平台 / Xbox Live / `api.minecraftservices.com` | 正版登录 | 使用公开的 OAuth 2.0 授权码流程；**不**绕过授权；令牌仅保存在本机并用 DPAPI 加密；不记录令牌 |
| `meta.fabricmc.net` / `meta.quiltmc.org` / `maven.neoforged.net` / `maven.minecraftforge.net` | 加载器元数据与安装器 | 只下载官方发布的元数据与安装器，且不绕过其分发方式 |
| `api.modrinth.com` | Mod 搜索与下载 | 公开 API，遵守其 User-Agent 要求；按 API 返回的 SHA-1 校验；不抓取网页 |
| CurseForge | — | **未接入**：其 API 需要授权 Key，不做未授权抓取 |
| BMCLAPI（`bmclapi2.bangbang93.com`） | 可选镜像 | 仅作为**传输替代**（官方源失败时回退或用户主动选择）；内容仍按官方摘要校验；不用于绕过任何授权/付费/限制 |

## 4. 本项目的依赖与其许可证

| 依赖 | 用途 | 许可证 | 是否进入发布产物 |
| --- | --- | --- | --- |
| .NET 10 / WPF 运行时 | 应用运行时 | MIT | 是（self-contained 打包） |
| xUnit、Microsoft.NET.Test.Sdk、coverlet | 单元测试 | Apache-2.0 / MIT | 否 |
| Windows DPAPI（`crypt32.dll`） | 令牌加密 | 操作系统 API | 是（系统组件调用） |

> 说明：`net10.0` 目标框架与 WPF 的运行时文件由 .NET SDK 提供，其许可证为 MIT；
> 单文件发布产物中会包含这些运行时二进制，随附许可证见 `THIRD-PARTY-NOTICES.md`。

## 5. 审计结论

1. 本项目**没有**复制任何参考项目的源代码或素材，因此不承担 GPLv3 或 PCL 许可的衍生义务。
2. 许可证兼容的 XMCL（MIT）因语言/技术栈差异未产生可复用代码。
3. 所有协议实现来自公开规范与实际可观察行为，并在 `docs/ARCHITECTURE.md` 中说明设计依据。
4. 本项目以 MIT 发布（见根目录 `LICENSE`）。
