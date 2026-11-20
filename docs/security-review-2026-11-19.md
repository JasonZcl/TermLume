# 2026-11-19 安全检查

检查当前工作区中的 0.8.0 源码、PostgreSQL 权限和 Windows 本地部署。确认并修复两类代码 / 配置问题，并清理一条已公开的构建依赖漏洞链。2026-11-20 已将桌面修复打入 [0.8.1 Windows 安装包](releases/0.8.1.md)，此前的 0.8.0 安装包保持原样。本机 `.local` 权限调整已经生效，源码尚未提交 Git。

## P1：服务端数据库凭据继承了宽松的本机权限

原启动脚本创建 `.local` 后直接写入连接配置，继承了项目所在磁盘的权限。实查 `.local/api-settings.local.json` 等文件包含 `BUILTIN\Users` 的读取权限，以及 `NT AUTHORITY\Authenticated Users` 的修改权限。其他本机账号可能读取数据库运行 / 迁移角色密码，通过本机 PostgreSQL 端口访问业务数据，或篡改服务端配置。此项为本机权限问题，未发现凭据实际被读取的证据。

新增 [Protect-WorkspaceLocalDirectory.ps1](../tools/Protect-WorkspaceLocalDirectory.ps1)，在启动、备份、恢复和 Docker 配置准备前设置目录权限。Windows 仅向当前用户、SYSTEM 和 Administrators 授予访问，关闭宽松继承；只修改 DACL，不申请审计权限。其他平台设置目录模式 700；拒绝将符号链接或 junction 用作该私密目录。

已经验证旧配置与备份的继承权限、新文件继承、PowerShell 5 / 7 重复调用，以及权限收紧后的备份和非 root Docker API 数据库连接。

## P2：旧账号的异步失败可以影响新账号工作区

在账号 A 的工作区读取挂起时切换至 B，再让旧请求返回 403，原生端会进入成员失效处理，并以当前账号计算路径，将 B 的 `selected-workspace.json` 改写为 `null`。刷新失败的路径同样缺少代次检查，可能发出不属于新账号的锁定事件。页面端旧工作区加载 Promise 的失败回调，也会再次锁定后来打开的工作区。

已经在 [WorkspaceController.Synchronization.cs](../TermLume.Shared/Application/WorkspaceController.Synchronization.cs) 的失败分支及异步清理后重新核验账号 / 工作区代次；切换工作区时保留捕获的代次，防止后续加载使用新账号。页面在接收不同账号身份时先清理旧工作区，并忽略旧代次的失败回调。

新增原生端延迟 403 和页面端延迟失败回归；修复前均可复现失败，修复后通过。本轮未复现跨账号读取服务端业务数据，此问题的已确认影响是本机选择记录和界面状态被旧请求修改。

## 构建依赖：minimatch 拒绝服务公告

完整 npm 扫描发现 6 条中危告警，均来自 electron-builder 工具链中的 minimatch 9 / 10，属于同一个 [GHSA-g3hv-75wg-m249 / CVE-2026-118764](https://github.com/advisories/GHSA-g3hv-75wg-m249) 漏洞链。公告中的触发条件为攻击者控制的 glob 模式与 `optimizationLevel >= 2`，会放大 CPU / 内存消耗；修复版本为 11.0.1。当前检查到的 TermLume 打包调用未启用该参数，未复现业务入口直接触发。

通过 [desktop/package.json](../desktop/package.json) 的版本范围 override，仅将受影响的 7–10 主版本升级到 11.0.1，保留不受此公告影响的旧版本依赖；更新锁文件，未降级 electron-builder。实际调用 electron-builder 的资源筛选器与 universal 包路径匹配验证兼容性。此依赖用于 Electron 打包工具链，不存在于 Windows WPF 安装包的运行依赖中。

## 验证结果

- Windows 解决方案构建：0 警告、0 错误。
- 认证 / 协作 / 隔离：198 项通过，含新增延迟响应回归。
- SSH / SFTP 安全回归：78 项通过。
- PostgreSQL 实库集成：20 项通过，使用独立测试业务库，覆盖伪造令牌、用户隔离、成员权限、设备撤销、并发和持久化。
- JavaScript：32 项通过，含新增页面账号边界回归。
- 桌面后端集成：2 项通过；Electron 登录、注销清理、原生桥接与七个管理页面冒烟通过。
- npm 完整依赖扫描：6 条中危降为 0；NuGet 四个业务项目及其传递依赖扫描未发现已知漏洞。
- Git 可提交范围的源码未检出本地数据库与 IAM 的实际密码 / 密钥值；检查过程没有输出这些值。
- 本机 API 健康检查、收紧权限后的非 root Docker API 读取配置和 PostgreSQL 备份通过。

本轮同时复核了 PKCE / nonce / issuer / 令牌用途验证、参数化 SQL、工作区 RLS、成员和设备访问控制、指纹采用确认、导入导出、页面转义和原生桥接。没有复现认证绕过、SQL 注入或跨账号业务数据读取。结论限于本次源码检查和列出的测试；未进行 macOS 实机验收。
