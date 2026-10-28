# TermLume

TermLume 是一款支持 Windows 与 macOS 的 SSH / SFTP 桌面客户端，提供主机管理、多标签终端、双栏文件传输及本地工作空间。Windows 使用 WPF / WebView2；macOS 使用 Electron 桌面入口，两者复用 .NET 10、SSH.NET、xterm.js 和同一套工作台界面。macOS 构建与验收说明见下方「macOS 版本」。

## 下载与安装（无需编译）

**安装包统一发布在 [GitHub Releases](https://github.com/JasonZcl/TermLume/releases)。** 请在对应版本的 **Assets（资源）** 中按平台下载 Windows EXE 或 macOS DMG / ZIP；具体可用平台以实际发布的附件为准。`artifacts/` 仅用于本地构建输出，不上传到 Git；克隆仓库或下载源码 ZIP 不包含安装包。

### 下载安装包（Windows）

当前 v0.7.4 安装包支持升级时自动卸载旧版程序，始终保留个人数据，并包含新的退出确认窗口、终端命令片段侧栏和可自定义的片段快捷键。

| 项目 | 说明 |
| --- | --- |
| 当前版本 | v0.7.4 |
| 下载位置 | [GitHub Releases → Assets](https://github.com/JasonZcl/TermLume/releases) |
| 安装包文件名 | `TermLume-0.7.4-win-x64-Setup.exe` |
| 安装包大小 | 约 48.7 MiB（51,113,574 字节） |
| 目标平台 | Windows 10 / 11，win-x64 安装包 |
| .NET 环境 | 已内置，不需要另外安装 .NET Runtime 或 SDK |
| WebView2 环境 | 已安装则复用；缺少时需要联网安装 Microsoft 官方运行时 |

1. 打开 [GitHub Releases](https://github.com/JasonZcl/TermLume/releases)，进入对应版本的 **Assets**，下载 `TermLume-0.7.4-win-x64-Setup.exe`。`Source code (zip)` / `Source code (tar.gz)` 是源码，不是安装包。
2. 保存远端工作并正常退出旧版 TermLume，然后双击安装包，选择简体中文或 English，按提示安装。
3. 安装完成后，从开始菜单打开 TermLume；安装器默认不自动启动应用。初始程序目录为 `%LOCALAPPDATA%\Programs\TermLume`，也可以在安装时修改。
4. 升级时直接运行新安装包，无需手动卸载；安装器会先静默卸载已登记的旧版程序，再安装新版本。主机、分组、语言、快捷键、已知主机、日志和缓存保存在独立的 `%LOCALAPPDATA%\TermLume`，自动卸载始终保留这些数据。

卸载时可运行**安装目录中的 `unins*.exe`（例如 `unins000.exe`）**，或使用开始菜单 / Windows「已安装的应用」中的卸载入口。默认保留个人数据；只有主动选择删除并再次确认，才清理 TermLume 应用数据。详细边界见下方「Windows 安装包」及「数据与安全边界」。

### 校验安装包

v0.7.4 安装包的 SHA-256 如下；其他版本或重新打包的文件，请以对应 Release 发布说明中的校验值为准：

```text
42f15078ec808b802ccb1a0656a94ec177089e04bb80e651ff1aebde43a0384a
```

在下载的安装包所在目录打开 PowerShell，运行以下命令，将输出的 `Hash` 与对应版本的校验值比较（大小写不影响比较）：

```powershell
Get-FileHash -LiteralPath ./TermLume-0.7.4-win-x64-Setup.exe -Algorithm SHA256
```

应用和安装包目前未使用代码签名证书，Windows 可能提示发布者未知。校验值用于核对文件完整性，不能代替发布者数字签名。

## 当前功能

- 深色工作台，支持简体中文 / English，提供主机卡片 / 列表、分组、收藏、搜索与最近连接。
- 主机新增、编辑、删除；支持 IPv4、域名、IPv6 和自定义端口。
- 独立新建分组，支持空分组并自动保存；在分组内添加主机自动归组，旧版主机分组自动保留。
- 快速地址填写：`user@example.com:22`、`root@[::1]:2222`，确认配置后保存并连接。
- SSH 密码认证、私钥认证（支持私钥口令），凭据每次连接时询问，不保存到磁盘。
- 多标签交互式 PTY 终端，支持 ANSI、UTF-8、终端大小同步、远端全屏应用及滚动历史；最多 12 个标签。
- SFTP 双栏文件管理：默认显示「此电脑」的全部磁盘，双击磁盘后浏览文件夹，也可手动选择文件夹；支持远端浏览、路径跳转、上级目录、名称筛选及隐藏文件开关；与 SSH 终端独立连接，复用主机配置、认证方式和已知主机指纹校验。
- 多文件上传 / 下载、传输队列、字节进度及取消；远端新建文件夹、重命名、经确认删除普通文件或空目录。同名文件不覆盖，普通文件使用临时文件完成后提交。
- SSH 主机指纹首次信任确认，后续算法对应的已保存指纹不匹配时拒绝连接；在「已知主机」中管理信任记录。
- 独立「已知主机」页面：按地址、主机名称、算法或指纹搜索，筛选指纹变化记录，复制指纹并经确认移除信任；显示首次信任、最近核验时间及最近收到的异常指纹。
- 独立「日志」页面：连接开始 / 成功 / 失败 / 取消 / 断开、应用启停及指纹信任 / 核验 / 变化 / 移除事件；支持关键词、级别、类型、时间筛选、分页、详情复制、JSON 导出与确认清空。
- 命令片段新增 / 编辑 / 删除 / 复制，支持终端侧栏搜索与目标会话选择；单行命令可无回车填入，多行命令可在再次确认后发送执行，含控制字符的片段拒绝发送。
- 字号与 Midnight / Nord 配色，主机配置自动保存到本机。
- 紫灰色紧凑工作台、可折叠侧栏、顶部终端标签与自定义窗口控制；主机支持单击选择、双击或 Enter 连接。
- 鼠标拖动窗口四边 / 四角缩放，保留原生缩放命中区；SFTP 中间分隔条可拖动调整双栏宽度，双击恢复均分。
- 统一的原生密码 / 私钥口令弹窗：紫灰卡片、圆角输入框、焦点高亮、Caps Lock 提醒，支持 Enter 连接和 Esc 取消。
- 分类设置中心：外观、终端、快捷键、数据与安全、关于；终端外观实时预览，支持强调色、卡片密度和侧栏状态保存。
- 按平台提供 18 项应用快捷键：Windows 使用 Ctrl 方案，macOS 使用 Command 方案；可录制修改、停用、还原，带冲突及系统保留键检查，重启后保留。
- 简体中文 / English 即时切换，覆盖工作台、设置、SFTP、已知主机、日志和原生认证 / 指纹提示；语言偏好自动保存。
- 终端字体、11–24 px 字号、行高、光标样式 / 闪烁及 1,000–50,000 行滚动历史可配置，应用到已打开的终端。
- 独立 TermLume 图标，覆盖可执行文件、任务栏、窗口、认证对话框与页面标识，ICO 包含 16–256 px 的 9 个尺寸。

## macOS 版本

源码提供 Apple Silicon（`arm64`）和 Intel（`x64`）两个独立构建目标，最低系统设为 **macOS 14**。macOS 安装包需要在 Mac 或仓库的 **Actions → macOS desktop → Run workflow** 中构建；不会把 Windows EXE 改名成 DMG，也不会在 Windows 上假装完成 macOS 原生验收。尚未执行的 Actions、真机测试及未上传的 Release 附件不视为已经发布。

| 设备 | 构建命令（在 `desktop` 目录中） | 生成文件 |
| --- | --- | --- |
| Apple Silicon | `npm run dist:mac:arm64` | `TermLume-0.7.4-macOS-arm64.dmg` / `.zip` |
| Intel | `npm run dist:mac:x64` | `TermLume-0.7.4-macOS-x64.dmg` / `.zip` |

- **安装与升级**：打开 DMG，将 `TermLume.app` 拖入「应用程序」。更新前正常退出旧版本，再替换应用；个人配置放在独立目录，不随 `.app` 覆盖。内置 .NET 后端，使用者无需另外安装 .NET 或 WebView2。
- **界面与操作**：保留主机、分组、命令片段、已知主机、日志、中英文切换与 SSH / SFTP 功能；使用 macOS 红黄绿窗口按钮、可缩放窗口、原生文件选择框及独立密码弹窗。可从 Finder 拖入普通文件上传，双栏间拖放上传 / 下载；不支持从远端面板直接拖出到 Finder。
- **快捷键**：`⌘N` 新建主机、`⌘K` 搜索、`⌘,` 设置、`⌘C / ⌘V` 终端复制 / 粘贴、`⌘⇧S` 命令片段、`⌘W` 关闭当前会话、`Control+Tab` 切换标签。可在设置中修改；`Control+C / D / Z / L` 保留终端语义，`⌘Q / ⌘H / ⌘M / ⌘Tab` 等不允许绑定为应用操作。
- **数据目录**：`~/Library/Application Support/TermLume/`，包含 `workspace.json`、`activity-log.json`，Electron 缓存位于 `Desktop/` 子目录。SFTP 默认显示系统磁盘和 `/Volumes` 下已挂载的普通卷，不默认打开用户主目录；符号链接不跟随。系统隐私权限不足时应由用户选择可访问目录，不建议授予整个磁盘权限。
- **迁移 Windows 配置**：先退出两端并备份，将 `workspace.json` 复制到 macOS 数据目录；私钥文件需要自行安全转移并重新选择路径。导入的 Ctrl 自定义组合会保留，若要使用 Command 方案，在 Mac「设置 → 快捷键」恢复默认。活动日志可按需迁移，缓存不要跨平台复制。
- **卸载**：退出后将 `.app` 移到废纸篓，默认保留个人数据。需要彻底清理时，先备份，再通过 Finder「前往文件夹」打开上述数据目录并手动移除；不要删除目录以外的私钥、下载文件或远端文件。
- **签名边界**：默认构建使用临时签名（ad-hoc），没有 Developer ID 身份和 Apple 公证，不等于可直接面向公众分发的正式签名包；系统可能阻止来源未知的应用。请勿要求用户关闭 Gatekeeper。正式发行的证书、公证与验收步骤见 [`desktop/README.md`](desktop/README.md)。

### macOS 开发与打包

开发机需要 .NET 10 SDK、Node.js 24 和 Xcode Command Line Tools；不要在 Mac 上构建包含 WPF 的整个 `.sln`，使用桌面入口脚本：

```bash
cd desktop
npm ci
npm start
# 按目标 CPU 选择一个
npm run dist:mac:arm64
# npm run dist:mac:x64
```

打包脚本将完整构建放在系统临时目录，仅把最终 DMG / ZIP 复制到仓库根目录的 `artifacts/`，并输出 SHA-256；该目录仍被 Git 排除。Actions 会分别验证两种架构并将安装包保存为工作流附件，不会自动发布 Release。维护者下载并验收后，再将内层 DMG / ZIP 上传到对应 GitHub Release 的 Assets；工作流附件外层的 ZIP 只是下载容器。

## Windows 开发运行

需要 Windows 10/11、.NET 10 SDK 及 Microsoft Edge WebView2 Evergreen Runtime；开发运行所需的 .NET Desktop Runtime 至少为 10.0.14，建议使用含该安全补丁或更新补丁的 SDK。首次还原 NuGet 包需要网络；终端前端依赖已打包在仓库，无需 Node.js、npm 或 CDN。SSH.NET 使用仓库 `vendor/packages` 中的本地安全修订包，来源及重建方法见该目录 README，不要替换成未设接收限额的上游版本。

```powershell
dotnet restore TermLume.sln
dotnet build TermLume.sln
dotnet run --project TermLume.Windows
```

发布框架依赖版（目标设备需要 .NET 10.0.14 或更新安全补丁的 Desktop Runtime，以及 WebView2 Runtime）：

```powershell
dotnet publish TermLume.Windows/TermLume.Windows.csproj -c Release -r win-x64 --self-contained false -o "$env:TEMP/TermLume-framework-dependent-v0.7.1"
```

## Windows 安装包

从 [GitHub Releases](https://github.com/JasonZcl/TermLume/releases) 下载 `TermLume-0.7.4-win-x64-Setup.exe` 后，双击安装。支持简体中文 / English 安装及卸载界面，默认仅为当前 Windows 用户安装，无需管理员权限；可选桌面快捷方式，也可从开始菜单打开。安装包内置 .NET，目标电脑无需另外安装 .NET Desktop Runtime。缺少 WebView2 Runtime 时，安装器使用带 Microsoft 数字签名的官方引导程序联网安装；离线电脑请提前安装 WebView2 Evergreen Runtime。运行时安装失败会中止应用安装，不继续更新程序文件。不自动启动应用，避免意外创建连接。

- **升级 / 重装**：直接运行新安装包，无需手动卸载。安装器按固定产品标识识别当前用户已登记的旧版，先检查运行状态、路径、版本和 WebView2 前置条件，再使用旧版自己的卸载器静默移除程序文件，最后安装新版本。自动卸载始终保留个人数据，旧版卸载失败或卸载器缺失时停止安装；也支持选择新程序目录，旧目录内非安装器创建的文件会保留。主机、分组、设置、信任记录、日志和缓存仍保存在 `%LOCALAPPDATA%\TermLume`，不会覆盖或初始化。阻止降级，包括通过更改安装目录绕过版本检查。只处理当前用户已登记的同一产品，不搜索或删除其他账号、便携副本及下载的旧安装包。新文件安装阶段失败不会自动恢复已卸载的旧版程序，可修复问题后重新运行同版或更新安装包；个人数据继续保留。
- **先关闭应用**：安装 / 卸载前请保存远端工作并正常退出 TermLume。检测到运行中的实例会要求先关闭，不强制结束 SSH 会话、不自动重启应用。
- **卸载入口**：安装后的软件文件夹中有 `unins*.exe`（例如 `unins000.exe`），开始菜单和 Windows「已安装的应用」中也有卸载入口。请不要删除同目录的 `unins*.dat` / `unins*.msg`，它们是卸载器所需文件。
- **保留个人数据（默认）**：只卸载安装器管理的程序文件和快捷方式，保留主机、设置、日志及 WebView2 缓存，下次安装继续使用。
- **删除全部 TermLume 个人数据**：在卸载界面主动选择该项，并再次确认后，才清理 `%LOCALAPPDATA%\TermLume` 全部内容。该操作不可恢复，需保留的配置请先备份到其他目录。不会连接服务器，也不会删除该数据目录以外的私钥原文件、SFTP 下载文件或远端文件；不要将这些文件自行放进应用数据目录。
- **安全边界**：清理不会跟随符号链接、目录联接或其他重解析点；遇到链接、文件占用或权限问题会保留无法清理的部分并提示。安装目录中非安装器创建的文件不会被笼统递归删除。保留个人数据不等同于自动备份，重要配置仍建议另行备份。
- **静默部署**：安装可用 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`。卸载同样使用这些参数时默认保留数据；只有显式增加 `/DELETEUSERDATA` 才删除当前账号的 TermLume 数据，静默模式没有二次确认，请谨慎使用。正常交互式卸载始终默认勾选保留数据。 内部自动卸载使用 `/KEEPUSERDATA`，该参数优先于 `/DELETEUSERDATA`，不弹出数据清理选项。

在 Windows 上安装 .NET 10 SDK、Inno Setup 6.5 或更新版本后，可重复构建：

```powershell
./tools/Build-Installer.ps1
```

脚本在系统临时目录生成自包含 win-x64 发布文件，校验 WebView2 引导程序的 Microsoft 签名，并校验内置 .NET / WPF 运行时与项目固定的 10.0.14 安全基线一致；`artifacts` 只输出 EXE 安装包，SHA-256 输出到控制台。应用与安装包当前未使用商业代码签名证书，Windows SmartScreen 可能提示发布者未知；分发时请同时提供 SHA-256 校验值。构建工具只需要 Inno Setup 编译器，不会自动安装到开发者的个人软件目录。自包含运行时不会随系统 .NET 自动升级，需要定期更新项目安全基线并重新打包。

安装器源码为 `tools/installer/TermLume.iss`；简体中文语言文件来自 Inno Setup 官方源码仓库的 `Files/Languages/ChineseSimplified.isl`，保留原维护者信息。安装及 WebView2 分发逻辑参考 Inno Setup 官方帮助与 Microsoft WebView2 Distribution 文档。隔离验证脚本为 `tools/Test-Installer.ps1`，只在构建临时目录下使用独立的安装标识、安装目录和模拟数据，不安装或卸载真实用户版本：

```powershell
$build = Join-Path $env:TEMP ("TermLume-build-" + [guid]::NewGuid().ToString('N'))
./tools/Build-Installer.ps1 -BuildDirectory $build
./tools/Test-Installer.ps1 -BuildDirectory $build
```

### 安全回归测试

安装器隔离测试同时覆盖同版本重装、新版本自动卸载旧程序、旧文件清理、目录迁移、卸载器缺失或失败时中止、降级和目录链接拦截，以及自动卸载保留数据和手动卸载的数据清理选择；不会安装或卸载真实用户版本。

测试只使用随机临时目录、模拟凭据和监听 `127.0.0.1` 的本地 SSH / SFTP 服务，不打开真实应用或个人配置。需要 Python 和 Paramiko；推荐使用独立虚拟环境：

```powershell
$venv = Join-Path $env:TEMP ("TermLume-test-python-" + [guid]::NewGuid().ToString('N'))
python -m venv $venv
& "$venv/Scripts/python.exe" -m pip install paramiko==4.0.0
$env:TERMLUME_TEST_PYTHON = "$venv/Scripts/python.exe"
dotnet run --project tests/TermLume.SecurityTests -c Release -p:SelfContained=true
```

覆盖密钥与算法变更、旧信任记录兼容、写盘失败回滚、无前端 ACK 的输出洪泛与确认超时、PTY 建立阶段洪泛、正常 UTF-8 输出与窗口调整、密码和加密 / 未加密 RSA 私钥认证、会话隔离，以及 SFTP 上传下载与拒绝覆盖。同时覆盖共享控制器的旧工作区兼容、主机 / 分组 / 片段操作、凭据取消、偏好写盘失败回滚、本地文件范围、日志导出保护及退出状态。测试使用自包含的 .NET 10.0.14 运行时。

片段前端回归测试只依赖 Node.js，使用模拟终端与桥接，不连接真实主机：

```powershell
node --test tests/snippets.test.cjs
```

覆盖目标选择、命令预览转义、单行无回车填入、多行确认执行、取消不发送、断线和会话替换保护，以及控制字符拒绝。

### 安装包的 Git 管理

源码仓库不包含安装包。`.gitignore` 忽略整个 `artifacts/` 目录，包括 EXE 文件；该目录只作为开发者本地构建的输出位置，不上传到 Git。

安装包统一作为 [GitHub Releases](https://github.com/JasonZcl/TermLume/releases) 附件发布，与源码版本管理分开；不要使用 `git add -f` 强行提交。发布目录、QA 文件及运行时缓存位于临时构建目录，不应提交。安装包内不得包含 `workspace.json`、`activity-log.json`、私钥或其他个人数据。

重新构建或发布版本后，请更新发布说明中的版本、大小和 SHA-256，将源码与 README 提交到 Git，EXE 单独上传到 Releases。构建时间等因素可能导致同一版本重新构建后的校验值不同，以实际准备分发的文件为准。

## 使用

可先点击「新建分组」，只填写分组名称即可创建空分组。选择该分组后点击「新建主机」，主机会默认归入此分组；删除最后一台主机不会删除分组。也可以在主机表单中直接输入新的分组名。

1. 点击「新建主机」，填写名称、地址、端口、用户名与分组。
2. 选择密码或本地私钥认证，保存主机。
3. 点击「连接」，在原生凭据窗口输入密码 / 私钥口令，未加密私钥可留空。
4. 首次连接时，通过可信渠道核对管理员提供的 SHA-256 指纹后再确认信任。
5. 在终端工作，使用左侧导航返回主机列表，已有会话在标签中保留。关闭会话或退出会断开 SSH 连接，远端任务可能被终止。

默认快捷键：`Ctrl+N` 新建主机、`Ctrl+K` 搜索、`Ctrl+,` 打开设置、`Ctrl+Shift+C` 复制终端选区、`Ctrl+Shift+V` 粘贴。可在「设置 → 快捷键」中修改。`Ctrl+C` 保持远端中断语义。粘贴内容可能包含换行，执行前请检查来源。

## 命令片段与终端联动

- 在左侧「命令片段」创建、编辑和复制常用命令；点击「使用片段」选择在线终端，可预览完整命令、主机名、地址及会话标识，不再静默选择最近建立的连接。
- 在终端工具栏点击「命令片段」或按 `Ctrl+Shift+S` 打开右侧面板，按名称或命令搜索，在不中断当前会话的情况下新建、编辑、复制和使用片段。快捷键可以在设置中修改；如果旧配置已把该组合分配给其他操作，会保留原绑定并将新操作设为未绑定。
- 默认「仅填入」只支持单行命令，不发送回车。多行片段可复制后检查，或切换到「确认后执行」；后者必须再次确认目标和命令才会发送命令与回车。取消确认不会发送任何内容。
- 执行前请确认目标是 shell 提示符，并清空未提交的输入；命令会发往现有交互式终端，不是隔离脚本任务，不保证多行命令逐条成功或依次等待结束。断开的会话、空白及包含控制字符的片段会被拒绝。
- 片段以本地明文保存，不要存放密码、令牌或私钥。连接仍按既有身份验证和主机指纹校验流程处理。

## 外观与快捷键

退出时使用与工作台一致的深色确认窗口，展示 SSH、SFTP 和传输任务数量；默认焦点为「继续使用」，按 Esc 或关闭弹窗均不退出。只有明确点击「断开并退出」才取消传输并释放连接；无活动连接时直接退出。

设置中心按外观、终端、快捷键、数据与安全、关于分类组织。外观支持 Midnight / Nord 终端配色、鸢尾紫 / 薄荷绿 / 海洋蓝强调色、紧凑 / 舒适卡片密度及侧栏折叠状态。终端配置自动保存，实时应用，不需要断开连接。JetBrains Mono 选项使用本机字体，未安装时自动使用备用字体，不会下载字体。

在「设置 → 快捷键」点击某项的按键区域，按下新组合后点击「保存快捷键」。可清除以停用单项，也可恢复默认。重复组合会提示冲突；应用只处理自身窗口内的快捷键，不注册全局热键，不改动 Windows 系统快捷键。

| 操作 | Windows 默认组合 |
| --- | --- |
| 新建主机 / 分组 | `Ctrl+N` / `Ctrl+Shift+N` |
| 搜索主机 / 设置 | `Ctrl+K` / `Ctrl+,` |
| 打开终端 / 关闭当前会话 | `Ctrl+Shift+T` / `Ctrl+Shift+W` |
| 下一个 / 上一个标签 | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| 返回工作空间 / 折叠侧栏 | `Ctrl+Shift+H` / `Ctrl+Shift+B` |
| 重新连接 / 清空本地终端显示 | `Ctrl+Shift+R` / `Ctrl+Shift+L` |
| 复制终端选区 / 粘贴 | `Ctrl+Shift+C` / `Ctrl+Shift+V` |
| 增大 / 减小 / 重置终端字号 | `Ctrl+加号` / `Ctrl+减号` / `Ctrl+0` |

终端专用操作只在当前终端页生效；关闭会话仍需确认。复制、粘贴、全选等输入框编辑组合，以及 `Ctrl+C/D/Z/L` 的终端控制语义不会被绑定覆盖。`Alt+F4`、`Alt+Tab`、Windows 键等由系统处理，无法重新映射为应用操作。录制按物理键位保存，支持 F1–F12 功能键，暂不支持多段组合、仅 Shift 加普通按键或 AltGr；自定义应用快捷键会优先于相同的远端按键。`Ctrl+加号` 同时识别主键盘的 `Ctrl+=` 和 `Ctrl+Shift+=`，也识别数字小键盘加减号。

设置写入失败会提示并保留上次成功的配置；快速连续修改通过队列保存，避免互相覆盖。配置保存在 `workspace.json` 的 `preferences` 中，兼容旧版字号和配色。损坏或冲突的快捷键配置在加载时回退为安全默认值，但不会在加载时静默重写原文件。

## 中英文切换

在「设置 → 外观 → 界面语言」中选择「简体中文」或「English」；英文路径为「Settings → Appearance → Interface language」。切换立即生效，重启后保留，不会断开 SSH / SFTP 连接、重建终端或清空传输队列。旧配置默认使用简体中文，保存失败时保留上一次成功的语言。

仅翻译应用界面及系统生成的提示；用户填写的主机名、分组名、备注、命令片段、文件名、路径和远端终端输出保持原样。内置「未分组」只改变显示名称，不改写原分组值；历史日志在展示时翻译，日志原文、事件 ID 和导出内容不改写。日期显示随界面语言切换，时区不变。Windows 系统文件选择框、系统消息框按钮以及第三方组件返回的原始错误可能遵循系统 / 组件语言。

中文源文案作为稳定键，英文资源统一位于 `TermLume.Windows/Web/locales/en.json`，前端和原生 WPF 共用同一词典。前端使用 `t()` 处理普通提示、`msg` 处理带参数的提示、`ui` / `uiHtml()` 只翻译模板静态部分，不翻译插入的用户数据。语言偏好存入 `workspace.json` 的 `preferences.language`，支持 `zh-CN` / `en`；未知的旧值回退为中文，不会在加载时静默覆盖原配置。

## SFTP 文件管理

1. 打开左侧「SFTP」，或悬停主机卡片后点击文件夹按钮，选择已保存的主机并点击「连接主机」。SFTP 需要服务器启用 SFTP 子系统，不依赖交互式终端。
2. 在原生认证窗口输入 SSH 密码 / 私钥口令。服务器身份校验与终端使用相同的已知主机记录，首次指纹需独立核实，指纹变化会阻止连接。
3. 打开 SFTP 即默认显示「此电脑」的全部磁盘，不再打开用户目录，也不需要先连接远端或手动选择目录。双击磁盘进入，磁盘根目录点击上级可返回全部磁盘，左栏电脑按钮也可随时返回；支持输入完整磁盘路径跳转。选择实际磁盘或文件夹后才能下载，「此电脑」不是下载目标。可通过「选择文件夹」切换本地位置，网络共享需通过该按钮明确选择；手动选择的文件夹仍以该目录为浏览边界。本次运行切换页面后保留当前位置。无介质、未就绪或无权限的磁盘打开时会显示错误，不会自动进入其他目录。
4. 双击文件夹或按 Enter 进入，使用向上按钮返回；路径栏输入完整路径后按 Enter 跳转。两栏可独立筛选名称，点开头的文件默认隐藏。
5. 单击选择文件，Ctrl+单击或勾选复选框进行多选，点击「上传」或「下载」。队列显示方向、字节进度和结果，可取消排队中或传输中的任务；每个 SFTP 连接串行执行操作，长传输期间目录刷新可能等待。
   也可将左栏文件拖到右栏上传，将右栏文件拖到左栏下载；拖到文件夹行时以该文件夹为目标，否则使用当前目录。支持多选后一起拖动，目标区域高亮并提示操作，拖放只复制、不移动源文件。「此电脑」背景不能接收下载，但可直接拖到实际磁盘行。可从 Windows 资源管理器直接拖入文件到右栏上传；原生端仅接受 WebView2 提供的真实 File 对象并逐项校验，不接受网页伪造的本地路径，不会改变左栏浏览位置。
6. 右栏支持新建文件夹、单项重命名和确认删除。删除不经过回收站，只允许普通文件、链接本身或空目录，不递归删除。传输期间禁止修改该连接的远端文件。

安全与范围：仅传输普通文件，不递归传输文件夹、不跟随文件符号链接；本地目录联接和重解析点不可浏览。本地同名或远端同名文件一律拒绝覆盖，请先重命名或选择其他目录。临时文件在成功后重命名为正式文件，失败 / 取消时尝试清理；网络中断、断电或强制退出可能留下 `.termlume-*.part`，请核对后手动清理。取消接收 / 写入会在当前网络操作返回后生效。拖放下载的目标是应用内左栏，不支持直接拖出到资源管理器；外部拖入需要支持 `postMessageWithAdditionalObjects` 的 WebView2 Runtime。暂不支持断点续传、权限编辑和文件夹递归传输。单目录最多 10,000 项，队列最多 100 个活动任务。关闭 SFTP 会取消其传输，但不影响 SSH 终端；退出应用会提示并断开全部连接。

SFTP 活动会记录连接结果、文件操作类型、传输方向和完成字节数，归入连接日志；不记录文件内容、文件名或完整传输路径。本地文件夹和远端当前目录不写入工作空间。

## 图标源文件

矢量源文件位于 `TermLume.Windows/Assets/TermLume.svg`，多尺寸 Windows 图标为 `TermLume.Windows/Assets/TermLume.ico`。修改图形时同步调整生成脚本的形状定义，在 Windows 上执行 `./tools/Generate-Icon.ps1` 可重新生成 ICO、PNG 和页面 SVG 标识。新版兼容旧配置，退出旧版后打开新版即可继续使用已有主机和信任指纹；旧记录没有核验时间时显示为未知，不虚构历史日期。使用新版后请勿交替运行旧版，旧版保存时不识别新增的分组和核验元数据。

## 已知主机与日志

- 「已知主机」按地址、端口及协商的主机密钥算法分别保存信任记录。原有指纹变化时不会自动替换，连接会被阻止；核实服务器确实换钥后，再移除对应旧记录并重新连接确认。移除信任不会中断现有会话。
- 核验时间记录的是 SSH 握手中的服务器身份核验，不代表用户认证成功。「日志」中的连接成功事件表示交互式终端已就绪。
- 日志位于 `%LOCALAPPDATA%\TermLume\activity-log.json`，为本地明文，最多保留最近 2,000 条事件，达到上限自动淘汰旧事件；不是终端录屏、命令历史或防篡改审计系统。异常退出可能没有正常退出 / 断开事件。
- 日志不收集密码、私钥内容或口令、终端输入 / 输出，也不直接持久化异常原文。记录可能包含主机名称、地址、用户名、指纹、会话 ID 和连接时长；导出及分享前请检查。
- 导出包含当前筛选条件下的全部匹配事件，不限于当前页，通过系统保存对话框选择 JSON 文件。清空会删除全部日志，不仅是当前筛选结果；不会删除主机配置或信任记录。
- 日志文件损坏时显示警告，不静默覆盖原文件；新事件暂存内存。先备份损坏文件，再确认清空以重建。磁盘写入失败也会在日志页提示，不因此阻断 SSH。

## 数据与安全边界

- 配置：Windows 为 `%LOCALAPPDATA%\TermLume\workspace.json`，macOS 为 `~/Library/Application Support/TermLume/workspace.json`；主机地址、用户名、备注、命令片段、私钥路径和信任指纹均为本地明文。请勿在备注或片段中保存秘密。
- 活动日志同样是本地明文；备份时按需同时保存 `activity-log.json`，不会上传到云端。
- 不持久化密码或私钥口令，不复制私钥、不云同步。认证库使用托管字符串，不能承诺密码在内存中被即时清零。
- RSA 私钥使用 SHA-2 签名认证，不尝试旧的 `ssh-rsa` / SHA-1 签名或 DSA。仅支持这些旧算法的服务器需要先升级服务器配置。
- 首次信任（TOFU）不是自动验证服务器身份；指纹应由独立可信渠道确认。
- 同一地址和端口已有身份时，未信任的新密钥算法同样阻止连接，不会重新弹出首次信任提示；核验服务器变更后，需在已知主机中显式移除该端点旧记录再重新登记。已有多算法信任记录仍可正常核验。
- 单个终端的 SSH 接收缓冲在分配前限制为 4 MiB，包含 PTY 建立阶段；超限仅断开对应会话。界面单批输出确认超过 30 秒也会断开连接，防止无响应界面长期占用连接资源。
- WebView2 只加载本地虚拟域名资源，禁止外部导航、弹窗、下载和权限申请；页面采用 CSP，不从 CDN 加载脚本。终端输出交由 xterm.js 解析，不插入 HTML。
- macOS 使用隔离、沙箱化的 Electron 页面，只加载随应用分发的 `termlume://app/` 资源；原生桥校验主窗口与主 frame、操作白名单和消息上限。后端通过子进程标准输入 / 输出通信，不监听本地网络端口；密码由独立受限窗口交给连接后端，不注入主工作台。
- 将配置备份到安全位置。应用退出后可复制 / 恢复 `workspace.json`。损坏的配置会阻止启动，不会被静默覆盖。
- 不含遥测或应用层云端请求；WebView2 Runtime 自身由 Microsoft 管理。

## 当前范围

当前支持真实 SSH 终端与 SFTP 文件管理。暂不包含跳板机、代理、端口转发、SSH agent、键盘交互 / MFA、密钥生成、云同步和自动更新；不承诺支持所有 SSH / SFTP 服务器或认证配置。

## 项目结构

三个 .NET 项目按职责命名：`TermLume.Windows` 是 Windows 桌面入口，`TermLume.DesktopBackend` 是供 Electron 桌面壳调用的后台进程，`TermLume.Shared` 是两端共用的业务类库。Windows 直接调用共享类库，不启动桌面后端。

项目更名不改变产品标识：Windows 仍生成 `TermLume.exe`，安装器升级标识、应用名称及用户数据目录保持不变。

| 路径 | 用途 |
| --- | --- |
| `TermLume.Shared/Models` | 两个平台共用的主机、分组、偏好及其他数据结构 |
| `TermLume.Shared/Services` | 配置持久化、SSH / SFTP 会话、日志及本地文件浏览 |
| `TermLume.Shared/Application` | 唯一的工作区请求处理、连接生命周期、传输调度和日志导出实现 |
| `TermLume.Shared/Abstractions` | 原生交互与线程调度接口，不依赖 WPF、Electron 或进程通信 |
| `TermLume.Windows/Views` | 原生凭据对话框和本地化界面支持 |
| `TermLume.Windows/MainWindow.xaml.cs`、`TermLume.Windows/Platform` | Windows 窗口、WebView2 消息桥及原生对话框、线程调度适配 |
| `TermLume.Windows/Web` | 工作台、设置、双栏文件管理及本地终端资源 |
| `TermLume.DesktopBackend` | macOS 桌面使用的 .NET 进程入口、标准输入 / 输出桥及线程调度，项目引用共享核心 |
| `desktop` | Electron 桌面入口、受限预加载桥、macOS 凭据窗口及 DMG / ZIP 构建脚本 |
| `.github/workflows/macos.yml` | Apple Silicon / Intel 构建、回归验证与工作流安装包附件 |
| `TermLume.Windows/Assets` | 应用图标及源素材 |
| `tools/Build-Installer.ps1` | 构建自包含 Windows 安装包 |
| `tools/installer` | Inno Setup 安装 / 卸载脚本与安装器语言文件 |
| `tools/Test-Installer.ps1` | 隔离的安装、升级、数据保留及卸载清理验证 |
| `tests/TermLume.SecurityTests` | 隔离的主机身份、输出洪泛及 SSH / SFTP 回归测试 |
| `tests/TermLume.PortableTests` | 在 macOS / Windows 上复用的核心安全与文件边界回归测试 |
| `desktop/tests` | IPC 安全、平台快捷键、真实 SSH / SFTP 桥接与隔离原生窗口验证 |
| `tests/snippets.test.cjs` | 模拟终端的片段使用、执行确认与安全边界回归测试 |
| `vendor/packages`、`tools/Build-SshNet.ps1` | 固定来源的 SSH.NET 安全补丁及重建脚本 |
| `artifacts/` | 本地生成的 Windows EXE、macOS DMG / ZIP，整个目录不提交 Git |

Windows 与 macOS 通过 `ProjectReference` 使用同一份 `TermLume.Shared` 类库，不再链接编译服务源码或维护两套业务控制器。用户数据目录与 JSON 格式不变，升级无需迁移工作空间文件。详细分层和开发约定见 [架构说明](docs/architecture.md)。

第三方终端资源许可证位于 `TermLume.Windows/Web/vendor`。本地构建产生的中间产物不属于发布文件，个人配置始终保存在仓库以外的应用数据目录。
