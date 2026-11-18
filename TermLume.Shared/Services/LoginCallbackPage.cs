using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TermLume.Shared.Services;

internal static class LoginCallbackPage
{
    private static readonly string Template = ReadResource("html");
    private static readonly string Styles = ReadResource("css");
    private static readonly string Script = ReadResource("js");

    // 仅允许随程序打包的样式和脚本；回调参数不会进入页面内容。
    public static string ContentSecurityPolicy { get; } =
        $"default-src 'none'; style-src 'sha256-{Hash(Styles)}'; script-src 'sha256-{Hash(Script)}'; " +
        "img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static string Render(bool valid, bool denied)
    {
        var english = Localization.Language == "en";
        var received = valid && !denied;
        string Text(string chinese, string translation) => english ? translation : chinese;
        var heading = received ? Text("可以返回 TermLume 了", "You're ready to return") :
            valid ? Text("这次登录未完成", "Sign-in wasn't completed") :
            Text("无法确认这次登录", "We couldn't verify this request");
        var description = received ? Text("登录结果已收到，请在应用中继续。", "Your sign-in response was received. Continue in the app.") :
            Text("请返回 TermLume，重新发起登录。", "Return to TermLume and try signing in again.");

        return Template
            .Replace("{{LANGUAGE}}", english ? "en" : "zh-CN")
            .Replace("{{STATE}}", received ? "received" : "attention")
            .Replace("{{TITLE}}", WebUtility.HtmlEncode(heading))
            .Replace("{{STATUS}}", Text(received ? "登录结果已接收" : "需要重新登录", received ? "RESPONSE RECEIVED" : "SIGN-IN INCOMPLETE"))
            .Replace("{{DESCRIPTION}}", WebUtility.HtmlEncode(description))
            .Replace("{{CONTINUE}}", Text("在 TermLume 中继续", "Continue in TermLume"))
            .Replace("{{SWITCH}}", Text("切换回应用窗口", "Switch back to the app window"))
            .Replace("{{MODIFIER}}", OperatingSystem.IsMacOS() ? "⌘" : "Alt")
            .Replace("{{CLOSE}}", Text("完成，关闭此页", "Done, close this tab"))
            .Replace("{{HINT}}", Text("此浏览器标签页可以安全关闭。", "You can safely close this browser tab."))
            .Replace("{{CONTEXT}}", Text("账号登录", "Account sign-in"))
            .Replace("{{FOOTER}}", Text("你的工作空间，在应用中继续。", "Your workspace is waiting in the app."))
            .Replace("{{STYLES}}", Styles)
            .Replace("{{SCRIPT}}", Script);
    }

    private static string ReadResource(string extension)
    {
        using var stream = typeof(LoginCallbackPage).Assembly.GetManifestResourceStream(
            "TermLume.Shared.Resources.LoginCallback." + extension) ?? throw new IOException("登录确认页面资源缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // 浏览器在计算内联资源 CSP 哈希前会将 CRLF 规范化为 LF。
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static string Hash(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
