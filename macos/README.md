# TraeBar — macOS 菜单栏版

[TraeTools](../README.md) 是 Windows 桌面应用；本目录是它的 **macOS 原生等价实现**（菜单栏常驻，Swift / AppKit，无第三方依赖），核心能力与 Windows 版对齐：接口、参数与风控处理均移植自 `Services/Checkin/TraeApiClient.cs` 与 `checkin.py`。

## 功能

- **多账号每日签到**：每 10 分钟自动检查，漏签自动补签；先查 `status` 再 `claim`，不重复请求
- **9074 风控自动重试**：命中「参与用户太多」自动更换 16 位数字设备号重试（最多 5 次）
- **剩余积分显示**：菜单栏图标直接显示所有账号积分总和，会话过期变 ⚠︎
- **一键登录添加账号**（移植自 Windows 版 `LoginHostForm`）：弹出内嵌 WKWebView 登录 trae.cn，登录成功自动抓取 `X-Cloudide-Session`，无需手动 F12 复制 Cookie；每次登录使用独立非持久化会话，多账号互不串号，同一账号自动去重
- 也保留手动粘贴 Cookie 的添加方式
- **账号管理**：昵称 / 脱敏手机号自动从 `GetUserInfo` 拉取；会话过期（约 14 天）提醒并一键更新
- **开机自启**：菜单内一键开关（写入 `~/Library/LaunchAgents/com.traebar.plist`），带单实例锁防重复拉起
- 凭证存储于 `~/Library/Application Support/TraeBar/state.json`（权限 600）

## 编译与运行

要求：macOS 13+、Xcode Command Line Tools（Swift 5.9+）。

```bash
cd macos
swift build --configuration release
.build/release/TraeBar        # 菜单栏出现 ⚡ 图标即运行
```

## 使用

1. 点菜单栏 ⚡ → **登录账号…**，在弹窗内用手机号验证码或扫码登录 trae.cn，窗口自动关闭即添加成功
2. 建议开启 **自动每日签到** 与 **开机自启**
3. 会话约 14 天过期，菜单显示 ⚠︎ 时点 **更新会话 Cookie…** 重新登录一次即可

## 说明

- 仅支持国内版（api.trae.cn）。国际版（api.trae.ai）无签到积分体系（订阅制），相关接口均 404，无适配意义
- 在 macOS 26 / arm64（Apple Silicon）实测：编译零警告、签到 / 积分 / 多账号 / 单实例锁均正常
- 与主项目同为 GPL-3.0
