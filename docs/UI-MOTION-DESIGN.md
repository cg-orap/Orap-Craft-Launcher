# Orap Craft Launcher — 界面与动效设计方案（v1 提案）

> 目标：把「点一下就硬切」的感觉换成**有节奏、有因果**的动效。动效不是装饰，而是告诉用户
> "发生了什么、从哪来、到哪去、能不能点"。本文是**方案**，不含实现。

---

## 0. 现状审计（这一版实际有什么）

| 已有动效 | 位置 | 参数 |
|---|---|---|
| 页面切换淡入 + 上滑 | `MainWindow.xaml.cs` `AnimatePage()` | 220 ms、8 px、CubicEase Out，只看 `CurrentPage` 变化 |
| 侧栏选中竖条滑动 | `MainWindow.xaml.cs` `AnimateRail()` | 200 ms、CubicEase Out |
| 侧栏折叠/展开 | `MainWindow.xaml` `SidebarHost.Width` | 220/240 ms |
| 分段控件滑块 | `SegmentedControl.cs` | 200 ms、CubicEase Out |
| 列表逐条淡入 | `Animations/StaggerIn.cs` | 每项延迟 40 ms、220 ms 内完成 |
| 启动按钮按压 | `HomeView.xaml` | 按下 0.98 缩放（80 ms）/ 回弹 140 ms |
| 首页卡片入场 | `HomeView.xaml` | 淡入 180 ms + 上滑 10 px |
| 主色调/主题切换 | `App.ApplyTheme` | **瞬时**，无过渡 |

**结论**：底座是好的（曲线、距离、只动 Transform/Opacity 这些原则已经在了），但覆盖面很窄 ——
只有 **页面切换、侧栏、分段控件、首页** 有动效，其余全是瞬时切换：

- 90% 的按钮**按下没有反馈**（`BaseButton` 模板只有 disabled 透明度）
- 所有 **hover 都是颜色瞬变**（没有过渡，5 px 的位移都没有）
- **详情页 / 覆盖层**（Modrinth 详情、CurseForge 详情、拖拽 overlay）是"突然出现"
- **列表首屏**（模组 376 行、下载结果、资源市场）没有入场；`StaggerIn` 只挂了 2 处
- **进度条**数值跳变、**数字**（下载量/统计）跳变
- **主题切换**白闪一下
- **模态对话框**（登录、确认）无入场
- 没有 **减少动效** 开关（无障碍）

---

## 1. 动效语言（一套参数，全局复用）

### 1.1 时长阶梯

| 名称 | 时长 | 用在哪 |
|---|---|---|
| `Instant` | 60 ms | 按下反馈（按下要"立刻"） |
| `Fast` | 120 ms | hover、开关、checkbox、图标状态 |
| `Base` | 180 ms | 按钮回弹、卡片 hover、tooltip |
| `Page` | 220 ms | 页面/页签切换、详情进出 |
| `Slow` | 320 ms | 主题切换、抽屉、大面积展开 |
| `Stagger` | 每项 24 ms | 列表入场（上限 8 项，最多 192 ms） |

### 1.2 曲线

| 名称 | 曲线 | 用途 |
|---|---|---|
| `Enter` | `CubicEase(EaseOut)` | 入场、展开、出现 |
| `Exit` | `CubicEase(EaseIn)` | 退出、收起（比入场快 20%） |
| `Soft` | `QuadraticEase(EaseOut)` | 颜色/透明度过渡 |
| `Press` | `QuadraticEase(EaseOut)` | 按压回弹 |
| `Emphasis` | `BackEase(Amplitude=0.15)` | 仅用于"成功/完成"这类需要一点弹性的时刻 |

> 禁止 `ElasticEase` / `BounceEase` / 长回弹：桌面工具类软件的动效应该"稳"，弹跳会显得廉价。

### 1.3 位移与缩放

| 场景 | 数值 |
|---|---|
| 页面进入位移 | 8–12 px（纵向），**不超过 16 px** |
| 卡片/行入场位移 | 6 px |
| 按下缩放 | 0.97（按钮）、0.99（整卡） |
| 覆盖层入场 | 0.98 → 1.00 + 淡入 |
| 抽屉滑入 | 100% → 0（横向，320 ms） |

### 1.4 动效分层原则

