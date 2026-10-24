# 第三方组件与引用说明

本项目自己的代码以 MIT 许可发布（见根目录 `LICENSE`）。
下面是发布产物与开发过程中涉及的第三方组件/服务。

## 1. 随发布产物一起分发的组件

| 组件 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| .NET Runtime（`Microsoft.NETCore.App`） | 10.x | MIT | 应用运行时（self-contained 打包进单文件 EXE） |
| Windows Desktop Runtime（`Microsoft.WindowsDesktop.App`，WPF） | 10.x | MIT | 桌面 UI 运行时 |
| **Microsoft.Web.WebView2 SDK** | 1.0.4314.35 | Microsoft Software License Terms（可免费用于应用并随应用分发 SDK 二进制） | 内嵌浏览器，用于微软账户登录 |
| Windows `crypt32.dll`（DPAPI） | 操作系统组件 | 随 Windows 授权 | 账户令牌加密 |

**关于 WebView2 运行时**：SDK 只包含接口层；实际的浏览器运行时（Evergreen Runtime）由
Windows / Microsoft Edge 提供，本项目**不打包、不重分发**运行时本体。若目标机器缺少该运行时，
启动器会给出提示并允许改用设备代码登录。

.NET 的许可证文本：<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
（MIT）。使用 self-contained 发布时这些运行时以二进制形式随 EXE 一起分发，
其版权与许可归属 Microsoft 及 .NET 贡献者。

## 2. 仅用于开发/测试的组件

| 组件 | 许可证 | 用途 |
| --- | --- | --- |
| xUnit.net | Apache-2.0 | 单元测试框架 |
| Microsoft.NET.Test.Sdk | MIT | 测试宿主 |
| coverlet.collector | MIT | 覆盖率收集（未强制使用） |
| .NET SDK 10.0.402 | MIT | 构建工具链 |

这些依赖**不会**进入发布目录（`dist/`）。

## 3. 运行期访问的第三方服务

| 服务 | 用途 | 说明 |
| --- | --- | --- |
| Mojang / Microsoft 启动器接口（`launchermeta.mojang.com`、`piston-meta.mojang.com`、`piston-data.mojang.com`、`libraries.minecraft.net`、`resources.download.minecraft.net`） | 版本清单、元数据、客户端、依赖库、资源 | 公开的官方接口，只读取 |
| Microsoft 身份平台 / Xbox Live / `api.minecraftservices.com` | 正版账户登录 | 官方 OAuth 2.0 授权码流程，不绕过授权 |
| `meta.fabricmc.net`、`meta.quiltmc.org` | Fabric / Quilt 版本与 profile | 官方元数据服务 |
| `maven.neoforged.net`、`maven.minecraftforge.net`、`files.minecraftforge.net` | NeoForge / Forge 安装器与库 | 官方分发渠道 |
| `api.modrinth.com`、`cdn.modrinth.com` | Mod 搜索与下载 | 公开 API，遵守 User-Agent 要求 |
| `bmclapi2.bangbang93.com`（可选） | 下载镜像 | 仅作传输替代，内容仍按官方 SHA-1 校验 |
| `maven.creeperhost.net`（可选镜像） | NeoForge Maven 镜像 | 同上 |

## 4. 参考过的开源项目（**未复制代码**）

| 项目 | 许可证 | 参考内容 |
| --- | --- | --- |
| PCL2（Meloong-Git/PCL） | 《PCL 分发有限许可》（非开源，禁止实质功能再实现） | **仅黑盒行为参照**，未阅读/复制源码 |
| HMCL（HMCL-dev/HMCL） | GPL-3.0 | 未复制；仅了解其产品能力范围 |
| Prism Launcher | GPL-3.0（品牌素材 CC BY-SA 4.0） | 未复制；实例管理模式的公开知识 |
| XMCL（Voxelum/x-minecraft-launcher） | MIT | 未复制代码（语言不同）；概念层面参考 |
| BMCLAPI | 仓库已不可公开访问（404），无法取得许可 | 仅作为可选 HTTP 镜像使用 |

详细审计结论见 [`docs/RESEARCH-LICENSES.md`](docs/RESEARCH-LICENSES.md)。

## 5. 声明

- 本项目与 Mojang Studios、Microsoft、Fabric、Quilt、NeoForge、Forge、Modrinth、
  BMCLAPI 均无隶属或背书关系。
- "Minecraft" 是 Mojang Studios 的商标。本项目不包含任何 Minecraft 游戏文件，
  游戏内容在用户机器上按其正版授权从官方服务下载。
- 本项目不提供任何绕过正版验证、破解或规避付费的功能。
