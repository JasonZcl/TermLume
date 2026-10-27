'use strict';

const isMacOS = window.termlume?.platform === 'darwin';
const platformName = isMacOS ? 'macOS' : 'Windows';
document.body.dataset.platform = isMacOS ? 'macos' : 'windows';
const macOSMessages = new Map([
  ['Windows', 'macOS'],
  ['为 Windows 定制你的操作习惯', '为 macOS 定制你的操作习惯'],
  ['所有自定义快捷键都将恢复为 Windows 默认方案。其他设置不受影响。', '所有自定义快捷键都将恢复为 macOS 默认方案。其他设置不受影响。'],
  ['Ctrl+C / D / Z / L 保留终端控制语义，输入框保留 Ctrl+A / C / V / X 等编辑操作。Alt+F4、Win 等系统组合不由应用接管。', 'Ctrl+C / D / Z / L 保留终端控制语义，输入框保留 Command+A / C / V / X 等编辑操作。Command+Q / H / M 等系统组合不由应用接管。'],
  ['默认终端复制 / 粘贴使用 Ctrl + Shift + C / V。组合按物理键位记录；自定义后会优先于对应的远端按键生效。', '默认终端复制 / 粘贴使用 Command + C / V，标签切换使用 Control + Tab。组合按物理键位记录；自定义后会优先于对应的远端按键生效。'],
  ['基于 WPF、WebView2、SSH.NET 与 xterm.js。支持 SSH 终端与 SFTP 文件管理，暂不包含跳板机和云同步。', '基于 Electron、.NET、SSH.NET 与 xterm.js。支持 SSH 终端与 SFTP 文件管理，暂不包含跳板机和云同步。'],
  ['此电脑', '本机'], ['此电脑：全部磁盘', '本机：系统磁盘与已挂载卷'],
  ['默认显示此电脑的全部磁盘。', '默认显示系统磁盘与已挂载卷。'],
  ['请使用 Ctrl / Alt 组合或 F1–F12 功能键；不支持 Windows 键或仅 Shift 加普通按键。', '请使用 Command / Ctrl / Option 组合或 F1–F12 功能键。'],
  ['不支持 Windows 键或 AltGr，请使用 Ctrl / Alt 组合或功能键。', '不支持 AltGr，请使用 Command / Ctrl / Option 组合或功能键。'],
  ['该组合由 Windows、文本编辑或终端控制使用，请选择其他快捷键。', '该组合由系统、文本编辑或终端控制使用，请选择其他快捷键。'],
  ['在下方按下组合键，例如 Ctrl + Shift + K', '在下方按下组合键，例如 Command + Shift + K'],
  ['支持 Ctrl / Alt 组合或 F1–F12；Esc 取消，Tab 切换焦点。Windows 系统保留组合及 Ctrl+C / V 等文本编辑组合不能覆盖。', '支持 Command / Ctrl / Option 组合或 F1–F12；Esc 取消，Tab 切换焦点。系统保留组合与输入框编辑操作不能覆盖。'],
  ['左右拖动文件即可上传 / 下载，也可拖入资源管理器中的文件 · 同名不覆盖 · 不传输文件夹或链接', '左右拖动文件即可上传 / 下载，也可拖入 Finder 中的文件 · 同名不覆盖 · 不传输文件夹或链接'],
  ['无法读取拖入的本地文件，请从资源管理器拖入普通文件。', '无法读取拖入的本地文件，请从 Finder 拖入普通文件。']
]);

function platformText(value) { return isMacOS ? macOSMessages.get(value) || value : value; }
function platformDataFile(name) { return isMacOS ? `~/Library/Application Support/TermLume/${name}` : `%LOCALAPPDATA%\\TermLume\\${name}`; }
