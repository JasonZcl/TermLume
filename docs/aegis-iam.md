# Aegis-IAM 登录对接

TermLume 启动后必须先登录 Aegis-IAM，才能读取工作空间、管理主机、查看日志或建立 SSH / SFTP 连接。共享核心校验每个工作区请求，Windows 和 macOS 使用同一份认证实现。

## 首次配置

1. 启动上一级目录中的 `Aegis-IAM`。当前 TermLume 测试配置使用独立 Docker 测试环境 `http://localhost:18080`（`aegis-iam-test`）；原 Docker 环境默认入口是 `http://localhost:8080`，开发脚本入口默认是 `http://localhost:5173`。访问 `/.well-known/openid-configuration` 检查实际 issuer；TermLume 的地址必须与返回的 `issuer` 完全一致。
2. 用有接入应用管理权限的管理员登录 Aegis-IAM，进入「接入应用」，登记以下公共客户端。修改应用时按 IAM 要求完成二次验证。

| 项目 | 值 |
| --- | --- |
| 名称 | TermLume Desktop |
| client ID | `termlume-desktop` |
| 类型 | 公共客户端，不配置 client secret |
| 回调地址 | `http://127.0.0.1:47831/callback/` |
| 允许的 scope | `openid`、`profile`、`email`、`offline_access` |

回调地址包括末尾 `/`，必须逐字一致；不能用 `localhost` 替换 `127.0.0.1`。IAM 当前不支持动态端口或自定义 scheme 回调。端口被占用时，选择另一个大于 1023 的固定端口，并同步修改 IAM 登记和 TermLume 配置。Windows 和 macOS 可登记为同一个公共客户端。

本机 Docker 测试环境和当前测试包统一使用公共客户端 `termlume-desktop`，不配置 client secret。其他机器或重建为空的数据卷仍需登记以上客户端。

通过管理 API 自动登记时，`POST /api/auth/clients` 的请求体已提供为 [aegis-client.json](aegis-client.json)。该接口需要管理员会话访问令牌及近期身份验证，不能用 TermLume 的 OAuth 令牌调用。项目不会读取 IAM 的 `.env` 密码、改写 IAM 配置或自动修改已有应用。

3. TermLume 默认读取可执行文件旁的 `aegis-iam.json`，随 Windows 发布和 macOS 后端一起打包：

```json
{
  "issuer": "http://localhost:18080",
  "clientId": "termlume-desktop",
  "redirectUri": "http://127.0.0.1:47831/callback/"
}
```

开发时修改仓库根目录的同名文件后重新构建。也可以用环境变量覆盖，优先级高于 JSON：

```powershell
# 使用 Aegis-IAM 的开发入口
$env:TERMLUME_IAM_ISSUER = 'http://localhost:5173'
# 以下两项通常不必修改
$env:TERMLUME_IAM_CLIENT_ID = 'termlume-desktop'
$env:TERMLUME_IAM_REDIRECT_URI = 'http://127.0.0.1:47831/callback/'
dotnet run --project TermLume.Windows
```

macOS 开发时在 `desktop` 目录运行 `TERMLUME_IAM_ISSUER=http://localhost:5173 npm start`。正式分发前请将配置中的 issuer 改为实际 HTTPS 地址并重新打包；从 Finder 启动的应用通常不会继承终端环境变量。普通远程 HTTP 地址、带路径的 issuer 和非 IPv4 回环的回调会被拒绝。不存在关闭登录要求的配置开关。

4. 启动 TermLume，点击「使用浏览器登录」，在系统浏览器完成登录、MFA 和授权，然后返回桌面应用。登录等待最多五分钟，可取消并重试。所有四项 scope 都需允许。

## 会话与数据

