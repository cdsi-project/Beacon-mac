# CDSI Beacon for macOS

这是 CDSI Beacon 的 macOS 桌面程序。界面使用 Avalonia，领域、应用和基础设施逻辑位于同一仓库的 `CDSI.Agent.Core`、`CDSI.Agent.Application` 与 `CDSI.Agent.Infrastructure`；`CDSI.Agent.Mac` 提供 macOS 生命周期、Finder、卷标识、钥匙串、单实例和 `.app` 打包集成。

应用当前支持 Apple Silicon 和 Intel Mac，最低系统版本为 macOS 12。应用版本以仓库根目录的 `VERSION` 为唯一来源。

## 开发依赖

- macOS 12 或更高版本。
- .NET 10 SDK。仓库的 `global.json` 请求 10.0.400 Feature Band，并允许使用该 Feature Band 的最新补丁版本。
- 首次 `restore` 时需要访问 NuGet 源。
- Git 项目同步和完整测试集需要系统 `git`。未安装时可运行 `xcode-select --install` 安装 Apple Command Line Tools。
- `codesign` 是可选的打包依赖。存在时打包脚本会进行 ad-hoc 签名；正式分发仍需 Developer ID 签名和 Apple 公证。
- 重新生成应用图标需要完整 Xcode，以便通过 `xcrun actool` 编译 Asset Catalog；日常构建只校验已提交的图标产物。

发布后的 `.app` 是 self-contained 应用，目标电脑不需要另行安装 .NET Runtime。当前脚本每次只生成一种架构，不会生成 Universal Binary。

## 构建与运行

以下命令均在 `mac` 目录执行：

```bash
dotnet restore CDSI.Agent.Mac.slnx
dotnet build CDSI.Agent.Mac.slnx -c Release --no-restore
dotnet run --project CDSI.Agent.Mac/CDSI.Agent.Mac.csproj
```

也可以使用 Makefile：

```bash
make restore
make build
make run
```

如需指定 `dotnet`，使用 `DOTNET_BIN`：

```bash
make DOTNET_BIN=/path/to/dotnet restore
make DOTNET_BIN=/path/to/dotnet build
```

## 测试

```bash
make restore
make test
```

`CDSI.Agent.Mac.slnx` 会运行以下测试层：

- `CDSI.Agent.Mac.Tests`：钥匙串适配、Volume UUID、Finder/URL/单实例集成，以及 macOS 生命周期辅助逻辑。
- `CDSI.Agent.Core.Tests`：资产、扫描计划、Git 地址、Reader、OpenWeb 和对象存储领域规则。
- `CDSI.Agent.Infrastructure.Tests`：SQLite、文件扫描、元数据、Git CLI、状态保护、Reader 和存储适配器。
- `CDSI.Agent.IntegrationTests`：扫描、项目、传输、云备份、Git、Reader 和 OpenWeb 应用服务。

WinForms UI 测试不属于 macOS 解决方案。需要单独定位测试时可执行：

```bash
dotnet test tests/CDSI.Agent.Mac.Tests/CDSI.Agent.Mac.Tests.csproj -c Release
dotnet test tests/CDSI.Agent.Core.Tests/CDSI.Agent.Core.Tests.csproj -c Release
dotnet test tests/CDSI.Agent.Infrastructure.Tests/CDSI.Agent.Infrastructure.Tests.csproj -c Release
dotnet test tests/CDSI.Agent.IntegrationTests/CDSI.Agent.IntegrationTests.csproj -c Release
```

## 打包应用

在当前 Mac 架构下生成应用包：

```bash
make app
```

指定目标架构：

```bash
make app BEACON_ARCH=arm64
make app BEACON_ARCH=x86_64
```

产物按运行时架构隔离，避免先后构建两个架构时互相覆盖：

```text
build/osx-arm64/CDSI Beacon.app
build/osx-x64/CDSI Beacon.app
```

以 Apple Silicon 产物为例，本地检查可执行：

```bash
open "build/osx-arm64/CDSI Beacon.app"
codesign --verify --deep --strict --verbose=2 "build/osx-arm64/CDSI Beacon.app"
plutil -lint "build/osx-arm64/CDSI Beacon.app/Contents/Info.plist"
```

`scripts/build-app.sh` 会先清理当前架构的旧发布目录，再生成 self-contained、single-file 发布，复制完整 Retina 图标和法律文件，并校验属性列表、可执行文件架构及 ad-hoc 签名。该签名只适合本地开发和验证，不替代 Developer ID 签名、hardened runtime、公证、staple 或安装包。

### 图标资源

