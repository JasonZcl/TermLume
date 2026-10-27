# TermLume macOS 桌面入口

Windows 正式入口仍为 `TermLume.Windows/TermLume.Windows.csproj`，原有安装器和用户目录不变。此目录提供 macOS 桌面壳，复用 `../TermLume.Windows/Web`，通过标准输入 / 输出 JSON Lines 与 `TermLume.DesktopBackend` 通信；不运行本地 HTTP / WebSocket 服务，不从渲染页直接访问 Node.js 或 SSH 库。

## 开发与构建

需要 macOS 14+、Node.js 24、.NET 10 SDK、Xcode Command Line Tools。应用构建自带固定版本的 .NET 运行时；开发者不需要修改全局运行时配置。两个架构必须分别构建，暂不提供 Universal 合并包。

```bash
cd desktop
npm ci
npm start
npm run dist:mac:arm64
# Intel: npm run dist:mac:x64
```

`scripts/build-backend.cjs` 发布带本地安全修订 SSH.NET 的自包含后端，按运行时分目录存放，避免把 Windows DLL / 本机原生运行时混入 macOS 包。`scripts/package-mac.cjs` 只允许在 macOS 执行，创建临时构建目录，将最终 DMG / ZIP 复制到 `../artifacts/` 并输出 SHA-256。构建产物、`backend/`、`node_modules/` 都不提交 Git。应用图标由既有图形生成 `../TermLume.Windows/Assets/TermLume-mac.png`，打包时转换为 ICNS。

GitHub **Actions → macOS desktop** 可手动触发两种架构的构建；相关 Pull Request 也会验证。安装包作为 Actions 附件保留 14 天，不自动创建 Release。正式发布前须人工验收，上传 DMG / ZIP 与校验值到 Release；已有 Windows EXE 不会因为源码改造而自动具备 macOS 能力。

## 隔离验证

```bash
python -m pip install paramiko==4.0.0
npm test
npm run test:backend
npm run test:desktop
dotnet run --project ../tests/TermLume.PortableTests -c Release -r osx-arm64
# Intel 将运行时改为 osx-x64
```

- `npm test` 验证原生桥的来源 / 操作 / 路径边界、Windows 和 Mac 快捷键，不启动真实桌面。
- `test:backend` 在临时目录生成合成密钥和回环 SSH / SFTP 测试服务器，验证真实桥接、首次信任、认证取消、文件拖放入队、上传下载、日志导出保护、退出和重启后的数据保留。
- `test:desktop` 用独立临时应用目录和隐藏窗口测试真实 Electron 自定义协议、沙箱、语言资源、设置路径和桥接；不读取真实工作空间，不连接服务器，也不显示日常应用窗口。
- 可移植 C# 测试复用 Windows 的服务器身份、RSA SHA-2、输出洪泛、接收限额、ACK 超时与 SFTP 回归，补充本机文件路径与 Command 保留键检查。

Windows 上可执行上述隔离测试，并交叉发布 `osx-arm64` / `osx-x64` 后端以检查编译。**这不能替代 macOS 运行测试、DMG 挂载安装、实际 Finder 拖放与原生签名验证。** 不要将 Windows 测试通过写成 macOS 真机已验收。

## 桌面和安全约定

- `main.cjs` 独占文件选择框、剪贴板、窗口和子进程。只允许应用主窗口的精确 URL / 主 frame 调用受限通道；凭据窗口只能初始化和提交自己的凭据，不能调用主工作台能力。
- `preload.cjs` 只暴露指定桥方法。主页面不能传入 `hostReply`、`shutdown` 或自行声明拖放的绝对路径。Finder 的真实 File 由预加载层 `webUtils.getPathForFile` 转换；后端逐项验证普通文件、路径范围、链接和队列容量。
- 密码不进入命令行参数、环境变量、工作空间或日志；托管字符串不能保证即时清零。窗口关闭或取消返回 `null`，空字符串仅代表用户明确提交空口令。
- Windows 与 .NET 后端均通过项目引用使用 `TermLume.Shared`，工作区控制器、SSH / SFTP、认证、已知主机、持久化和本地文件服务只有一份实现。平台差异通过 `IWorkspaceHost`、`IWorkspaceDispatcher` 适配；不要在桌面壳或进程入口重复实现业务验证。必须继续使用 `NuGet.config` 固定来源的 `SSH.NET 2026.0.0.2`，不能换回没有接收限额的版本。详细约定见 [架构说明](../docs/architecture.md)。
- 配置 / 日志位于 `~/Library/Application Support/TermLume/`，Electron 浏览器缓存位于其中的 `Desktop/`。替换应用不会重置数据。默认只卸载 `.app`；彻底删除数据必须由用户明确选择并先备份，不递归清理其他目录。
- 退出时检查在线连接和传输，默认继续使用；明确确认后取消传输、等待清理并释放连接。后端异常退出会停止界面，不自动重新连接或替换损坏配置。

## Developer ID 签名与公证

默认是没有发布者身份的 **ad-hoc 测试包**。为了避免临时签名与 Electron 已签名框架之间的库验证冲突，默认不启用 Hardened Runtime；页面隔离和 Chromium 沙箱仍启用。不要把这种包描述成已公证的正式发行版，也不要建议用户关闭 Gatekeeper。

正式公开发行需要 Apple Developer ID Application 证书及 Apple 公证凭据。由维护者在受保护的构建环境中完成以下配置，勿将证书、口令或 API 私钥提交 Git：

1. 在 `package.json` 的 `build.mac` 移除测试用 `identity: "-"`，设置 `hardenedRuntime: true`，并启用 `notarize: true`；通过 electron-builder 的 `CSC_LINK` / `CSC_KEY_PASSWORD` 注入证书，通过受保护的 Apple 公证环境变量配置认证。
2. 保留 `entitlements.mac.plist` 中 JIT 所需权限，分别验证 Electron 与内置 .NET 后端在签名后可启动；不要通过笼统关闭沙箱解决签名问题。
3. 在每种架构上验证 `codesign --verify --deep --strict TermLume.app`、`spctl --assess --type execute --verbose TermLume.app` 和公证票据，随后使用带下载隔离标记的 DMG 在干净 Mac 安装验收。
4. 退出旧版本、升级并核对主机 / 分组 / 指纹 / 快捷键 / 日志保留；测试中文与英文、密码与加密私钥、指纹变化拦截、磁盘 / 外接卷浏览、Finder 上传、双栏下载 / 取消、缩放和退出确认。

目前不包含自动更新、系统钥匙串密码保存或自动发布 Release；Windows 安装器仍负责 Windows 的卸载数据保留选项。
