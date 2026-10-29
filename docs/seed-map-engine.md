# 种子地图引擎（cubiomes）

工具箱里的「种子地图」现在跑的是 **cubiomes** —— 一个 C 语言写的 Minecraft Java 版世界生成模拟库
（MIT 许可，[Cubitect/cubiomes](https://github.com/Cubitect/cubiomes)）。它也是 Cubiomes-Viewer、
Amidst 一类地图工具和多数启动器内置种子地图的底层实现。

## 组成

| 位置 | 作用 |
|---|---|
| `third-party/cubiomes/` | vendor 的 cubiomes 源码（含 `tables/`，MIT，见 `LICENSE`） |
| `src/Orion.Core/Native/orap_cubiomes.c` | 桥接层：把 cubiomes 包成 4 个 C ABI（版本表 / 渲染瓦片 / 单点群系 / 出生点） |
| `build/build-native.ps1` | 编译出 `src/Orion.Core/Native/OrapCubiomes.dll` |
| `src/Orion.Core/Native/OrapCubiomes.dll` | 编译产物（随仓库提交，免去用户装编译器） |
| `src/Orion.Core/World/CubiomesNative.cs` | C# 侧 P/Invoke 封装 + 版本映射 + 缺库时的降级探测 |
| `src/Orion.Core/World/BiomeCatalog.cs` | cubiomes 英文群系名 → 中文名 |

C# 里 `CubiomesNative.Available == false`（DLL 缺失/损坏）时，画布自动退回原来的
`SimplifiedBiomeMap`（5 通道噪声近似），界面会显示"简化模型"，不会崩。

## 重新编译

```powershell
# 需要 C 编译器：D:\tools\mingw64\bin\gcc.exe（WinLibs 解压即用），或 zig.exe
powershell -NoProfile -ExecutionPolicy Bypass -File build\build-native.ps1
```

编译命令等价于：

```
gcc -O2 -fwrapv -static -static-libgcc -shared -o OrapCubiomes.dll \
    orap_cubiomes.c ../../../third-party/cubiomes/*.c -I ../../../third-party/cubiomes -lm
```

两个必须保留的参数：

- `-fwrapv`：cubiomes 依赖 Java 式的二补数溢出，去掉会算错群系。
- `-static -static-libgcc`：否则 DLL 依赖 `libwinpthread-1.dll`，在其他机器上加载会报
  "找不到指定的模块"（这个坑我们踩过一次）。

## 能力与边界

| 项 | 状态 |
|---|---|
| 版本 | 1.6.4 – 1.21.4（`MC_1_21_WD`），共 18 个档位；比引擎新的版本按次版本号就近映射 |
| 维度 | Overworld（画布使用）；Nether / End 已有 ABI，界面暂未接 |
| 群系 | **精确**（原版算法） |
| 史莱姆区块 | 精确（自研 JavaRandom，黄金值测试对照真实 JDK） |
| 出生点 | cubiomes 的统计估算（`getSpawn`），尚未显示在界面上 |
| 结构（村庄/要塞/神殿…） | cubiomes 支持，尚未接界面 |
| 地形高度/等高线 | cubiomes 不做方块级地形，只到群系 |

## 验证

`tests/Orion.Tests/CubiomesNativeTests.cs`：

- 加载后版本表非空、包含 1.21.x 与 1.18.2；
- **cubiomes README 的示例**：种子 262 在 1.18 的 (0,63,0) 必须是 `mushroom_fields`（id 14）——
  这条断言证明跑的是原版算法而不是近似；
- 渲染瓦片全不透明且颜色 ≥ 3 种；
- 出生点落在合理范围；
- 群系名中英映射。

DLL 缺失时这些用例只断言"降级原因非空"，不会让 CI 失败。
