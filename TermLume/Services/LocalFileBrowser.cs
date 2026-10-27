using System.IO;

namespace TermLume.Services;

public sealed record FileEntry(string Name, string Path, bool Directory, bool Link, long Size, DateTime Modified, bool Drive = false);
public sealed record FileListing(string Path, string? Parent, IReadOnlyList<FileEntry> Entries, bool Computer = false);

/// <summary>在选定目录或本机磁盘范围内浏览文件，拒绝路径越界和重解析点。</summary>
public sealed class LocalFileBrowser(string root, bool allDrives = false)
{
    // “此电脑”只是显示磁盘列表的虚拟节点，不能作为文件传输目标。
    public const string ComputerPath = "computer:";
    public string Root { get; } = allDrives ? ComputerPath : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public static LocalFileBrowser ForComputer() => new(ComputerPath, true);

    /// <summary>校验原生 WebView2 文件对象提供的路径；调用方不得传入页面自行声明的路径。</summary>
    public static LocalFileBrowser ForDroppedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\"))
            throw new InvalidDataException("无法读取拖入的本地文件，请从资源管理器拖入普通文件。");
        var browser = new LocalFileBrowser(Path.GetPathRoot(Path.GetFullPath(path))!);
        var file = new FileInfo(browser.Validate(path));
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("暂不支持拖入文件夹或链接，请打开文件夹后选择普通文件。");
        return browser;
    }

    public string Validate(string path)
    {
        if (path == ComputerPath) throw new InvalidDataException("请先打开一个磁盘或文件夹，再选择文件或下载位置。");
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("请输入完整的本地路径。");
        if (allDrives && OperatingSystem.IsWindows() && (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')))
            throw new InvalidDataException("请输入完整磁盘路径，例如 C:\\；网络共享请通过「选择文件夹」打开。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var scopeRoot = allDrives ? Path.GetPathRoot(full)! : Root;
        if (allDrives && OperatingSystem.IsWindows() && !DriveInfo.GetDrives().Any(drive => string.Equals(drive.Name, scopeRoot, PathComparison)))
            throw new DirectoryNotFoundException("此磁盘不可用，请返回「此电脑」刷新磁盘列表。");
        if (!string.Equals(full, scopeRoot, PathComparison) &&
            !full.StartsWith(Path.EndsInDirectorySeparator(scopeRoot) ? scopeRoot : scopeRoot + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("请先选择此文件所在的本地文件夹。");
        var current = full;
        // 不仅检查最终文件，还检查作用域内的每一级父目录，防止通过目录联接跳出边界。
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("为保护本地文件，暂不跟随符号链接或目录联接。");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), scopeRoot, PathComparison)) break;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("本地路径无效。");
        }
        return full;
    }

    public FileListing List(string path)
    {
        if (allDrives && path == ComputerPath && !OperatingSystem.IsWindows())
        {
            var volumes = new List<FileEntry> { new(Localization.Text("系统磁盘 (/)"), "/", true, false, 0, DateTime.MinValue, true) };
            if (OperatingSystem.IsMacOS() && Directory.Exists("/Volumes"))
                volumes.AddRange(new DirectoryInfo("/Volumes").EnumerateDirectories()
                    .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(directory => directory.Name, StringComparer.Ordinal)
                    .Select(directory => new FileEntry(directory.Name, directory.FullName, true, false, 0, DateTime.MinValue, true)));
            return new(ComputerPath, null, volumes, true);
        }
        if (allDrives && path == ComputerPath)
            return new(ComputerPath, null, DriveInfo.GetDrives().OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                .Select(drive => new FileEntry($"磁盘 ({drive.Name.TrimEnd('\\')})", drive.Name, true, false, 0, DateTime.MinValue, true)).ToArray(), true);
        var full = Validate(path);
        var entries = new List<FileEntry>();
        foreach (var item in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            if (entries.Count >= 10000) throw new InvalidDataException("目录超过 10,000 项，请选择更小的目录。");
            var link = (item.Attributes & FileAttributes.ReparsePoint) != 0;
            entries.Add(new(item.Name, item.FullName, item is DirectoryInfo, link,
                !link && item is FileInfo file ? file.Length : 0, item.LastWriteTimeUtc));
        }
        var parent = allDrives && string.Equals(full, Path.GetPathRoot(full), PathComparison)
            ? ComputerPath : string.Equals(full, Root, PathComparison) ? null : Path.GetDirectoryName(full);
        return new(full, parent,
            entries.OrderByDescending(entry => entry.Directory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public string Destination(string directory, string name)
    {
        Validate(directory);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Length > 240 || OperatingSystem.IsWindows() && (name.EndsWith('.') || name.EndsWith(' ') ||
            System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("远端文件名不兼容本地系统，请先在远端重命名。");
        return Path.Combine(directory, name);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
