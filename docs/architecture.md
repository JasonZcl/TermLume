# 代码架构与开发约定

## 分层与依赖

项目名用于区分平台和职责，产品仍叫 TermLume。Windows 项目显式保留 `AssemblyName=TermLume`，因此可执行文件仍是 `TermLume.exe`；桌面后端生成 `TermLume.DesktopBackend`，共享类库生成 `TermLume.Shared.dll`。


```text
Windows: Web UI → WebView2 → WPF 平台适配 ─────────┐
                                                ├→ TermLume.Shared → SSH.NET → 服务器
macOS:   Web UI → Electron → JSON Lines → Backend ┘

TermLume.Shared → HTTPS / 本机 HTTP → TermLume.Api → PostgreSQL
                                    └→ Aegis-IAM（令牌与实时授权校验）
```

- **共享界面**：`TermLume.Windows/Web` 保存 HTML、CSS、原生 JavaScript 和本地 xterm.js 资源。`platform.js` 处理平台显示差异，`i18n.js` 处理中英文切换。
- **共享核心**：`TermLume.Shared` 是 `net10.0` 类库，由 Windows 应用和后端通过 `ProjectReference` 引用。模型、服务与请求处理只有一份实现，不使用链接编译来复制核心源码。
- **业务 API**：`TermLume.Api` 基于 ABP Framework 10.7.1，通过 `TermLumeApiModule` 配置服务与管线、Autofac 注入 `WorkspaceAppService`，由显式 MVC 控制器保持 `/v1/workspace` 桌面协议。标准认证处理器验证 Aegis 访问令牌与实时授权，再由应用服务按账号访问 PostgreSQL。它与桌面后台进程是独立服务，数据库凭据只存在于服务器配置。
- **Windows 入口**：`TermLume.Windows` 是 WPF / WebView2 应用，负责窗口、系统剪贴板、文件选择和原生对话框。
- **macOS 入口**：`desktop` 负责 Electron 窗口与系统能力；`TermLume.DesktopBackend` 只负责启动核心、JSON Lines 通信和工作区线程调度，除账号登录期间的 IPv4 回环回调外，不启动本地网络服务。

## 核心目录

| 目录 | 职责 |
| --- | --- |
| `TermLume.Shared/Models` | 持久化模型及相关数据结构；使用 `TermLume.Shared.Models` 命名空间 |
| `TermLume.Shared/Services` | SSH / SFTP、认证、文件边界、信任、配置、日志、设置和翻译服务；使用 `TermLume.Shared.Services` 命名空间 |
| `TermLume.Shared/Application` | `WorkspaceController`：工作区请求、会话生命周期、传输队列、信任确认和日志导出 |
| `TermLume.Shared/Abstractions` | `IWorkspaceHost` 和 `IWorkspaceDispatcher`：平台必须提供的能力 |

核心不能引用 WPF、WebView2、Electron 或 `TermLume.DesktopBackend`。应用和后端只引用核心，核心不能反向引用入口项目。SSH.NET 的固定包源和安全修订仍由仓库根目录 `NuGet.config` 管理。

## 平台适配

### 原生交互

`IWorkspaceHost.Send` 发送请求回复和状态事件；`RequestAsync` 请求凭据、首次信任确认、安全提醒、文件夹选择、日志保存位置、剪贴板读写和私钥文件选择。

- Windows 的 `Platform/WindowsWorkspaceHost` 调用 WPF 原生窗口和系统文件对话框。
- 后端的 `DesktopRpc` 发送 `hostRequest`，Electron 主进程执行原生操作，再通过 `hostReply` 返回结果。
- 密码取消返回 `null`；空字符串仅表示用户明确提交空口令。原生适配器不得保存凭据或将凭据写入配置、日志、命令行参数。
- 系统剪贴板和私钥文件选择由共享控制器统一鉴权后调用桌面壳；原生实现保留在平台适配器。窗口控制由桌面壳直接处理。

### 调度与退出

`WorkspaceController` 的请求和生命周期操作必须在同一个工作区线程处理。`IWorkspaceDispatcher` 将 SSH / SFTP 后台回调送回该线程。Windows 使用 WPF Dispatcher，后端使用自己的单线程 SynchronizationContext。

主机指纹校验需要同步等待用户决定：Windows 原生交互在 UI 线程返回已完成任务；后端必须在读取线程直接完成 `hostReply`，不能把回复排到正在等待的工作区线程，以免死锁。SSH / SFTP 服务在后台启动 SSH.NET 建连，避免库中同步握手或异常清理路径占用工作区线程，导致主机指纹回调无法调度。

退出时由平台展示确认框，再调用同一份 `ShutdownAsync`。Windows 在异步清理期间取消首次窗口关闭事件，清理完成后再次关闭，避免重复确认；核心取消传输并释放 SSH / SFTP 会话。

### 拖放和文件边界

`HandleAsync(request, nativePaths)` 的第二个参数只能来自原生可信文件对象：Windows 使用 WebView2 的 `CoreWebView2File`，Electron 使用预加载层取得的真实 File 路径。核心不读取网页请求自行声明的 `nativePaths` 字段，仍需逐项验证路径、普通文件类型、会话与队列容量。

日志导出、主机参数、已知主机核验、SFTP 文件边界和拒绝覆盖规则统一由核心执行，不能只在 JavaScript 或平台对话框中校验。

## 数据兼容与资源

项目与命名空间调整不改变工作区 JSON 字段或用户数据目录：