1. **只动 `RenderTransform` / `Opacity` / `Effect`**，不动 `Width/Height/Margin/Padding`（会触发布局，64 行以上必卡）。
2. **每层最多一个运动维度**：横向滑入就不要再缩放；淡入就不要再旋转。
3. **因果一致**：入场从"用户点击的位置/方向"来。点侧栏第 4 项，新页面从下方 8px 淡入（已有），
   侧栏指示器同步滑过去（已有）——两点要在**同一帧**开始。
4. **层级差异**：越"远"的元素动得越慢越大（页面 220ms/8px），越"近"的元素动得越快越小（按钮 60–180ms/0.97）。

---

## 2. 组件级规格

### 2.1 按钮（全站统一，改 `BaseButton` 模板即可覆盖 90%）

| 状态 | 变化 | 时长/曲线 |
|---|---|---|
| hover | 背景色过渡（当前是瞬变）+ 边框/文字色过渡 | 120 ms / Soft |
| 按下 | `ScaleTransform` 0.97 + 亮度 -6% | 60 ms / Press |
| 松开 | 回 1.0 | 180 ms / Press（Out） |
| 禁用 | 透明度 0.45（现状） | 150 ms |

> 实现要点：`BaseButton` 模板加 `RenderTransformOrigin=(0.5,0.5)` + 共享 `ScaleTransform`，
> 用 `EventTrigger(RoutedEvent=PreviewMouseLeftButtonDown/Up)` 而不是改模板触发器（模板触发器不能动画）。

**特殊按钮**：
- **主要按钮（启动游戏）**：按下 0.97，松开 1.0 + 阴影从 0 → 8px 模糊（180 ms）。
- **危险按钮（删除）**：按下 0.97，悬停边框由红转深红（120 ms）；**不做**抖动/闪烁。
- **图标按钮（标题栏、关闭）**：hover 背景 120 ms；关闭按钮 hover 变红（120 ms）。

### 2.2 导航（侧栏）

| 交互 | 动效 |
|---|---|
| 悬浮 | 背景 120 ms 淡入 + 图标 1.05 倍（120 ms） |
| 选中 | 指示条 200 ms 滑动（已有）；图标 1.0→1.06→1.0（180 ms）；选中项背景 180 ms |
| 折叠 | 宽度 240 ms（已有）；折叠完成后文字淡出 90 ms、图标居中 150 ms |
| 折叠态 hover | **tooltip 淡入 120 ms + 右移 4 px**（现在是瞬时出现） |

### 2.3 页面 / 页签切换

| 场景 | 动效 |
|---|---|
| 一级页面切换（侧栏 10 项） | 旧页快速淡出 90 ms（不做位移，比"先完全消失再进"更连贯）→ 新页 220 ms 淡入 + 8 px 上滑（已有，补上"旧页退出") |
| 二级页签（内容管理三页、模组筛选、日志级别） | 内容区 160 ms 淡入 + 6 px 上滑；滑块 200 ms（已有） |
| 下载中心类型切换（游戏版本/整合包/模组/光影/材质包） | 列表 160 ms 淡入，保留滚动位置（不重播列表 stagger，避免闪烁） |
| 详情页进出（Modrinth / CurseForge） | 进入：240 ms 淡入 + 12 px 上移 + 0.985 缩放；返回列表：180 ms 反向（Exit 曲线，更快） |
| 版本选择器（游戏版本 tab） | 版本行 20 ms 交错淡入 |

### 2.4 列表 / 卡片

| 场景 | 动效 |
|---|---|
| 首屏列表入场 | 每项 24 ms 交错、6 px 上滑、180 ms；**最多 8 项**（第 9 项起同时出现，避免等 2 秒） |
| 重新搜索 / 换页 | **不播**交错（只播一次整块 120 ms 淡入）——反复交错会晕 |
| 行 hover | 背景 120 ms；行内主图标 1.03 倍（120 ms） |
| 行选中 | 背景 120 ms + 左侧 2 px 强调条 120 ms 滑入 |
| 卡片 hover（首页/概览） | 上移 2 px + 阴影加深（150 ms），**不缩放整卡** |
| 列表滚动 | 不播任何动效；滚动惯性交给系统 |

### 2.5 反馈 / 状态

