using System.IO;
using System.Text.RegularExpressions;
using TermLume.Models;

namespace TermLume.Services;

public static class PreferenceSettings
{
    public static readonly IReadOnlyDictionary<string, string> DefaultShortcuts = new Dictionary<string, string>
    {
        ["newHost"] = "Ctrl+KeyN", ["newGroup"] = "Ctrl+Shift+KeyN", ["searchHosts"] = "Ctrl+KeyK",
        ["settings"] = "Ctrl+Comma", ["workspace"] = "Ctrl+Shift+KeyH", ["openTerminal"] = "Ctrl+Shift+KeyT",
        ["closeTab"] = "Ctrl+Shift+KeyW", ["nextTab"] = "Ctrl+Tab", ["previousTab"] = "Ctrl+Shift+Tab",
        ["reconnect"] = "Ctrl+Shift+KeyR", ["copy"] = "Ctrl+Shift+KeyC", ["paste"] = "Ctrl+Shift+KeyV",
        ["clearTerminal"] = "Ctrl+Shift+KeyL", ["toggleSidebar"] = "Ctrl+Shift+KeyB",
        ["fontIncrease"] = "Ctrl+Equal", ["fontDecrease"] = "Ctrl+Minus", ["fontReset"] = "Ctrl+Digit0"
    };
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "Alt+F4", "Alt+Tab", "Alt+Shift+Tab", "Alt+Space", "Ctrl+Alt+Delete", "Ctrl+Escape", "Ctrl+Shift+Escape",
        "Ctrl+KeyA", "Ctrl+KeyC", "Ctrl+KeyV", "Ctrl+KeyX", "Ctrl+KeyY", "Ctrl+KeyZ", "Ctrl+Shift+KeyZ", "Ctrl+KeyD", "Ctrl+KeyL"
    };

    public static string NormalizeShortcut(string? value)
    {
        if (value == "") return "";
        if (value is null || value.Length > 60) throw new InvalidDataException("快捷键格式无效。");
        var parts = value.Split('+');
        var code = parts[^1];
        code = code switch { "NumpadAdd" => "Equal", "NumpadSubtract" => "Minus", _ => code };
        var modifiers = parts[..^1];
        var functionKey = Regex.IsMatch(code, @"\AF([1-9]|1[0-2])\z");
        if (modifiers.Distinct().Count() != modifiers.Length || modifiers.Any(part => part is not ("Ctrl" or "Alt" or "Shift")) || !functionKey && !modifiers.Any(part => part is "Ctrl" or "Alt") ||
            !Regex.IsMatch(code, @"\A(Key[A-Z]|Digit[0-9]|F([1-9]|1[0-2])|Comma|Period|Slash|Backslash|Semicolon|Quote|BracketLeft|BracketRight|Minus|Equal|Backquote|Tab|Space|Enter|Backspace|Delete|Insert|Home|End|PageUp|PageDown|ArrowLeft|ArrowRight|ArrowUp|ArrowDown)\z"))
            throw new InvalidDataException("请使用 Ctrl / Alt 组合或 F1–F12 功能键；不支持 Windows 键或仅 Shift 加普通按键。");
        var normalized = string.Join('+', new[] { "Ctrl", "Alt", "Shift" }.Where(modifiers.Contains).Append(code));
        if (normalized == "Ctrl+Shift+Equal") normalized = "Ctrl+Equal";
        if (Reserved.Contains(normalized)) throw new InvalidDataException("该组合由 Windows、文本编辑或终端控制使用，请选择其他快捷键。");
        return normalized;
    }

    public static Preferences Normalize(Preferences value, bool strict = false)
    {
        if (value.Language is not ("zh-CN" or "en"))
        {
            if (strict) throw new InvalidDataException("不支持的界面语言。");
            value.Language = "zh-CN";
        }
        value.FontSize = Math.Clamp(value.FontSize, 11, 24);
        if (value.TerminalTheme is not ("midnight" or "nord")) value.TerminalTheme = "midnight";
        if (value.FontFamily is not ("cascadia" or "consolas" or "jetbrains")) value.FontFamily = "cascadia";
        value.LineHeight = double.IsFinite(value.LineHeight) ? Math.Clamp(value.LineHeight, 1, 1.8) : 1.2;
        value.Scrollback = Math.Clamp(value.Scrollback, 1000, 50000);
        if (value.CursorStyle is not ("block" or "underline" or "bar")) value.CursorStyle = "block";
        if (value.Accent is not ("lavender" or "mint" or "ocean")) value.Accent = "lavender";
        if (value.Density is not ("compact" or "comfortable")) value.Density = "compact";
        try
        {
            var supplied = value.Shortcuts ?? [];
            if (supplied.Keys.Any(key => !DefaultShortcuts.ContainsKey(key))) throw new InvalidDataException("快捷键操作不存在。");
            var shortcuts = DefaultShortcuts.ToDictionary(pair => pair.Key, pair => NormalizeShortcut(supplied.GetValueOrDefault(pair.Key, pair.Value)));
            if (shortcuts.Values.Where(binding => binding.Length > 0).Distinct().Count() != shortcuts.Values.Count(binding => binding.Length > 0))
                throw new InvalidDataException("多个操作使用了相同的快捷键，请先解除冲突。");
            value.Shortcuts = shortcuts;
        }
        catch (InvalidDataException) when (!strict)
        {
            value.Shortcuts = DefaultShortcuts.ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        return value;
    }
}
