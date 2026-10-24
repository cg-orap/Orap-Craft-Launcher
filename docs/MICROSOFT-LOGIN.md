# 微软正版登录

登录只有**一种方式**：在启动器内的独立登录窗口里打开微软官方登录页，选择账户并同意后自动完成。
**不需要复制任何链接，也不需要设备代码。**

![登录窗口](images/login-window.png)

## 使用

1. 点击左侧栏顶部的账户卡片（显示头像与账户名）。
2. 在弹出的「登录账户」窗口中点击右下角 **用浏览器登录**。
3. 窗口右侧会加载微软官方登录页；输入账户、完成验证并同意后，窗口自动完成登录并回到账户列表。
4. 账户会出现在左侧列表中并被选中，关闭窗口即可开始游戏。

首次打开登录窗口时只会读取本地账户（不联网）；点击「用浏览器登录」才会创建内置浏览器。

## 关于 Azure 应用 ID（可选，但建议配置）

启动器内置一个公共客户端 ID，可以零配置登录；但微软对这套旧流程签发的令牌可能缺少
Xbox Live 授权范围，表现为登录后提示「Xbox Live 拒绝了该令牌（HTTP 401）」。

若遇到该提示，花两分钟注册一个自己的应用 ID 即可，之后仍是同一个窗口、同一套操作：

1. <https://portal.azure.com> → **Microsoft Entra ID** → **应用注册** → **新注册**。
2. 名称随意；**受支持的账户类型**选「仅个人 Microsoft 账户」。
3. **重定向 URI**：平台选「公共客户端/本机(移动和桌面)」，填 `http://localhost`。
4. 复制 **应用程序(客户端) ID**。
5. **身份验证** → **允许公共客户端流 = 是** → 保存。
6. 启动器「设置 → 微软登录」粘贴该 ID → 保存。

配置后登录窗口会自动改用 v2.0 端点（`XboxLive.signin` 权限），Xbox Live 即接受该令牌。

## 常见错误

| 现象 | 含义 | 处理 |
| --- | --- | --- |
| Xbox Live 401 | 旧流程令牌缺少 Xbox 范围 | 填入自己的应用 ID（见上）后重新登录 |
| Xbox Live 400 | 账户侧问题（多为无 Xbox 档案） | 先在 <https://www.xbox.com> 用同一账户登录一次；检查地区；未成年人需家庭批准 |
| `AADSTS700016` / `AADSTS7000218` / `AADSTS50011` | 应用 ID 不存在 / 未允许公共客户端 / 未登记回调 | 按提示修正 Azure 应用配置 |
| 「缺少 WebView2 运行时」 | 系统没有内置浏览器组件 | 安装 WebView2 运行时（Windows 11 通常自带） |

## 技术说明

- 登录窗口：WPF + `Microsoft.Web.WebView2`；`NavigationStarting` 中匹配回调地址并取消跳转，
  直接从 URL 取授权码，`state` 参与校验。配置了应用 ID 时走
  `login.microsoftonline.com/.../oauth2/v2.0/authorize`（`redirect_uri=http://localhost:<随机端口>`，
  在窗口内拦截，不监听端口），否则走 `login.live.com`。
- 令牌链：Xbox Live `user.auth.xboxlive.com/user/authenticate`（`RpsTicket: d=<token>`，依次尝试
  `d=` / `t=` / 无前缀）→ XSTS → `login_with_xbox` → `/minecraft/profile`。
- 皮肤头像：用档案里的 `skinUrl` 下载皮肤 PNG，本地裁出 8×8 头部并叠加帽子层，
  以最近邻放大显示；结果缓存在 `<数据目录>/cache/skins`，离线可用。
- 令牌存储：Windows DPAPI 加密的 `accounts.json`；日志只记录流程名与错误码。