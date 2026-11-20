# 开发与提交约定

## 组件与环境

共享业务逻辑位于 `TermLume.Shared`，WPF 和 Electron 入口通过平台接口使用它。工作区后端位于 `TermLume.Api`，使用 ABP Framework；桌面版本与 API 版本独立维护，更新时同时核对健康接口、能力接口、镜像标签和发布说明。

Windows 本地开发需要 .NET 10 SDK、Node.js 24、Docker Desktop，以及同级 Aegis-IAM 项目的 Docker 测试环境。服务器配置由工具生成到 `.local/`，默认只使用 `termlume` 业务库。具体步骤见 [PostgreSQL 工作区](docs/postgresql.md) 和 [代码架构](docs/architecture.md)。

## 修改代码

- 跨平台校验和认证边界放在共享核心；平台工程只处理系统交互。
- 账号或工作区切换后的异步回复、文件对话框和凭据窗口，必须重新验证捕获的代次后才能写盘或更新当前状态。
- API 身份只能来自 Aegis 校验结果。客户端传入的用户、设备和工作区标识不能直接成为授权依据。
- 仓储使用参数化 SQL 和事务内 RLS 上下文，保持连接池隔离。已有迁移 SQL 连同注释均不可修改，新增结构变化使用下一份迁移。
- 注释说明权限、生命周期和兼容性约束；协议或部署行为改变时同步更新文档和有实际覆盖价值的回归测试。
- 自包含运行时基线当前为 10.0.15；更新时同步各宿主与测试项目、安装脚本、Dockerfile 和说明。NuGet / npm 审计之外，单独检查运行时及容器基础镜像公告。

## 验证

```powershell
dotnet build TermLume.sln -c Release
dotnet run --project tests/TermLume.AuthenticationTests -c Release
node --test tests/*.test.cjs
npm --prefix desktop ci
npm --prefix desktop test
npm --prefix desktop run test:backend
```

SSH / SFTP 测试的隔离 Python 环境按根目录 README 配置后运行 `dotnet run --project tests/TermLume.SecurityTests -c Release`。涉及 API、数据库或权限的变更，另运行 `node --test tests/workspace-api.integration.cjs`，并用 `TERMLUME_TEST_DOCKER_IMAGE` 指定构建后的镜像重复验证。测试使用隔离临时目录和可清理的测试身份；不要在常规测试中创建长期保留的业务库。

## 提交范围

提交源码、公开配置样例、版本锁文件、SQL 迁移，以及 `vendor/packages` 中固定来源的 SSH.NET 包和补丁脚本。`.local/`、安装包、编译产物、数据库备份、实际环境文件、日志、私钥和密码配置保留在本地。

提交前检查 `git status --short`、`git diff --stat` 和 `git diff --check`，同时查看未跟踪文件。`.gitignore` 不会移除已经跟踪的文件，因此发现真实凭据时还需核查 Git 历史并处理泄露的凭据。建议的本次提交描述见 [提交说明](docs/commit-message.md)。
