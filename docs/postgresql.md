# PostgreSQL 工作区（服务端 0.9.0 / 桌面端 0.8.1）

桌面程序通过基于 ABP Framework 10.7.1 的 `TermLume.Api` 访问 PostgreSQL，必须先通过 Aegis-IAM 登录。安装包只携带 API 地址和公共客户端配置；数据库密码只在服务端保存。所有账号和工作区共用一套 API 及 `termlume` 业务库。

## 已接入的数据与功能

| 数据 / 功能 | 保存位置与行为 |
| --- | --- |
| 账号与个人工作区 | 验证后的 IAM `issuer + sub`；个人工作区始终保留 |
| 共享工作区 | 所有者、管理员、编辑者、只读成员；一次性邀请、退出、成员移除、所有权转让 |
| 主机、分组、片段 | 工作区共享；编辑者以上可维护，只读成员可连接 |
| 收藏、最近 SSH 连接 | 按账号、工作区和主机隔离，成员之间不共享 |
| 偏好 | 通用偏好随账号同步；快捷键、字体与侧栏分别保存 Windows / macOS 配置 |
| 配置历史 | 最近 50 个资产版本，可预览和恢复；恢复生成新版本，不回退个人偏好和本机信任 |
| 设备 | 安装实例随机标识、名称、平台、版本和访问时间；与 OAuth grant 绑定，可撤销 |
| 跨设备活动日志 | 仅当前账号自己的事件，服务端保留 7 天；可暂停上传或清空云端个人日志 |
| 服务端审计 | API 生成的配置、成员、安全操作，保留 7 天；所有者 / 管理员可看，运行角色不能任意修改或删除 |
| 指纹参考 | 管理员发布；成员在可信渠道核验并通过原生确认后才能采用到本机 |
| 离线修改 | 本机持久队列，最多 200 项 / 4 MiB；恢复服务后重试，操作 ID 去重，冲突需显式处理 |
| 导入 / 导出 | 原生文件对话框；导出当前草稿，导入合并配置；不含私钥路径和本机信任 |
| SSH 凭据 / 私钥 | 密码与口令仅连接期间使用；私钥文件不复制，本机路径不上传 |
| 会话 / 传输队列 | 运行时状态，不保存到数据库，不在设备间迁移 |
| 登录刷新凭据 | Windows 凭据管理器 / macOS 钥匙串，不进入页面、JSON、日志或数据库 |

跨设备日志会在客户端和服务端双重过滤：只保留事件代码、时间、主机等有限元数据，以及白名单数值、方向和指纹。消息原文、异常详情、任意字段、路径和终端输入输出不会上传。主机地址和用户名仍属于可同步元数据；不要在主机名、备注或命令片段中保存秘密。

服务端业务日志按连续 7 × 24 小时保留：活动日志以服务端 `received_at` 为准，审计以 `created_at` 为准。过期记录立即从查询结果排除；API 启动时及每小时执行物理清理，包括不再登录或上传的账号。停机期间暂停清理，恢复服务后补做。数据库提供无参数、固定期限的清理函数，运行角色仍没有审计表的 UPDATE / DELETE 权限。本机活动日志维持最多 2,000 条；数据库备份和容器运行输出由各自运维策略管理。

## 启动本机测试 API

先启动上一级 `Aegis-IAM` 项目的 Docker 测试环境，然后在本仓库运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Start-WorkspaceApi.ps1
Invoke-RestMethod http://127.0.0.1:18741/healthz
```

脚本从 `../Aegis-IAM/.env.test` 读取端口与 issuer，通过已有 PostgreSQL 容器中的管理员环境初始化独立业务库，不修改 IAM 的数据库和客户端登记。密码经标准输入传递，不进入命令行或控制台。

| 项目 | 本地测试值 |
| --- | --- |
| PostgreSQL 容器 | `aegis-iam-test-postgres-1` |
| PostgreSQL 地址 | `127.0.0.1:54330`，取自 IAM 测试配置 |
| 数据库 / schema | `termlume` / `termlume` |
| 迁移角色 | `termlume_owner`，仅用于迁移 |
| API 运行角色 | `termlume_app`，非超级用户、非表所有者、不可绕过 RLS |
| API | `http://127.0.0.1:18741` |
| IAM / 客户端 | `http://localhost:18080` / 公共客户端 `termlume-desktop` |

