# TraeTools

> Trae 一体化管理工具：多账号每日签到 · 云端自动签到 · 账号快速切换 · 用量统计

TraeTools 是对 **TRAE-Checkin** 的重构版，并集成了 **Trea-Switch** 的账号切换能力，用 Avalonia 重写为现代化客户端（.NET 10）。**一套代码同时产出 Windows 桌面版与 Android 手机版**，覆盖 Trae 账号的「登录 → 签到 → 切换 → 用量查看 → 云端托管」全流程。

### 气死我了，怎么老是给我爆9074

## 功能特性

- **多账号每日签到**：一页管理多个 Trae 账号，一键签到全部启用账号；自动签到可设定时间，到点自动执行
- **云端签到（永不掉线）**：一键把签到任务部署到你自己的 GitHub fork（GitHub Actions 每天 08:00 运行），无需电脑开机
- **账号切换**：为每个账号备份登录态（vault），一键冷切换客户端账号，失败自动回滚
- **用量统计**：按会话展示模型 / 输入输出 Token / 缓存命中率 / 积分消耗，并本地持久化；另含「积分快过期提醒」
- **账号资料**：展示每个账号的昵称 / 脱敏手机号 / 头像 / 学生认证徽章
- **飞书推送**：签到结果可实时推送到飞书机器人
- **自动更新**：内置检查更新与静默替换安装

## 界面

| 导航 | 说明 |
|---|---|
| 仪表盘 | 剩余积分、今日签到状态、积分趋势图、账号概览 |
| 用量统计 | 逐会话用量 + 模型分布 + 汇聚统计，全部本地留档 |
| 每日签到 | 本月日历、连签天数、签到记录、一键/自动签到 |
| 账号切换 | 建档（备份登录态）、一键切换、校验 |
| 云端签到 | GitHub 授权、一键部署、部署状态与日志 |
| 设置 | 自动签到配置、Token 信息、飞书推送、账号管理、关于 |

## 快速开始