- 使用授权码 + S256 PKCE；核对回调的 state / issuer，通过发现文档和 JWKS 验证 RS256 ID Token 的签名、issuer、audience、nonce、有效期、访问令牌哈希，并核对 UserInfo 的 subject。
- 密码只输入在 IAM 的系统浏览器页面。访问和 ID 令牌只保存在共享后端内存；刷新凭据保存到当前系统用户的 Windows 凭据管理器或 macOS 钥匙串，与 issuer、client ID、账号和应用数据目录绑定。不进入页面、工作区 JSON、日志或命令行，不将 IAM 令牌用作 SSH 密码。旧版本没有保存会话，升级后需要先登录一次。
- 正常关闭程序保留登录；重新打开时无需再次操作浏览器，先使用刷新凭据取得新令牌，校验签名、issuer、audience、有效期和 UserInfo subject 后才开放工作区。退出登录、服务端撤销、刷新凭据过期或验证失败后需要重新登录。IAM 发现端点不可用时不开放工作区，并保留尚未消费的凭据供下次启动恢复。
- 系统凭据访问在线程池执行；同一数据目录仅允许一个进程持有会话锁，避免多实例重放一次性刷新凭据。数据目录的 `.enabled` 标记与 `.lock` 文件不含令牌；清除数据后不会恢复旧登录，下次启动时清理对应的遗留系统凭据。需要立即删除系统凭据时，先在应用内退出登录。系统接口参考：[Windows 通用凭据](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)、[macOS 钥匙串](https://developer.apple.com/documentation/security/updating-and-deleting-keychain-items)。
- 访问令牌临近到期时串行刷新，替换并安全保存新的刷新令牌；消费前移除旧凭据，不自动重试结果不确定的刷新请求，避免重放。刷新请求已发出但结果不确定时需要重新登录。每 30 秒检查一次会话，UserInfo 的校验结果最多缓存 60 秒；远程撤销通常在 60–90 秒内被检测。运行期间网络验证失败也会锁定客户端并清除保存的会话。
- 退出登录、服务端会话失效或用户安全状态变更被检测后，立即禁止工作区请求，关闭 SSH / SFTP 连接并取消传输。等待凭据的旧建连请求不能在退出后继续建立连接。
- 剪贴板读写和私钥选择同样经过共享后端鉴权；异步验证或原生对话框等待期间退出登录、变更账号后，旧请求不能继续写盘或交付敏感结果。
- 「设置 → 账号」显示账号资料并提供退出入口。退出会调用 `/oauth/revoke` 撤销该应用授权；服务不可达时仍清除本机登录，并提示撤销未完成。浏览器中的 IAM 管理会话由 IAM 自行管理，后续登录会请求重新验证身份。
- 主机、分组、收藏、最近连接、片段与偏好通过独立业务 API 保存到 PostgreSQL，每个 issuer + subject 拥有独立工作区。私钥路径与本机信任留在各工作区缓存；指纹参考需逐设备核验采用，活动日志可按账号同步，设备授权可独立撤销。旧版文件在确认导入后归属当前账号并保留备份；详见 [PostgreSQL 工作区](postgresql.md)。
- 业务 API 验证 `at+jwt / oauth_access` 的签名、issuer、audience/client ID、有效期及授权声明，并在每次请求中通过 UserInfo 校验实时授权。ID Token 与管理台令牌不能访问业务 API。桌面仍为公共客户端，不需要 client secret。
- 回环监听仅绑定 `127.0.0.1`，只在交互登录期间开启。Windows 使用系统浏览器，macOS 由 Electron 主进程调用系统浏览器；工作台 CSP 继续禁止连接远程认证服务。
- 浏览器回调确认页使用随程序嵌入的深色样式，跟随应用的中英文设置，分别显示已接收、未完成及无效请求。页面不展示回调参数，并在收到响应后清理地址栏中的临时参数；提示切换回应用，关闭标签页被浏览器阻止时显示手动操作指引。样式和脚本仅通过固定内容的 CSP 哈希授权，不依赖外部资源；收到回调不等于认证通过，工作区仍由共享认证核心完成验证后开放。

## 验证

```powershell
dotnet build TermLume.sln -c Release
dotnet run --project tests/TermLume.AuthenticationTests -c Release -r win-x64
node --test tests/snippets.test.cjs
npm --prefix desktop test
npm --prefix desktop run test:backend
npm --prefix desktop run test:desktop
```

认证测试使用随机回环端口和临时 RSA 密钥生成的本地 OIDC 协议夹具，不读取 IAM 的真实用户或数据库；检查登录前的后端访问限制、回调混淆、签名与声明、授权拒绝、刷新轮换、撤销、取消、端口占用及退出时的并发建连，也检查系统凭据读写、重新打开时恢复、并发恢复与登出后不再恢复。桥接测试实际结束后端进程，再用同一临时数据目录启动新进程，确认无需浏览器即恢复认证与工作区。桥接和 Electron 测试通过真实 PKCE / 签名校验后再访问工作区，生产入口没有测试免登录开关。SSH / SFTP 桥接回归另需 README 所述的隔离 Paramiko 环境。

macOS 上将认证测试的 RID 改为 `osx-arm64` 或 `osx-x64`。Windows 上通过测试不代替 macOS 钥匙串、系统浏览器与回环网络的真机验收。

2026-11-18 已使用公共客户端 `termlume-desktop`，通过共享认证核心连接本地 Docker 环境 `http://localhost:18080`，完成真实授权码 + S256 PKCE、签名 ID Token、UserInfo、系统凭据保存、关闭后恢复、刷新轮换和退出撤销验证；协议联调通过 HTTP 自动化模拟浏览器导航。认证回归 139 项、SSH / SFTP 安全回归 78 项通过；桌面桥接验证了结束进程后重新启动可自动恢复，Electron 隐藏窗口验证了登录门禁、登录回调、账号页、退出和中英文显示。安装器逻辑此前已通过 53 项隔离检查。MFA 账号、系统浏览器完整交互、拒绝授权和管理员远程撤销仍需在实际桌面流程中验收。

客户端协议处理使用 [Duende OIDC Client](https://docs.duendesoftware.com/identitymodel-oidcclient/manual/)，签名验证使用 [Microsoft IdentityModel](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet)。服务端协议约定对应上一级项目的 `docs/authentication.md`。

接入后的检查结果、已修复问题和剩余依赖风险见 [2026-11-18 安全检查记录](security-review-2026-11-18.md)。