服务端配置及随机角色密码位于 Git 忽略的 `.local/`。启动、备份、恢复和 Docker 配置脚本会自动收紧该目录权限：Windows 仅允许当前用户、SYSTEM 和管理员，其他平台使用 700。API 发布到 `.local/api` 并在后台运行，PID、标准输出和错误输出也保存在 `.local/`。重复启动只停止记录的、路径匹配的 API 进程。`-SkipBuild` 复用发布文件，`-PrepareOnly` 初始化并迁移后不启动。停止本机 API：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Stop-WorkspaceApi.ps1
```

桌面端读取根目录 `workspace-api.json` 的 `baseUrl`，也可设置 `TERMLUME_WORKSPACE_API_URL`。HTTPS 为默认要求，仅回环测试地址允许 HTTP。当前回环地址只服务这台电脑；其他设备需要能访问的 HTTPS API 和 IAM 地址。

## 工作区与权限

从左侧工作区按钮进入管理页。新建或凭邀请码加入后，可选择切换；切换会关闭 SSH / SFTP 连接、取消传输，各工作区待同步修改分别保留。所有者不能直接退出，须先转让给现有成员。个人工作区不能邀请成员、删除或转让。

| 操作 | 所有者 | 管理员 | 编辑者 | 只读成员 |
| --- | --- | --- | --- | --- |
| 读取配置、SSH / SFTP、个人收藏和偏好 | 是 | 是 | 是 | 是 |
| 维护主机、分组、片段、恢复历史 | 是 | 是 | 是 | 否 |
| 邀请、管理非所有者成员、指纹参考、审计 | 是 | 是 | 否 | 否 |
| 转让或删除共享工作区 | 是 | 否 | 否 | 否 |

邀请码为 256 位随机值，服务端只保存散列；默认 24 小时，API 可指定 1 至 168 小时。仅向预期成员分享。使用后其他账号不能再次领取，同一成员可安全重试丢失的加入响应。

设备撤销绑定已签发令牌的 `gid`。去掉设备头或换设备标识不能绕过撤销，刷新同一 grant 也不会恢复权限。在线桌面端在下一次请求或 30 秒检查时退出并关闭连接；再次使用需要重新登录。此操作撤销 TermLume 工作区访问，不影响用户在其他应用的登录。

## 缓存、离线与冲突

本机缓存按 `API + issuer + client ID + sub` 的散列隔离。个人目录为 `accounts/<账号标识>/`，共享目录为其下 `workspaces/<工作区 ID>/`。`workspace.json` 保存当前草稿及本机私钥路径 / 信任，`pending-changes.json` 保存已确认快照和待提交操作。切换账号或工作区会取消旧请求；旧回复和文件对话框不得写入新工作区。

API 暂时不可达时，已通过 IAM 校验且有本账号缓存的设备可以读取并修改草稿。队列先落盘，再尝试提交；新主机和片段使用稳定标识，服务端在事务中记录操作 ID。响应丢失、重启、重试不会重复创建或反复切换收藏。IAM 仍必须验证登录，缓存不会绕过 IAM；服务端拒绝授权时也不会降级为缓存登录。

冲突时暂停重放，保留草稿并提示：先「导出草稿」留存，再选择「重试同步」将操作应用到服务端最新版本，或「放弃待同步修改」读取服务端数据。记录已删除、权限下降等情况可能无法重放；已提交到服务端的操作不会因放弃队列回退。文件导入、历史恢复和工作区删除须先处理待同步修改。

旧版根目录的 `workspace.json` 不自动归属账号。确认导入后，先生成 `workspace.pre-postgres.json` 并落盘归属，再请求 API；网络失败也不能被其他账号领取。原文件和备份保留。私钥路径和信任只保留在当前设备，不通过旧配置导入上传。

## 服务端边界与迁移

### 接口概览

| 方法与路径 | 用途 |
| --- | --- |
| `GET /healthz` | 匿名健康检查，检查实际数据库并返回服务端版本 |
| `GET /v1/workspace` | 读取个人或选定工作区的快照 |
| `POST /v1/workspace/changes` | 提交带预期修订号及可选操作 ID 的配置修改 |
| `POST /v1/workspace/import` | 将校验通过的导入配置合并到工作区 |
| `POST /v1/workspace/actions` | 工作区、成员、设备、历史、日志与指纹管理；`capabilities` 返回服务端版本和能力 |

业务请求通过 `Authorization: Bearer <access-token>` 认证，可携带 `X-TermLume-Workspace`、`X-TermLume-Device`、`X-TermLume-Version` 和 `X-TermLume-Platform`。工作区与设备使用不带连字符的 GUID；平台为 `windows` 或 `macos`。这些请求头是选择参数，仓储仍核验成员权限及设备授权绑定。错误响应为 `{ "error": "安全的错误说明" }`，状态码区分 400 / 401 / 403 / 404 / 409 / 413 / 503；缺失或无效 Bearer 令牌的认证质询带有 `WWW-Authenticate: Bearer`。

JSON 请求严格拒绝未知顶层字段、缺失必填参数和无效类型，正文上限为 3 MiB，工作区本身上限为 2 MiB。日志详情按允许字段过滤，无效或空的可选详情被丢弃，日志批次结构无效时返回 400。

### 认证、事务与版本迁移

API 只接受 `termlume-desktop` 的签名访问令牌（实际 client ID 为 `termlume-desktop`），校验 RS256、issuer、audience、用途、`sub/sid/gid/openid`，拒绝管理台和 ID 令牌。每次请求再调用 IAM UserInfo 校验实时会话 / grant，且核对 subject。JWKS 有缓存与受限即时轮换刷新。不启用 CORS，拒绝 Origin 请求，不记录令牌或请求正文。

16 张业务表启用 RLS。事务内从已验证身份设置 issuer / subject；工作区、成员和资产按角色授权，个人状态按账号隔离。少量固定 search_path 的安全定义函数处理当前角色、邀请兑换、所有权转让和修订号；运行角色只能直接修改工作区名称，不能任意更改 owner 或修订号。审计表只授予 SELECT / INSERT。

RLS 身份由可信 API 设置，数据库运行凭据必须由服务端保管。它用于约束业务查询和隔离账号，不能代替 API 认证，也不应把运行角色的数据库访问权交给桌面用户。连接上下文仅在当前事务有效，归还连接池后不会保留前一账号身份。

`001` 创建个人数据，`002` 修复级联删除，`003` 增加协作和同步数据，`004` 增加七天日志保留规则、清理函数与时间索引。版本与 SQL 的 SHA-256 记录在 `public.termlume_migrations`，迁移在事务与部署锁中执行；重复执行安全，改写已应用迁移会被拒绝。已有个人工作区会迁为个人类型并补所有者成员，数据保留。

新增结构变化应增加下一份迁移，不修改已有 SQL 的语句、注释或换行内容。ABP 宿主及应用服务复用已有仓储事务，服务端框架迁移不新增业务库。

## 备份与恢复

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Backup-WorkspaceDatabase.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Restore-WorkspaceDatabase.ps1 -BackupFile '完整路径/TermLume-termlume-时间.dump'
```