> **macOS 用户看这里**：macOS 原生版 TraeBar（菜单栏常驻多账号自动签到，Swift/AppKit 实现）在本仓库的 [`macos`](https://github.com/star620/TraeTools/tree/macos) 分支独立维护与发布，版本 tag 为 `macos-v*`，发布产物见 [Releases](https://github.com/star620/TraeTools/releases)。本分支（main）维护 **Windows 版与 Android 版**。

### 方式一：直接使用
在 [Releases](https://github.com/star620/TraeTools/releases) 下载最新版运行（Windows 需 .NET 10 桌面运行时；Android 直接安装 APK，需 Android 8.0+）。

### 方式二：源码编译

需要 **.NET 10 SDK**：

```bash
git clone https://github.com/star620/TraeTools.git
cd TraeTools

# Windows 桌面版
dotnet build TraeTools.csproj -c Release -f net10.0-windows

# Android 手机版（需先安装 Android 工作负载）
dotnet workload install android
dotnet build TraeTools.csproj -c Release -f net10.0-android
```

产物分别位于 `bin/Release/net10.0-windows/`（`TraeTools.exe`）与 `bin/Release/net10.0-android/`（`*.apk`）。

> 项目是双目标（`net10.0-windows;net10.0-android`），**编译时必须用 `-f` 限定框架**，否则会同时构建两端。在非 Windows 系统上构建 android 目标时，`EnableWindowsTargeting` 已在 csproj 中开启，无需额外参数。

## Android 版

Android 与 Windows **共用同一套** `ViewModels/` `Services/` `Views/`（Avalonia 单视图模式），平台差异统一收敛在 `Platform/` 目录与 TFM 条件编译中，不存在第二套业务代码。

### 功能差异

| 能力 | Windows | Android |
|---|---|---|
| 多账号每日签到 / 自动签到 | ✅ | ✅（进程存活期间到点执行） |
| 云端签到部署（GitHub Actions） | ✅ | ✅ |
| 用量统计 / 仪表盘 / 账号资料 | ✅ | ✅ |
| 账号切换（vault 建档 / 冷切换） | ✅ 直接操作本地客户端 | ⚠️ 手机端无本地 Trae 客户端，页面仅作展示 |
| 登录方式 | ✅ 内嵌浏览器（WebView2）自动取凭证 | ✅ 手动粘贴 Token / 会话 Cookie |
| 开机自启 | ✅ 注册表 `Run` 项 | ❌ 系统无等价机制，已降级为无操作 |
| 应用内自动更新 | ✅ 下载 zip 整包并整套替换 | ⚠️ 引导至 Release 页下载 APK 覆盖安装 |
| 凭证加密 | DPAPI（当前用户） | Android Keystore（RSA-2048 包裹 AES-256-GCM 数据密钥） |

### 实现要点

- **多目标而非分叉**：`TraeTools.csproj` 改为 `<TargetFrameworks>net10.0-windows;net10.0-android</TargetFrameworks>`。Windows 侧的 AOT / 裁剪 / 单文件 / `RuntimeIdentifier` 全部保留在 `net10.0-windows` 条件组内，**现有 Windows 发布流程零改动**（`release.yml` 仅追加 `-f net10.0-windows` 以消除多目标歧义）。
- **对话框全部改为 `UserControl`**：Android 走 `ISingleViewApplicationLifetime`，此时 `new Window()` 会抛 `NotSupportedException`。新增 `Views/OverlayDialog.cs` 作为双模宿主——桌面端包一层 `DialogHostWindow`，Android 端挂到 `MainView` 的全屏覆盖层并用 `TaskCompletionSource` 模拟模态；所有弹窗调用点由 `ShowDialog` 改为 `OverlayDialog.ShowAsync`。
- **Android 目标显式关闭 AOT / 裁剪 / 链接器**：.NET for Android 在 Release 下默认启用 LLVM AOT，会因 `SQLitePCLRaw` 等反射路径抛 `MissingRuntimeArtifactException`（表现为「启动即闪退」）。本应用是网络 IO 型工具，稳定性优先。
- **版本号单一真源**：`version.json` 的读取从 `Target` 上移到**求值期** `PropertyGroup`，否则 `AndroidManifest` 的 `versionName` 会停在静态兜底值，与 App 内「关于」显示的版本互相矛盾。
- **崩溃可见**：`Platform/CrashLog.cs` 安装全局异常钩子（含 `AndroidEnvironment.UnhandledExceptionRaiser`），异常落盘并在下次启动时弹出原生错误页 `CrashReportActivity`，避免手机端「闪退且无日志」。

### 构建产物与 CI

- APK 产物位于 `bin/Release/net10.0-android/`；Release 默认使用调试密钥签名，正式分发请自行配置 keystore（`AndroidKeyStore` / `AndroidSigningKeyStore`）。
- `.github/workflows/ci.yml` 已拆为 `build-windows` 与 `build-android` 两个作业，后者在 Ubuntu 上安装 Android 工作负载编译 APK 并上传为构建产物。

## 使用指南

### 1. 添加 / 登录账号
`设置 → 账号管理 → + 添加`：自动打开登录窗口（内嵌浏览器），手机号+验证码登录后自动读取凭证并保存；登录态约 14 天，失效可一键静默续期。

### 2. 每日签到
签到页点「一键签到」或开启「每日自动签到」；也可在仪表盘点「立即签到」。签到结果写入本地历史，日历与连签天数随之更新。

### 3. 用量统计
进入「用量统计」页面自动同步近 7 天会话；每次会话展示模型、入/出 Token、缓存命中字节与积分消耗，数据按账号本地保留，可追溯历史趋势。

### 4. 云端签到（部署到 GitHub Actions）
1. `云端签到` 页点击「授权 GitHub」（设备码授权，无需密码，仅需 repo 权限）
2. 点击「一键部署到云端」：程序自动 fork 本仓库 → 写入每个账号的 `TRAE_SESSION` / `TRAE_DEVICE_ID` secret → 启用并触发验证运行
3. 验证成功即生效：**每天北京时间 08:00** 自动签到，即使本机关机也不中断

部署原理：fork 仓库中的 `.github/workflows/checkin.yml` 定时运行 `checkin.py`，脚本用 `X-Cloudide-Session` 换取新 JWT 后调用签到接口（仅依赖 Python 标准库）。

> 从旧版 TRAE-Checkin 迁移：已部署过旧 fork 的用户，在新版「重新部署」时会提示自动删除旧 fork 并重建，无需手动去网页操作。旧 fork 仓库可自行删除。

### 5. 账号切换
`账号切换` 页：为当前登录的客户端「建档」（备份登录态载体）→ 选择目标账号 → 「切换至此」一键冷切换；切换失败会自动回滚到原账号并重启客户端。账号删空后可直接「建档」输入新名称重新建立。

## 常见问题

| 现象 | 说明 / 处理 |
|---|---|
| 签到提示「当前参与用户太多」 | Trae 风控：同设备/网络频繁请求触发。建议错开 08:00 高峰、减少账号数、不同账号用独立设备号 |
| 云端部署 HTTP 401 | GitHub 授权失效或权限不足：点「重新授权」；仍失败则自动删除旧 fork 重建 |
| 云端提示「未找到 workflow」 | 旧 fork 早于本仓库添加 checkin.yml：按提示一键「删除并重建 fork」即可自愈 |
| 签到 0 积分 | 当日已签过（重复签到不加分），或非会员当日基础 150 分已到账 |
| Token 失效 | 程序会用 `X-Cloudide-Session` 静默换新；彻底失效时提示重新登录 |

> 云端签到凭证 `TRAE_SESSION` 有效期约 14 天，过期后请在软件内重新登录并重新部署。

## 开发者

- 技术栈：Avalonia 12 / .NET 10 / CommunityToolkit.Mvvm / Sodium.Core / WebView2（仅 Windows 登录宿主）
- 目录：`ViewModels/` 页面逻辑、`Services/` 网络与领域服务、`Views/` 界面、`Models/` 数据模型、`Platform/` 平台差异实现（加密 / 路径 / 日志 / 崩溃 / 浏览器 / 对话框宿主）
- 行为默认不对外请求官方非公开接口之外的能力；用量/积分接口为逆向网页接口，字段随官方可能变化，代码均做容错解析

## 免责声明

- 本项目为个人开源工具，与 ByteDance / Trae 官方无任何关系
- 使用自动签到、账号切换等功能请遵守 Trae 服务条款，风险自负
- 请勿出售、外借或用于非法用途；账号凭证仅保存在你自己的电脑/GitHub secret 中

## 致谢

- [TRAE-Checkin](https://github.com/star620/TRAE-Checkin)：本项目的前身，云端签到体系（checkin.py + GitHub Actions 流程）源自该仓库
- [Trea-Switch](https://github.com/star620/Trea-Switch)：账号切换（vault 建档 / 冷切换 / 守护回滚）能力源自该仓库

## 许可证

见 [LICENSE](LICENSE)。
