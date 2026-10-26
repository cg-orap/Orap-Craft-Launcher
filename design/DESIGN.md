# Orap Craft · UI 概念稿（React + TS + Tailwind）

## 1. 设计 Token

| Token | 值 | 用途 |
|---|---|---|
| `--bg` | `#0F1115` | 应用底色（深岩灰，非纯黑） |
| `--surface` | `#171A21` | 卡片 / 侧栏 |
| `--surface-elevated` | `#1E222B` | 浮层、抽屉、右键菜单 |
| `--surface-hover` | `#242935` | 悬浮/选中底色 |
| `--border` | `rgba(255,255,255,.08)` | 统一描边 |
| `--text-primary` | `#E8EAED` | 主文字（非纯白） |
| `--text-secondary` | `#9AA3B2` | 次要文字 |
| `--text-muted` | `#667085` | 弱化/元信息 |
| `--primary` | `#6FBF73` | 苔藓绿，仅用于主动作 |
| `--primary-hover` | `#7FD184` | 主动作 hover |
| `--accent` | `#D9A85C` | 强调/徽章 |
| `--info` | `#67C7D8` | 同步中、信息 |
| `--danger` | `#E06C75` | 错误、删除、回滚 |
| `--success` | `#7BC98B` | 已同步、校验通过 |
| `--warning` | `#E5B567` | 警告、冲突 |
| `--radius-sm/md/lg` | `8 / 12 / 16 px` | 标签/卡片/大容器 |
| `--shadow-soft` | `0 8px 24px rgba(0,0,0,.28)` | 卡片投影 |
| 动效 | `150–250ms`，`cubic-bezier(.2,.8,.2,1)` | 状态变化、悬浮、页面切换 |

字体：正文 `Inter / Noto Sans SC`；版本号与日志 `JetBrains Mono`；**像素字体仅用于 Logo 与小插画**。

## 2. 信息架构

```
标题栏(40) Logo · 当前实例 · 窗口按钮
├ Rail(72)  主页 实例 下载 模组 备份 日志 设置 … 底部账户
├ Sidebar(280, 可折叠) 随 Rail 变化
└ Main(≤1200, px 28, gap 16)
    主页      启动控制台 / 性能（内存·CPU·GPU·状态）/ 最近实例（纵向列表）/ 最近备份·存储占用·公告
    实例      详情 Tabs：概览·模组·存档·日志·快照·设置
    下载      搜索 + 来源(Modrinth/CurseForge) + 类型筛选 + 详情抽屉
    日志      级别筛选+搜索+自动滚动 | 等宽日志流 | 崩溃报告归档
    备份      时间线 + 云同步状态条
    设置      外观/Java/下载源/备份/联机/高级（3 套预设主题）
```

## 3. 线框（文字版）

主页
```
┌ 实例封面 92 ┬ 名称 · 加载器标签 · 版本 ──────────────┐
│             │ MC · 模组数 · 最后游玩                  │
│             │ ✓账户 ✓Java ✓内存                     │
│             │ [启动游戏]  ← 唯一主视觉（绿实色）        │
│             │ 启动中：阶段文案 + 进度条 + %           │
│             │ 运行中：[游戏运行中] 查看日志 结束进程    │
└─────────────┴ 右侧：Java/内存/版本 · 云同步状态 ──────┘
[最近游玩 ×4]  [自动备份(时间线摘要)] [P2P] [公告]
```

日志页
```
标题 + 打开文件夹 / 导出 / 清空
[级别 ALL/ERROR/WARN/INFO/DEBUG]  [等宽日志流 flex-1]  [崩溃报告归档]
[搜索 · 自动滚动 · 统计]           ERROR 行浅红底高亮      复制报告/打开文件夹
```

## 4. 组件树

```
App
├ Shell
│  ├ TitleBar(毛玻璃 40px)
│  ├ Rail(72) + Tooltip
│  ├ Sidebar(280, 折叠) + FooterAccount
│  └ Main(≤1200)
├ pages/Home
│  ├ LaunchConsole（Cover / Meta / PrimaryAction(3 态) / StageProgress / EnvPanel / SyncChip）
│  ├ RecentGrid(4) · BackupSummary · P2PEntry · AnnouncementList
├ pages/Instance
│  ├ InstanceHeader（封面/标签/操作）
│  ├ TabBar
│  ├ SnapshotTimeline（创建/对比/回滚 + 二次确认）
│  ├ ModList（状态：正常/待更新）
│  └ UsageBars
├ pages/Logs
│  ├ FilterPanel（级别/搜索/自动滚动/统计）
│  ├ LogStream（等宽、级别高亮、单选复制）
│  └ CrashArchive（时间/实例/文件/复制/打开）
└ pages/Placeholder（下载/模组/备份/设置，可点击占位）
```

## 5. 状态

- 启动键三态：`idle / starting(阶段+进度) / running(查看日志·结束进程)`
- 云同步三态：`已同步 / 同步中(脉冲点) / 冲突`
- 危险操作（删除实例、回滚）红色描边 + 二次确认浮层
- 右下角「演示状态」面板仅用于评审，切换启动/同步状态

## 6. 运行

```powershell
cd design
pnpm install     # 已执行
pnpm dev         # http://127.0.0.1:5273
```

> 中文目录下 vite 的 URL 解析会乱码，因此开发时用 `D:\designui`（指向 design 的目录联接）作为工作目录。
## 7. 第二批页面

| 页面 | 内容 |
|---|---|
| **资源市场** | 搜索 + 来源切换（Modrinth / CurseForge）+ 类型胶囊；420ms 骨架屏；长条结果行；点击从右侧滑出详情抽屉（Esc / 点遮罩关闭） |
| **设置** | 五组（外观 / Java / 下载源 / 备份 / 高级）；外观含 3 套主题、背景图、标题栏毛玻璃、动效、列表密度 |
| **模组** | 按实例聚合：搜索、分类筛选、仅显示可更新、启停开关（`aria-pressed`）、批量更新入口、禁用后不写入启动参数的说明 |
| **备份与云同步** | 云同步状态条三态（已同步 / 同步中带进度 / 冲突带"保留本地·使用云端"）；备份时间线（时间·大小·触发方式，恢复二次确认）；保留策略与占用 |

## 8. 三套主题

| id | 名称 | 类型 | 背景 / 卡片 | 主色 | 气质 |
|---|---|---|---|---|---|
| `rock`（默认） | 深岩 | 暗 | `#0F1115` / `#171A21` | `#6FBF73` 苔藓绿 | 深色岩灰，夜间与长时间游玩 |
| `frost` | 霜白 | **浅色** | `#EEF1F5` / `#FFFFFF` | `#1F8A5B` 墨绿 | 白卡片 + 冷灰底，白天层次清晰 |
| `paper` | 暖纸 | **浅色** | `#F3EDE2` / `#FDFAF4` | `#9B5F2A` 赭石 | 米白纸感，书桌工具箱 |

浅色主题不是简单反色：新增语义 token `--tint / --tint-strong / --border-strong / --code-bg / --scrim / --on-primary`，把原来散落的 `bg-white/[0.04]`、`border-white/10`、`bg-black/20` 全部替换为 token，因此浅色下 hover 底色、分隔线、输入框、遮罩、主按钮上的文字都自动正确。切换写入 `localStorage`，也可用 `?theme=frost|paper` 预览。
