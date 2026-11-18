# Aegis-IAM 接入安全检查（2026-11-18）

本次检查覆盖 TermLume 的 OIDC 客户端、登录门禁、Windows / Electron 原生桥接、退出登录时的异步请求，以及 NuGet / npm 依赖公告。确认并修复了两个中等风险的认证边界问题，另有两类打包依赖公告尚未消除。风险评级按本项目实际触发条件评估，不表示存在无需交互的远程利用路径。

## 已修复：原生接口绕过登录门禁

**影响**：Windows 的 `MainWindow.OnMessage` 和 Electron 的 `termlume:request` 曾直接处理 `copy`、`paste`、`pickKey`，绕过共享控制器。能够在受信任的主页面发送桥接请求的代码，即使没有登录，也可以读取或改写剪贴板、打开私钥选择器；私钥选择器返回路径，不直接返回私钥内容。这违反“必须登录后才能使用工作空间”的权限边界。

**复现证据**：修复前的 Electron 集成测试在未登录状态调用 `request('paste')`，能够成功返回测试替身的剪贴板内容。测试使用合成内容替换系统剪贴板，没有读取或覆盖真实剪贴板。

**修复**：上述三种请求统一进入 `WorkspaceController.HandleAsync`，通过认证后才使用 `IWorkspaceHost` 调用原生适配器。Windows 和 Electron 不再直接向页面提供这些能力。共享控制器同时校验复制内容的类型和 1 MiB 长度限制；异步原生结果返回时重新检查账号状态和认证代次。

**回归覆盖**：登录前与退出后的三种请求均被拒绝，原生处理器调用次数为零；已登录的复制、粘贴、取消选取仍可用；账号变更后不交付旧对话框返回的私钥路径。

## 已修复：异步鉴权与退出登录之间的竞态

**影响**：工作区请求曾先等待 `ValidateAsync()`，再读取认证代次；缺少等待结束后的状态检查。验证期间退出登录或变更账号后，旧的成功验证快照可能使 `saveGroup` 等操作继续写盘。操作末尾的检查无法撤销已发生的磁盘修改，且可能已读取到新账号的代次。

**复现证据**：控制器测试暂停验证，发起退出登录或切换账号，再返回旧的已认证快照。修复前 `workspace.json` 实际出现测试分组，证明存在副作用越过认证边界。

**修复**：在异步验证前记录认证代次；验证结束后、派发业务操作前，同时检查当前登录状态、退出状态和认证代次，并在异步业务操作返回后再次检查。退出进行中对外始终显示未登录；重复退出请求不能提前解除退出保护。

**回归覆盖**：退出尚未完成、认证对象仍保留旧状态，以及账号已经变更两种场景，都拒绝旧请求并保持磁盘不变；旧原生对话框结果也被丢弃。

## 尚存的打包依赖公告

`npm --prefix desktop audit --json` 返回 **11 个依赖节点告警（6 high、5 moderate）**，来自以下两条根公告及其依赖链，不能解释为 11 个独立漏洞：

| 依赖 | 本项目涉及版本 | 公告级别 | 修复情况 |
| --- | --- | --- | --- |
| minimatch | 9.0.9、10.2.6 | Moderate | 官方修复版本为 11.0.1 及以后；当前 electron-builder 26.18.0 仍引用受影响版本 |
| picomatch | 4.0.7 | High | 检查时官方公告标记为无修复版本，npm 最新版仍为 4.0.7 |

minimatch 的公告涉及恶意 glob 模式导致 CPU / 内存消耗，触发条件包括 `optimizationLevel >= 2`；本次检查到的打包器匹配调用使用默认优化参数。picomatch 的公告涉及重复 extglob 导致正则回溯和事件循环阻塞。来源：[minimatch 官方安全公告](https://github.com/advisories/GHSA-g3hv-75wg-m249)、[picomatch 安全公告](https://github.com/advisories/GHSA-fc5q-39xc-vjpf)。

当前依赖路径位于 `electron-builder → app-builder-lib → @electron/universal / @electron/rebuild → node-gyp → tinyglobby` 等构建工具链。没有发现工作区、终端输出或未登录页面把输入传给这些 glob 处理器的路径。实际风险集中在打包环境处理不可信构建配置或模式时；这不是已证实的桌面登录绕过。

本次保留锁文件，没有强制跨主版本覆盖构建依赖或降级打包器。后续应采用包含已修复 minimatch 的受支持依赖组合，验证两种 macOS 架构的打包；picomatch 则需跟进上游补丁。在此之前，构建配置和 glob 模式应只来自可信仓库内容。

`dotnet list TermLume.sln package --vulnerable --include-transitive --format json` 未报告已知 NuGet 漏洞。该结果受漏洞数据库覆盖范围限制，不覆盖操作系统、运行时、浏览器引擎或本地修改包的全部风险。

## 验证记录

- `dotnet build TermLume.sln -c Release --nologo`：通过，零警告、零错误。
- `dotnet run --project tests/TermLume.AuthenticationTests -c Release -r win-x64`：100 项通过，包含本次新增的竞态、原生权限和延迟结果回归。
- `dotnet run --project tests/TermLume.SecurityTests -c Release -p:SelfContained=true`：78 项通过，使用隔离的 Paramiko 测试环境。
- `node --test tests/snippets.test.cjs desktop/tests/security.test.cjs desktop/tests/shortcuts.test.cjs`：29 项通过。
- `node --test desktop/tests/backend.integration.cjs`：2 项通过，覆盖认证后的 SSH / SFTP 桥接和损坏配置保护。
- Electron 隐藏窗口集成测试：通过，验证完整浏览器登录回调、登录前后原生权限、账号显示、退出、沙箱和原生凭据取消；系统剪贴板与文件选择器使用测试替身。

## 边界与待验收项

- 当前同一操作系统用户下的 IAM 账号共用本地工作区，工作区文件没有按 IAM 账号隔离或加密。登录门禁不能阻止有本地文件访问权的进程读取配置；如需多账号数据保密，应另行设计账号存储隔离及密钥管理。
- 服务端撤销通过周期性 UserInfo 验证发现，存在约 60–90 秒检测窗口，网络超时还会影响上界；本机主动退出立即禁止新请求。它不提供服务端撤销后的瞬时断线保证。
- 协议测试使用临时密钥和本地 OIDC 夹具。真实 Aegis-IAM 服务未启动，本次未进行真实用户 / MFA / 反向代理与部署环境测试，也未对整个 Go 服务做完整渗透测试。仅只读核对了相关 OAuth 服务端代码，未修改 Aegis-IAM。
- Windows 已构建并执行测试；macOS 系统浏览器、回环网络和安装包仍需真机验收。

后续测试包验证：同日 Docker 测试环境启动后，已使用共享认证核心完成真实 IAM 的 PKCE 登录、签名校验、UserInfo 和退出撤销。此前“未启动真实服务”的描述仅指本次安全检查当时的环境；后续结果及尚待手工验收的项目见 [Aegis-IAM 对接验证](aegis-iam.md#验证)。

会话持久化补充：针对正常关闭后丢失登录的问题，新增 Windows 凭据管理器 / macOS 钥匙串存储，仅持久化与服务、客户端及账号绑定的刷新凭据。重新打开必须完成刷新、签名与 UserInfo 校验才能开放工作区；显式退出会删除本机凭据。Windows 原生凭据读写、并发恢复、异常身份、服务端撤销、退出及后端进程重启已覆盖；139 项认证回归通过，本地 Docker IAM 的关闭后恢复验证通过。macOS 钥匙串仍需对应系统的真机验收。
