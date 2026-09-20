# Codex 额度浮窗

一个运行在 Windows 上的 Codex 额度监控小工具，用于在桌面浮窗和系统托盘中查看当前额度。

![Codex 额度浮窗图标](assets/codex-widget.png)

## 功能

- 显示 5 小时窗口剩余额度
- 显示 1 周窗口剩余额度
- 显示 GPT-5.6 Luna 储备额度（账号可用时自动显示）
- 显示最近一张 Codex 重置卡的过期时间（账号有可用重置卡时自动显示）
- 自动刷新额度，支持 30 秒、60 秒或关闭自动更新
- 进度条和剩余百分比根据额度变化显示颜色
- 支持窗口拖动、折叠和展开
- 折叠后只保留 5 小时窗口和剩余百分比
- 支持透明度和字体大小设置
- 支持主题颜色设置
- 支持自定义全局快捷键
- 支持始终置顶
- 支持最小化到系统托盘
- 支持开机自动启动
- 支持记忆窗口位置和用户设置

## 使用方式

### 直接运行

从 [Releases](../../releases) 下载便携版，解压后双击：

```text
CodexQuotaWidget.exe
```

当前便携版需要 Windows 10/11 64 位和 .NET 8 Desktop Runtime。

### 从源码运行

需要安装 .NET 8 SDK，然后在项目目录执行：

```powershell
dotnet run --project CodexQuotaWidget\CodexQuotaWidget.csproj
```

## 构建发布

普通 Windows 发布：

```powershell
dotnet publish CodexQuotaWidget\CodexQuotaWidget.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output CodexQuotaWidget\publish
```

生成文件位于：

```text
CodexQuotaWidget\publish\CodexQuotaWidget.exe
```

## 登录状态和隐私

程序不会把账号信息写入项目目录，也不会包含开发者的登录凭据。

额度查询时，程序会读取当前 Windows 用户自己的 Codex 登录文件：

```text
%USERPROFILE%\.codex\auth.json
```

请不要将自己的 `auth.json`、Token 或运行日志上传到 GitHub。每台电脑都应该使用自己的 Codex 登录状态。

项目中的 `.gitignore` 已排除 `bin/`、`obj/`、`publish/`、便携版目录、ZIP 和 PDB 文件。

## 数据来源说明

额度数据来自当前 Codex 客户端使用的 ChatGPT 额度接口；可用重置卡的过期时间来自对应的重置卡详情接口。上述接口属于客户端内部使用的接口，字段和可用额度可能随 Codex 或 ChatGPT 更新而变化。

如果接口结构发生变化，额度解析可能需要同步更新。

## 项目结构

```text
CodexQuotaWidget/
├─ App.xaml                 # WPF 应用资源
├─ MainWindow.xaml          # 浮窗界面
├─ MainWindow.xaml.cs       # 窗口、托盘和设置逻辑
├─ CodexUsageClient.cs      # 登录状态读取和额度查询
├─ QuotaModels.cs           # 额度数据模型
├─ WidgetSettings.cs        # 设置持久化
├─ Themes.xaml              # 控件样式
├─ assets/                  # 程序图标
└─ tools/                   # 图标生成脚本
```

## 许可证

本项目采用 [MIT License](LICENSE) 开源许可证。