| 场景 | 动效 |
|---|---|
| 进度条数值 | 数值变化用 200 ms 补间（`DoubleAnimation` 到目标值），不用瞬变；完成时从强调色变成功色（180 ms） |
| 数字（下载量/模组总数） | 首次出现时 300 ms **count-up**（只对首屏统计，不对每行） |
| 状态文字变化 | 旧文字 90 ms 淡出 → 新文字 120 ms 淡入（不位移，避免抖动） |
| 忙碌指示 | 按钮内文字换成 3 点跳动（600 ms 循环，`ReduceMotion` 时改为静态"…"） |
| 骨架屏 | 列表首次加载（无缓存）显示 5 行 1200 ms 循环微光（`LinearGradientBrush` 平移）；有缓存时**不显示** |
| Toast / 活动项 | 从右上 180 ms 滑入 + 淡入；消失 140 ms 淡出 |
| 保存成功 | 按钮文字"保存设置" → 1200 ms 显示"已保存 ✓"再变回（淡入淡出各 120 ms） |

### 2.6 覆盖层 / 对话框

| 场景 | 动效 |
|---|---|
| 确认对话框 | 遮罩 160 ms 淡入（0 → 0.5 黑）；卡片 200 ms 0.96→1 + 淡入 |
| 登录窗口 | 同上（窗口级：`Opacity` 0→1，220 ms） |
| 拖拽整合包 overlay | 遮罩 140 ms 淡入；提示文字 1.0↔1.02 呼吸（1.6 s 循环，ReduceMotion 关闭） |
| Tooltip | 120 ms 淡入 + 4 px 位移（现在是瞬时） |
| 右键菜单 | 120 ms 淡入 + 4 px 上移（WPF 默认菜单动画可保留） |

### 2.7 主题 / 主色切换

- 现状：瞬时换 `ResourceDictionary`，会"白闪一下"。
- 方案：**整窗截图式淡入**太贵；改用 —— 换主题前把 `Window` 的 `Opacity` 降到 0.92（80 ms），
  换完资源再回 1.0（180 ms）。视觉上像"呼吸一下"，成本极低。
- 强调色（10 种）切换：只有颜色变化，用 120 ms 过渡（`SolidColorBrush.Color` 动画，注意画刷不能冻结）。

---

## 3. 无障碍与性能约束（硬性）

1. **减少动效开关**：设置 → 外观与主题 → 「减少动效」。开启后：
   - 所有位移/缩放/交错动画关闭，仅保留 ≤120 ms 的透明度过渡；
   - 进度条与状态文字直接赋值；
   - 骨架屏改为静态占位。
   建议同时读取系统 `SystemParameters.ClientAreaAnimation`（Windows 的"显示动画"设置）。
2. **性能预算**：单帧脚本时间 ≤ 4 ms；一次交互触发的动画元素 ≤ 30 个；列表入场 ≤ 8 项。
3. **不动的场景**：键盘连发、滚动、窗口缩放、虚拟化列表滚动中、批量刷新（一次 > 50 项）。
4. **共享冻结资源**：所有 `EasingFunction`/`Duration` 放 `Themes/Motion.xaml` 并 `Freeze()`，
   避免每个控件 new 一份（现有代码里每个动画都 `new CubicEase`，有优化空间）。
5. **动画对象可复用**：`RenderTransform` 必须每个实例自建（模板里的会被冻结），
   现有 `SegmentedControl`/`AnimateRail` 已经踩过这个坑，其余处按同样写法。

---

## 4. 架构落地

### 4.1 新增 `Themes/Motion.xaml`（资源字典）

```xml
<Duration x:Key="Motion.Instant">0:0:0.06</Duration>
<Duration x:Key="Motion.Fast">0:0:0.12</Duration>
<Duration x:Key="Motion.Base">0:0:0.18</Duration>
<Duration x:Key="Motion.Page">0:0:0.22</Duration>
<CubicEase x:Key="Motion.Enter" EasingMode="EaseOut" />
<CubicEase x:Key="Motion.Exit" EasingMode="EaseIn" />
<QuadraticEase x:Key="Motion.Soft" EasingMode="EaseOut" />
<QuadraticEase x:Key="Motion.Press" EasingMode="EaseOut" />
```

### 4.2 新增 `Animations/Motion.cs`（代码侧统一入口）

```csharp
public static class Motion
{
    public static bool ReduceMotion { get; set; }        // 由设置驱动
    public static Duration Pick(Duration normal);        // ReduceMotion 时返回 0
    public static void FadeIn(UIElement element, double from = 0, int ms = 180);
    public static void SlideFadeIn(UIElement element, double dy = 8, int ms = 220);
    public static void PressScale(FrameworkElement element, bool down);
    public static void CrossFade(UIElement outgoing, UIElement incoming);
    public static DoubleAnimation Tween(double to, int ms, IEasingFunction ease);
}
```