备份默认保存在 `.local/backups/`，包含 PostgreSQL custom-format dump 与同名 `.dump.json` 清单，记录 SHA-256 和迁移校验值。二进制文件在容器内生成后复制，兼容 Windows PowerShell 5。备份包括工作区配置、历史和审计，应独立保管。

恢复先校验散列和迁移，只允许创建新的 `termlume_restore_*` 数据库，目标已存在则拒绝；不会覆盖 `termlume` 或 IAM 库。恢复权限后执行缺失迁移并写出 `.local/restores/` 中的运行配置，默认验证端口 18745。使用恢复库完成验收后，再显式调整正式 API 连接串；工具不会自动切换。迁移角色和运行角色密码单独保管，桌面端本机信任与私钥路径需另行备份。

恢复验收产生的库属于临时资源。验收完成后，操作者应核对名称、连接和备份，再清理临时库及对应配置；正常运行只需要一个 `termlume` 业务库。集成测试直接使用可清理的独立测试身份，结束后清除自己的数据，不额外创建数据库。

## Docker 部署 API

推荐通过独立 Docker 容器运行 API，和现有 IAM PostgreSQL 共用宿主测试端口。在 Aegis-IAM 测试容器就绪后，一键备份、构建、检查迁移并启动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Publish-WorkspaceDocker.ps1
Invoke-RestMethod http://127.0.0.1:18741/healthz
docker compose -f docker/compose.workspace.yml ps
```

镜像为 `termlume-workspace-api:0.9.0`。健康响应包含 `status: ok`、`version: 0.9.0` 和 `framework: ABP`；当前 0.8.1 桌面包可直接连接。需要分步执行时：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Start-WorkspaceApi.ps1 -PrepareOnly
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Prepare-WorkspaceDocker.ps1
docker compose -f docker/compose.workspace.yml build workspace-api
docker compose -f docker/compose.workspace.yml --profile migration run --rm migrate
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Stop-WorkspaceApi.ps1
docker compose -f docker/compose.workspace.yml up -d workspace-api
Invoke-RestMethod http://127.0.0.1:18741/healthz
```