应用图标以 `CDSI.Agent.Mac/Assets/logo.png` 为母版。可审计的 10 个 macOS 标准尺寸及 `Contents.json` 位于 `Resources/Assets.xcassets/AppIcon.appiconset`；构建使用的 `Assets.car` 和完整 `.icns` fallback 位于 `CDSI.Agent.Mac/Assets`。`Resources/AppIcon.sha256` 绑定母版、各尺寸源图、生成器和两个编译产物，防止只更新其中一部分。

更换母版后执行：

```bash
./scripts/generate-icons.sh
```

该命令使用系统 AppKit 生成带标准 alpha 通道的 16 至 1024 像素 1x/2x PNG，并通过 `actool` 重新编译 `Assets.car` 和完整 `Beacon.icns` fallback；fallback 使用兼容目标编译，以保留全部 legacy/Retina 图层，不改变应用本身的 macOS 12 最低版本。`sips` 用于复核每张图片的实际尺寸，`iconutil` 用于反解校验 `.icns`。只检查已提交资源而不重新生成时执行：

```bash
./scripts/generate-icons.sh --check
```

`make app` 会在发布前自动执行同一项只读检查，避免 Asset Catalog、`.icns` 与源 PNG 缺层或尺寸漂移。

## 数据位置

默认本机数据目录为：

```text
~/Library/Application Support/CDSI
```

自动化验收或隔离调试时，可将 `CDSI_BEACON_DATA_DIRECTORY` 设置为一个绝对路径；
未设置时始终使用上述默认位置。不要把临时目录用于需要长期保留的正式数据。

主要内容包括：

- `cdsi.db`：资产索引、项目、扫描目录、非敏感连接配置和操作记录。
- `reader.db`：RSS 订阅、条目、已读和收藏状态。
- `client-identity.json`：本机客户端标识。
- `Logs/`：仅在启动、首次配置前或工作目录不可用时使用的应急日志目录。
- `StateProtection/`：恢复准备过程中使用的受控临时状态。

首次运行必须选择工作目录，建议位置为：

```text
~/cdsi_workspace
```

工作目录包含 `Inbox`、`Assets`、`Exports`、`Cache`、`Temp` 和 `System`。数据库快照位于 `System/DatabaseBackups`，Reader 快照位于其 `Reader` 子目录；用户创建的完整状态包位于 `System/StateBackups`。这些数据库和状态包含有本地路径、项目及 RSS 数据，应按敏感文件管理并纳入独立备份策略。

正常运行日志位于当前工作目录的 `System/Logs`。应用在解析出工作目录后会把当前会话的启动日志复制到这里并继续写入；凭据赋值和 URL 查询参数会脱敏，但路径、文件名和错误上下文仍可能包含隐私信息。切换工作目录不会移动或删除旧工作目录中的历史日志。

## macOS 钥匙串

对象存储 Secret、WordPress 应用程序密码和 Git 密码不写入 SQLite，而是保存到当前登录用户的 macOS 钥匙串。所有条目使用服务名：

```text
com.cdsi.beacon
```

可在“钥匙串访问”中按该服务名查找。删除数据库或应用包不会自动删除钥匙串条目；删除配置时应用会清理对应凭据。macOS 可能在首次访问或钥匙串锁定时要求用户确认或解锁。

## 文件与网络权限

CDSI Beacon 当前不是 App Sandbox 应用。它仍受 macOS 隐私控制约束：

- 扫描“桌面”“文稿”“下载”、外置磁盘或网络卷时，macOS 可能要求“文件与文件夹”访问权限。只应授权实际需要扫描的位置。
- 若用户明确需要索引受系统保护且普通授权无法覆盖的位置，可在“系统设置 > 隐私与安全性 > 完全磁盘访问权限”中授权。常规工作目录和普通扫描目录不应默认要求完全磁盘访问权限。
- Finder 定位、文件选择和目录选择由系统接口完成；应用不会借此绕过目录权限。
- RSS 刷新、更新检查、云备份/取回/删除、OpenWeb 发布和 Git 同步会访问网络。保存配置本身不会上传资产。
- SSH 同步依赖用户自己的 `~/.ssh` 密钥和远端主机信任配置。未发现密钥时，只有用户明确确认后，Beacon 才会在 Terminal 中打开系统 `ssh-keygen`，并选择未占用的 Beacon 专用文件名；应用不会覆盖、读取、复制或上传私钥。

如果应用无法读取某个目录，先在 Finder 中确认当前用户拥有访问权，再检查“系统设置 > 隐私与安全性 > 文件与文件夹”中的 CDSI Beacon 权限。不要通过放宽整个磁盘权限来掩盖文件自身的所有权或 ACL 问题。