约定：**页面/视图代码不再自己写 `DoubleAnimation`**，一律调 `Motion.*`，保证参数一致、也能一处关闭。

### 4.3 新增附加行为（XAML 里一行挂上）

| 行为 | 用法 | 作用 |
|---|---|---|
| `anim:StaggerIn`（已有） | `anim:StaggerIn.IsEnabled="True"` | 列表入场，补上"上限 8 项"和 ReduceMotion 判断 |
| `anim:HoverLift` | `anim:HoverLift.IsEnabled="True"` | 卡片 hover 上移 2 px + 阴影 |
| `anim:PressFeedback` | 挂在 Button 上 | 避免每个按钮模板写一遍 |
| `anim:TransitionOnChange` | 绑 `DataContext` 或属性 | 内容变化时淡入（状态文字、数字、进度） |

### 4.4 落地顺序（每步都能单独验收）

| 阶段 | 内容 | 影响面 | 预估 |
|---|---|---|---|
| **P0** | `Motion.xaml` + `Motion.cs` + ReduceMotion 设置与开关 | 全局基础设施 | 0.5 天 |
| **P1** | 按钮按压/悬停统一（`BaseButton` + 危险/主要/图标按钮） | 全站按钮 | 0.5 天 |
| **P2** | 页面退出 + 详情/覆盖层进出 + 页签淡入 | 10 个页面、2 个详情 | 1 天 |
| **P3** | 列表入场统一（模组/资源/存档/版本/日志，含 8 项上限）+ 行 hover/选中 | 6 处列表 | 0.5 天 |
| **P4** | 状态类：进度条补间、状态文字交叉淡化、数字 count-up、保存成功反馈 | 首页/下载/设置 | 0.5 天 |
| **P5** | 骨架屏 + 忙碌三点 + toast 滑入 | 列表首次加载、活动中心 | 0.5 天 |
| **P6** | 主题切换呼吸、tooltip 淡化、对话框入场 | 全局 | 0.5 天 |

合计 **4 天**左右，可分批交付；每批都能独立发布。

---

## 5. 验收方法（不是"看着还行"）

1. **逐帧截图对比**：用 `--page` 路由 + 定时截图（现有截图管线可复用），
   在 0 / 60 / 120 / 200 ms 各抓一帧，确认位移/透明度确实在变而不是瞬变。
2. **性能**：`dotnet-trace` 抓 UI 线程，确认单次交互脚本时间 ≤ 4 ms、无布局抖动；
   大列表（376 行）滚动时 FPS ≥ 55。
3. **ReduceMotion 回归**：开关打开后，用同样的截图确认**没有任何位移**（只有透明度或直接切换）。
4. **一致性检查**：全局搜索 `new DoubleAnimation`，除 `Motion.cs` 外应清零。

---

## 6. 反模式（明确不做）

- ❌ 页面切换用横向大位移 / 3D 翻转 / 百叶窗：桌面工具不需要，且会让人等。
- ❌ 每次列表刷新都重播交错入场（晕，且慢）。
- ❌ 给所有元素加缩放 hover（列表行缩放会让文字发虚）。
- ❌ 动画期间禁用交互（除了必要的一次性遮罩），点击应当立即生效。
- ❌ 超过 320 ms 的入场动画（体感"卡"）。
- ❌ 用 `ElasticEase` / 无限循环的非进度类动画（除了骨架屏微光与忙碌点）。

---

## 7. 已拍板（2026-11）

| 项 | 决定 |
|---|---|
| 整体气质 | **稳健**：Cubic/Quadratic 曲线，不用 Elastic/Bounce；只有"完成/成功"时刻允许 0.15 振幅的 `BackEase` 回弹 |
| 动效强度 | **标准**：位移 8–12 px、交错 24 ms、总量不减免 |
| 氛围动效 | **要**：启动按钮点击脉冲、安装/保存成功反馈、活动项滑入 |

## 8. 实施进度

