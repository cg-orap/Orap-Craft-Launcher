# 微软正版登录

本启动器支持三种微软登录方式。**推荐设备代码流**：不需要把任何链接复制回启动器，
因此不会出现“授权码过期”，这也是主流启动器的做法。

## 为什么需要你自己注册一个 Azure 应用？

微软身份平台要求每个应用有自己的「客户端 ID」。旧版 Live Connect 的公共 ID
（`00000000402b5328`）只能走已经老化的 `login.live.com` 流程，无法使用设备代码流。
为遵守微软的服务条款，本项目**不冒用其他启动器的应用 ID**，而是由使用者免费注册一个。

注册一次，长期有效，约 2 分钟。

## 注册步骤（2026 版）

1. 打开 <https://portal.azure.com> 并用你的微软账户登录（个人账户即可，不需要订阅/付款）。
2. 搜索并进入 **Microsoft Entra ID** → 左侧 **应用注册** → **新注册**。
3. 名称随便填（例如 `Orion Launcher`）；**受支持的账户类型**选择
   **「仅个人 Microsoft 账户」**（或「任何组织目录中的账户和个人 Microsoft 账户」）。
4. **重定向 URI** 可选：平台选 **公共客户端/本机(移动和桌面)**，填 `http://localhost`。
   （只使用设备代码流时可以不填；用「浏览器自动回调登录」则必须填。）
5. 注册完成后，在应用概览页复制 **应用程序(客户端) ID**（形如 `12345678-1234-1234-1234-123456789012`）。
6. 进入 **身份验证** 页面，把 **允许公共客户端流** 设为 **是**，保存。
7. 把复制的客户端 ID 粘贴到启动器首页「微软正版账户」下方的输入框，点击 **保存**。

> 提示：如果「允许公共客户端流」没有开启，设备代码流会返回 `AADSTS7000218`；
> 如果应用 ID 填错或应用不在该目录，会返回 `AADSTS700016`。启动器会把这两类错误翻译成
> 对应的修复步骤。

## 三种登录方式

| 方式 | 需要的配置 | 特点 |
| --- | --- | --- |
| **设备代码登录（推荐）** | 客户端 ID + 允许公共客户端流 | 启动器给出短代码，你在任意浏览器（甚至手机）输入即可；启动器自动轮询完成登录 |
| **浏览器自动回调登录** | 客户端 ID + 重定向 URI `http://localhost` | 打开浏览器，登录后自动回到启动器；全程无需操作 |
| **旧版粘贴登录（备用）** | 无需配置（内置公共 ID） | 老式 `login.live.com` 流程，需要把浏览器地址栏链接复制回启动器；该流程已被微软弱化，成功率不稳定 |

## 常见错误

| 错误 | 含义 | 处理 |
| --- | --- | --- |
| `AADSTS700016` | 应用 ID 不存在 / 目录不对 | 确认复制的是应用注册页的「应用程序(客户端) ID」，账户类型包含个人账户 |
| `AADSTS7000218` | 未允许公共客户端流 | 身份验证 → 允许公共客户端流 = 是 |
| `invalid_grant ... code has expired` | 旧版粘贴方式的授权码过期/已被使用 | 改用设备代码登录 |
| `authorization_declined` | 你在浏览器里取消了授权 | 重新登录 |
| `not_entitled` | 该微软账户没有 Minecraft: Java Edition | 需要正版账户才能启动在线游戏 |
| Xbox 认证失败 | 账户没有 Xbox 档案 / 地区或年龄限制 | 先在 <https://www.xbox.com> 登录一次创建档案 |

## 技术说明

- 设备代码流：`POST {authority}/oauth2/v2.0/devicecode` 取 `user_code`，随后按
  `grant_type=urn:ietf:params:oauth:grant-type:device_code` 轮询 `/oauth2/v2.0/token`，
  正确处理 `authorization_pending` / `slow_down` / `expired_token` / `authorization_declined`。
- 回环登录：本地监听 `http://localhost:<随机端口>`（HTTPS 客户端无需任何证书配置），
  校验 `state` 防 CSRF，收到 `code` 后立即换取令牌。
- 两条流程拿到微软访问令牌后，都走同一条链：
  Xbox Live `user.auth.xboxlive.com/user/authenticate` → XSTS
  `xsts.auth.xboxlive.com/xsts/authorize` → Minecraft
  `api.minecraftservices.com/authentication/login_with_xbox` → `/minecraft/profile`。
- 令牌只保存在内存与被 DPAPI 加密的 `accounts.json` 中；日志里只记录流程名与错误码。