- Windows：`%LOCALAPPDATA%/TermLume/`。
- macOS：`~/Library/Application Support/TermLume/`。

程序目录和版本号不参与用户数据定位。根目录旧文件保留；PostgreSQL 工作区使用 `accounts/<账号与服务标识>/` 保存每个账号的本机缓存、私钥路径、信任和日志，旧数据通过明确确认导入，不自动归属第一个登录账号。Web 资源继续由 Windows 工程复制、Electron 打包脚本收集；后端同时携带原生翻译所需的本地词典。`artifacts/` 只放最终安装包，保持 Git 忽略，正式安装包通过 GitHub Releases 发布。

## 开发与验证

新增跨平台功能时，优先在 `TermLume.Shared/Application` 增加请求处理和校验，再修改共享 Web 界面。确实需要新的系统能力时，才扩展原生交互接口并实现两端适配，不另建一套业务控制器。

```powershell
# Windows：同时构建两个入口和共享核心
dotnet build TermLume.sln -c Release

# 跨平台：只构建核心与后端，不构建 WPF 工程
dotnet build TermLume.DesktopBackend/TermLume.DesktopBackend.csproj -c Release

# 配置隔离的测试 Python 环境后执行，具体步骤见根目录 README
dotnet run --project tests/TermLume.SecurityTests -c Release -p:SelfContained=true
dotnet run --project tests/TermLume.PortableTests -c Release -r win-x64
node --test tests/snippets.test.cjs
npm --prefix desktop test
npm --prefix desktop run test:backend
npm --prefix desktop run test:desktop
```

macOS 的 PortableTests 使用 `-r osx-arm64` 或 `-r osx-x64`。共享控制器测试使用临时目录和模拟原生适配器，覆盖旧数据读取、主机 / 分组 / 片段、偏好回滚、凭据取消、文件边界、导出保护和退出状态；SSH / SFTP 集成测试只连接隔离的本地测试服务器。Windows 测试或交叉编译不能代替 macOS 真机安装和系统交互验收。

## 账号认证

共享核心的 `AegisAuthentication` 管理 OIDC 授权码 / PKCE、签名校验、刷新轮换、UserInfo 校验和撤销，令牌不进入 Web 界面。`WorkspaceApiClient` 从后端的 `IWorkspaceAccessTokenProvider` 获取访问令牌，通过受信配置的业务 API 读写工作区；不把数据库连接串交给桌面。`IAegisSessionStore` 保存与 issuer、client ID 和账号绑定的刷新凭据；生产实现 `SystemAegisSessionStore` 调用 Windows 凭据管理器或 macOS 钥匙串，不依赖 WPF 或 Electron。按数据目录隔离的进程锁防止多个实例消费同一刷新凭据，数据目录内仅保存无令牌的启用标记。启动时使用保存的凭据刷新并完成签名与 UserInfo 校验后才解锁；关闭仅释放会话，退出登录则删除系统凭据并请求服务端撤销。`WorkspaceController` 在处理所有工作区操作前检查账号授权；退出或失效会关闭 SSH / SFTP。`IWorkspaceAuthentication` 和 `IAegisSessionStore` 供隔离测试替换依赖，两个生产入口始终使用真实 Aegis 实现，没有免登录开关。Windows / Electron 负责打开系统浏览器。部署与协议说明见 [Aegis-IAM 对接](aegis-iam.md)。

## 0.8.0 工作区协作

`WorkspaceController` 按账号及工作区隔离缓存、信任、日志和 `WorkspaceOutbox`。队列先落盘，服务器以操作 ID 去重并以修订号检测冲突。工作区切换在认证代次之外也更新访问代次，使旧网络回复、凭据窗口和文件对话框失效，并关闭连接与传输。

`TermLume.Api` 使用数据库事务、RLS 和固定 search_path 的权限函数执行团队成员、设备、历史、审计与信任参考管理。历史只保存共享资产，个人偏好与本机信任不随恢复回退。客户端上报日志会被双重过滤；服务端审计由 API 生成，运行角色仅可追加读取。完整部署、备份恢复与权限说明见 [PostgreSQL 工作区](postgresql.md)。

## 0.9.0 ABP 工作区服务

服务端采用单项目模块结构：`Application/WorkspaceAppService.cs` 实现 ABP 应用服务，`HttpApi/` 提供控制器与兼容桌面端的安全错误响应，`Security/` 将已验证的 IAM 身份映射为标准认证主体及请求作用域上下文。身份、设备和工作区选择始终从已认证请求读取，应用服务仍执行授权检查。

已有 Npgsql 仓储继续负责事务内 RLS 上下文、操作去重和修订号。应用服务及控制器禁用自动 ABP 工作单元，避免与这些显式事务重叠；SQL 迁移及其历史校验值保持稳定。框架参数审计关闭，业务审计由原有仓储写入。仅发现业务及健康控制器，ABP 基础设施 API 不加入路由；严格 JSON、请求大小限制、Origin 拒绝和实时授权校验均有集成测试覆盖。

`tools/Publish-WorkspaceDocker.ps1` 先备份已有 `termlume` 库，再准备配置、构建 `termlume-workspace-api:0.9.0`、验证迁移并启动容器。容器以非 root 身份和只读文件系统运行，数据库配置只读挂载，健康探针访问实际数据库。Docker 与本机开发服务使用相同的 `18741` 端口，部署脚本会关闭自己启动的本机 API。
