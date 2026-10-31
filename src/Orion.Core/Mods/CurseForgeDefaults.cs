namespace Orion.Core.Mods;

/// <summary>
/// 内置的 CurseForge API Key —— 分享给别人时用：别人解压即用，不用自己去申请。
///
/// ┌──────────────────────────────────────────────────────────────────────────┐
/// │  本地填入方法：把下面 ApiKey 的 "" 换成你自己的 Key，然后重新构建：        │
/// │      powershell -ExecutionPolicy Bypass -File build\verify.ps1 -DotnetRoot D:\dotnet │
/// │  留空（""）= 不内置，行为与之前完全一致：用户在「设置 → 网络与下载」里填。 │
/// └──────────────────────────────────────────────────────────────────────────┘
///
/// 三点必须知道的后果（写在这里，免得以后忘了）：
/// 1. **Key 会被打包进 exe**，任何人用字符串扫描都能挖出来；因此它只能当"分发专用"，
///    不要用你主账号的 Key，最好去 console.curseforge.com 单独申请一把。
/// 2. **共用一把 Key = 共用每日配额**，用的人越多越容易打到上限（届时 CurseForge 请求返回 429）。
/// 3. **提交到 git 就等于公开**：如果这个仓库要推公开地址，填了真 Key 后请勿提交本文件，
///    或者改用「用户在设置里填」的方式（把这里留空）。
///
/// 用户自己在设置里填的 Key 永远优先于内置 Key（见 <see cref="Resolve"/>）。
/// </summary>
public static class CurseForgeDefaults
{
    /// <summary>内置的分发用 Key；留空表示不内置。</summary>
    public const string ApiKey = "";

    /// <summary>是否内置了 Key（界面据此提示"已内置，可不填"）。</summary>
    public static bool HasBuiltInKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>用户填的 Key 优先；没填时回退到内置 Key；都没有则 null。</summary>
    public static string? Resolve(string? userKey) => Resolve(userKey, ApiKey);

    /// <summary>可测试版本：显式传入内置 Key。</summary>
    public static string? Resolve(string? userKey, string? builtInKey)
    {
        if (!string.IsNullOrWhiteSpace(userKey)) return userKey.Trim();
        if (!string.IsNullOrWhiteSpace(builtInKey)) return builtInKey.Trim();
        return null;
    }
}