| 阶段 | 状态 | 提交 | 说明 |
|---|---|---|---|
| P0 基础设施 | ✅ 完成 | 本批 | `Animations/Motion.cs`（时长阶梯、冻结曲线、FadeIn/SlideFadeIn/PressScale/HoverLift/Pulse）+ 设置里的「减少动效」（写 `settings.json`、启动即应用、同时读系统"显示动画"） |
| P1 按钮 | ✅ 完成 | 本批 | `BaseButton` 模板：悬停上浮 1px（120ms）、按下 0.97 缩放（60ms）、松开 180ms 回弹（0.15 振幅）→ 所有主/次/低强调/危险按钮一次性生效；启动按钮点击脉冲 |
| P2 页面/详情/页签 | ✅ 完成 | 本批 | 新增 `ContentTransition` 附加行为（值变化即淡入+滑入，Reducemotion 归零），替换手写的 `AnimatePage`；页面 12px/240ms，详情 12px/240ms，页签内容 6px/200ms（内容管理 3 处、版本详情 6 处、存档 2 处、工具箱 3 处、Mods 筛选、日志） |
| P3 列表/行 | ✅ 完成 | 本批 | 交错入场：24ms / 8 项上限 / 6px / 减少动效直显（模组、存档、版本、资源包列表）。行 hover 与选中改为**遮罩层 120ms 透明度过渡**：全局 `ListBoxItem` 样式一处 + 模组行模板一处，不再瞬间换底色 |
| P4 状态类 | ✅ 完成 | 本批 | `AnimatedValue.To`（进度条 200ms 补间，已挂下载/版本/首页三条使用率）、`TextFade.Text`（状态行文字变化淡入，5 个页面）、`CountUp.To`（数字滚动，备用）。**保存成功反馈未做**（需要先定"成功提示"的落点） |
| P5 骨架屏/忙碌/toast | ⏳ 待做 | — | 首次加载骨架屏、按钮内三点、活动项滑入 |
| P6 主题/tooltip/对话框 | 🟡 部分完成 | 本批 | 主题切换「呼吸」（换字典后整窗 0.85 → 1，220ms）、tooltip 淡入 120ms、登录窗口入场（淡入 + 10px）。**主题呼吸尚未实测**（主题下拉是 ComboBox，UIA 取不到可选项，需要换成鼠标坐标点击） |

**P1 的实测证据**（不是"看起来动了"）：按住设置页「保存设置」时，按钮强调色区域宽度
111 px → **107 px**（≈96.4%，即 0.97 缩放），松开回到 111 px —— 用 `PrintWindow` 截窗口 +
像素包围盒量出来的。

### P2 实测证据（逐帧）

用 `PrintWindow` 连拍窗口（不受遮挡影响），把每帧与"稳定帧"做像素平均绝对差：

| 场景 | 切换前 vs 稳定 | 切换后逐帧 |
|---|---|---|
| 侧栏切页（设置 → 首页） | **18.14** | +1ms 18.14 → +101ms 9.88 → +132ms 3.48 → +163ms 1.69 → +195ms 0.85 → +226ms 0.51 → **+257ms 0.42**；两张稳定帧之间 **0.00** |
| 打开 CurseForge 详情 | **22.08** | +0ms 22.03 → +39ms 15.70 → +69ms 11.56 → +100ms 9.66 → +130ms 8.84 → +161ms 8.57 → +193ms 8.24（残余是详情异步加载的图标/描述，不是动画） |

如果是"硬切"，第一帧就该等于 0；现在是**单调收敛**，说明 240ms 的缓动确实在跑。

### P3 实测证据（列表交错入场）

存档页刚进去时连拍，与稳定帧比较：8.79 → 2.96 → 1.01 → 0.44 → **0.14（约 274ms）**，
之后保持 0.14–0.17（页面自身的静态差异）。交错 + 淡入的总时长符合 8×24ms + 220ms 的设计。

### 关于"行 hover 动画"的一次失败验证（如实记录）

给全局 `ListBoxItem` 和模组行都加了 120ms 的遮罩层过渡后，我用屏幕连拍测量行 hover：
静止 → 稳定总变化 11.39，但**每一帧（+1ms 起）都与稳定帧完全一致**，看起来像瞬时切换。
把过渡时长临时改成 **1.2 秒**再测，结果仍然"第一帧即完成"——说明这个视觉变化**不是**我加的遮罩动画产生的，
而是行模板里别处的高亮在起作用（这一版主题为 Snow + Blue，`SurfaceHover` 与行底色非常接近，
遮罩几乎不可见）。结论：

- 遮罩过渡的代码是正确写法（与已验证的按钮/页面机制相同），但**在当前主题下它几乎看不见**；
- 行 hover 的"可见反馈"来自模板里既有的高亮，那部分仍是瞬时；
- 下一轮要做的不是继续加动画，而是**先统一行高亮的来源**：把模板内各自为政的 hover 高亮收敛到
  一个遮罩层，并让遮罩色与主题的 `SurfaceHover` 拉开一点对比（例如叠一层 `AccentBrush` 6% 透明度）。
