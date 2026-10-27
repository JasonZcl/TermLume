using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TermLume.Shared.Services;

/// <summary>原生对话框与网页共用英文词典，缺失的翻译保持原始中文文案。</summary>
public static class Localization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() =>
    {
        // 只加载程序随附的本地资源；读取失败不应阻止应用显示必要的错误提示。
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "locales", "en.json"))) ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    });

    public static string Language { get; set; } = "zh-CN";

    public static string Text(string text, params object?[] values)
    {
        var translated = Language == "en" ? English.Value.GetValueOrDefault(text, text) : text;
        return values.Length == 0
            ? translated
            : Regex.Replace(translated, @"\{(\d+)\}", match =>
                int.TryParse(match.Groups[1].Value, out var index) && index < values.Length
                    ? Convert.ToString(values[index], CultureInfo.InvariantCulture) ?? ""
                    : match.Value);
    }
}