容器默认绑定本机 18741，使用非 root 用户、只读文件系统、临时目录、无额外 capabilities，重启策略为 `unless-stopped`。镜像内置健康探针，检查 API 与实际数据库连接。此服务仅使用 Bearer 认证，数据保护提供器明确使用内存密钥；登录凭据仍由桌面系统凭据库和 IAM 管理。数据库设置通过 `.local/` 的只读文件挂载，不进入镜像；迁移容器只在显式调用时使用 owner 配置。停止容器可运行 `docker compose -f docker/compose.workspace.yml down`，不会删除数据库。

本地 Docker 下，签名校验仍使用原 issuer；只有服务端回源改为 `host.docker.internal`。明文 Docker 回源必须显式启用 `Aegis:AllowLocalDockerBackchannel`，仅允许该宿主别名；正式部署使用 HTTPS。浏览器授权和桌面端不接受这个内部回源地址。

## 验证

```powershell
dotnet build TermLume.sln
dotnet run --project tests/TermLume.AuthenticationTests
dotnet run --project tests/TermLume.SecurityTests
node --test tests/workspace-api.integration.cjs
# 可选：使用刚构建的实际 Docker 镜像运行同一套集成测试
$env:TERMLUME_TEST_DOCKER_IMAGE = 'termlume-workspace-api:0.9.0'
node --test tests/workspace-api.integration.cjs
Remove-Item Env:TERMLUME_TEST_DOCKER_IMAGE
node --test tests/*.test.cjs
cd desktop
npm test
npm run test:backend
npm run test:desktop
```

SSH 测试需可导入 Paramiko 的 Python，通过 `TERMLUME_TEST_PYTHON` 指定。PostgreSQL 集成测试默认读取 `.local/api-settings.local.json` 和 `.local/api/TermLume.Api.exe`，以临时签名颁发方、随机 API 端口和可清理账号验证真实数据库，测试结束只清理该临时 issuer 的数据。隔离库可通过 `TERMLUME_TEST_API_SETTINGS`、`TERMLUME_TEST_API_EXECUTABLE`、`TERMLUME_TEST_DATABASE` 指定。

PostgreSQL 集成套件现包含 24 项检查，覆盖实际 Windows 可执行文件及 Docker 镜像。最新构建、依赖扫描、修复与回归结果见 [提交前安全检查](security-review-2026-11-20.md)，此前记录见 [2026-11-19 检查](security-review-2026-11-19.md)。macOS 共用实现与界面，本次未在 macOS 机器构建或验收安装包。
