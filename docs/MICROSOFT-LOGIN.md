# 微软正版登录

本启动器提供 **4 种**微软登录方式。**默认的「用浏览器登录」**在启动器内嵌浏览器中完成：
选择账户并同意后自动登录，**不需要复制任何链接**，与 HMCL / PCL 的做法一致。

![内嵌浏览器登录](images/login-embedded.png)

## 关键说明：Xbox Live 与「v2.0 应用 ID」

微软的旧版流程（`login.live.com`，内置公共客户端 ID）**不需要任何配置**，但微软已对第三方
弱化了该流程：它签发的令牌常常**不含 Xbox Live 授权范围**，于是会出现

```
微软登录成功，但 Xbox Live 拒绝了该令牌（HTTP 401）
```

这不是账号问题（同一账号在 HMCL / PCL 能登录，因为它们使用新的 v2.0 流程）。
**一旦你填写了自己的 Azure 应用 ID，启动器的「用浏览器登录」会自动切换到 v2.0 流程**
（`XboxLive.signin` 权限 + `http://localhost` 回调），窗口和体验完全一样，仍然无需复制。

| 场景 | 建议 |
| --- | --- |
| 想零配置先试试 | 直接点「用浏览器登录」（旧版流程）。若提示 Xbox 401 → 按下节注册一次应用 ID |
| 要稳定登录正版 | 注册一次应用 ID（约 2 分钟，免费），之后「用浏览器登录」即走 v2.0，长期有效 |

## 注册应用 ID（一次性，2 分钟）

1. <https://portal.azure.com> → **Microsoft Entra ID** → **应用注册** → **新注册**。
2. 名称随意；**受支持的账户类型**选「**仅个人 Microsoft 账户**」（或包含个人账户的选项）。
3. **重定向 URI**：平台选「**公共客户端/本机(移动和桌面)**」，填 **`http://localhost`**
   （v2.0 浏览器登录必须；仅用设备代码流可留空）。
4. 注册后复制 **应用程序(客户端) ID**。
5. **身份验证** → **允许公共客户端流 = 是** → 保存。
6. 启动器首页「微软正版账户」下方粘贴该 ID → **保存**。

之后点「用浏览器登录」，窗口地址会变成 `login.microsoftonline.com`，登录流程与之前完全相同。

## 四种方式对比

| 方式 | 需要配置 | 复制链接 | 说明 |
| --- | --- | --- | --- |
| **① 用浏览器登录（默认）** | 无；**配了应用 ID 更好** | 否 | 内嵌 WebView2 打开微软登录页；配置应用 ID 时自动改走 v2.0（`XboxLive.signin`），否则走旧版 |
| ② 设备代码登录 | 自备应用 ID | 否 | 启动器给短代码，任意浏览器（含手机）输入；启动器轮询完成 |
| ③ 浏览器自动回调登录 | 自备应用 ID + `http://localhost` | 否 | 用系统浏览器，本地监听端口接回调 |
| ④ 旧版粘贴登录（备用） | 无 | 是 | 老式 `login.live.com`；授权码几分钟有效，成功率低，仅兜底 |

## 常见错误与处理

| 现象 | 含义 | 处理 |
| --- | --- | --- |
| **Xbox Live 401** | 旧版令牌没有 Xbox Live 范围 | 按上文注册应用 ID 并保存，再点「用浏览器登录」；或改用设备代码登录 |
| Xbox Live 400 | 账户侧问题（多为无 Xbox 档案） | 先在 <https://www.xbox.com> 用同一账户登录一次；检查地区；未成年人需家庭批准；新档案有 1–2 分钟延迟 |
| `invalid_grant ... code has expired` | ④ 旧版粘贴的授权码过期/已用 | 改用 ① 用浏览器登录 |
| `AADSTS700016` | 应用 ID 不存在或目录不匹配 | 确认复制的是「应用程序(客户端) ID」，账户类型含个人账户 |
| `AADSTS7000218` | 未允许公共客户端流 | 身份验证 → 允许公共客户端流 = 是 |
| `AADSTS50011` | 未登记 `http://localhost` 回调 | 身份验证 → 添加平台「公共客户端/本机」→ 重定向 URI `http://localhost` |
| `not_entitled` | 该账户没有 Minecraft: Java Edition | 需要正版账户 |
| 「未检测到 WebView2 运行时」 | 系统缺少内嵌浏览器组件 | 安装 WebView2 运行时，或改用 ② 设备代码登录 |

## 技术说明

- **① 内嵌浏览器**：WPF + `Microsoft.Web.WebView2`；`NavigationStarting` 中匹配
  `redirect_uri` 并取消跳转，直接从 URL 取 `code`（并校验 `state`）。
  - 旧版：`redirect_uri=https://login.live.com/oauth20_desktop.srf`
  - v2.0（已配置应用 ID）：`redirect_uri=http://localhost:<随机端口>/`，
    在浏览器内拦截，**不需要真的开监听端口**
  - `NewWindowRequested` 交给系统浏览器，避免在启动器内弹窗
- **Xbox Live 票据**：依次尝试 `d=<token>`、`t=<token>`、`<token>` 三种 `RpsTicket` 形态
  （对应 MSA / Azure AD / 无前缀），第一个成功即用；每次尝试都会记录状态码与响应体
  （截断、无凭据）。实测旧版流程返回 `401`、响应体为空，据此在提示中区分 401（范围问题）
  与 400（账号问题）。
- **② 设备代码流**：RFC 8628，正确处理 `authorization_pending` / `slow_down` /
  `expired_token` / `authorization_declined`。
- **③ 回环**：本地 `TcpListener` 监听 `http://localhost:<随机端口>`，校验 `state` 防 CSRF。
- **令牌链**（四种方式共用）：Xbox Live `user.auth.xboxlive.com/user/authenticate`
  → XSTS `xsts.auth.xboxlive.com/xsts/authorize`
  → `api.minecraftservices.com/authentication/login_with_xbox` → `/minecraft/profile`。
- **令牌存储**：内存 + Windows DPAPI 加密的 `accounts.json`；账户记住创建它的流程，
  刷新令牌时使用同一 authority。日志只记录流程名、错误码与授予的作用域，绝不记录令牌。
