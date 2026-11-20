using System.Text.RegularExpressions;
using TermLume.Shared.Models;

namespace TermLume.Shared.Services;

public static class ActivityPrivacy
{
    /// <summary>仅保留允许同步的元数据；原始消息、异常、路径和任意详情均不上传。</summary>
    public static ActivityLogEntry Sanitize(ActivityLogEntry entry)
    {
        if (entry is null || !Guid.TryParse(entry.Id, out _) || entry.Event is null ||
            !Regex.IsMatch(entry.Event, "\\A(connection|hostkey|application|workspace|sftp)\\.[a-zA-Z]{1,50}\\z") ||
            entry.Level is not ("info" or "warning" or "error") || entry.Category is not ("connection" or "security" or "application") ||
            entry.Timestamp < DateTimeOffset.UtcNow.AddYears(-1) || entry.Timestamp > DateTimeOffset.UtcNow.AddMinutes(10))
            throw new InvalidDataException("活动日志字段或时间无效。");
        string? Clean(string? text, int limit) => text is not null && text.Length <= limit && !text.Contains('\0') ? text : null;
        var result = new ActivityLogEntry
        {
            Id = Guid.Parse(entry.Id).ToString("N"), Timestamp = entry.Timestamp, Event = entry.Event, Message = "客户端事件 · " + entry.Event,
            Level = entry.Level, Category = entry.Category, HostId = Guid.TryParse(entry.HostId, out var host) ? host.ToString("N") : null,
            HostName = Clean(entry.HostName, 100), Address = Clean(entry.Address, 255), Username = Clean(entry.Username, 100),
            Port = entry.Port is >= 1 and <= 65535 ? entry.Port : null,
            SessionId = Guid.TryParse(entry.SessionId, out var session) ? session.ToString("N") : null,
            DurationSeconds = entry.DurationSeconds is >= 0 and <= 604800 ? entry.DurationSeconds : null
        };
        // 消息原文、异常、路径和任意详情不进入跨设备日志。
        foreach (var pair in entry.Details ?? [])
        {
            if (pair.Key is "bytes" or "uploadedBytes" or "downloadedBytes" && long.TryParse(pair.Value, out var number) && number >= 0)
                result.Details[pair.Key] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // JSON 仍可包含 null 字典值，不能因 string 的非空标注而直接交给 Regex。
            if (pair.Key == "fingerprint" && pair.Value is { } fingerprint && Regex.IsMatch(fingerprint, "\\ASHA256:[A-Za-z0-9+/]{43}=?\\z")) result.Details[pair.Key] = fingerprint;
            if (pair.Key == "direction" && pair.Value is "upload" or "download") result.Details[pair.Key] = pair.Value;
        }
        return result;
    }
}

public static class TrustReferenceValidation
{
    public static void Validate(KnownHostEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Address) || entry.Address.Length > 255 || entry.Address.Any(char.IsWhiteSpace) || entry.Address.Any(char.IsControl) || entry.Address.Contains('/') || entry.Address.Contains('\\') ||
            entry.Port is not (>= 1 and <= 65535) || !Regex.IsMatch(entry.Algorithm ?? "", "\\A[a-zA-Z0-9@._+-]{1,100}\\z") ||
            !Regex.IsMatch(entry.Fingerprint ?? "", "\\ASHA256:[A-Za-z0-9+/]{43}=?\\z") ||
            entry.Endpoint != KnownHostRegistry.Endpoint(new HostProfile { Address = entry.Address, Port = entry.Port.Value }, entry.Algorithm!))
            throw new InvalidDataException("服务器指纹参考记录无效。");
    }
}
