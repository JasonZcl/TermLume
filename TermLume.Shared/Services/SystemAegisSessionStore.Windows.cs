using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TermLume.Shared.Services;

public sealed partial class SystemAegisSessionStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsCredential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref WindowsCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    private static IOException CredentialError() => new("无法访问 Windows 凭据管理器。", new Win32Exception(Marshal.GetLastWin32Error()));

    private static byte[]? WindowsRead(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw CredentialError();
        }
        try
        {
            var credential = Marshal.PtrToStructure<WindowsCredential>(pointer);
            if (credential.BlobSize > 2560) throw new IOException("保存的登录会话无效。");
            var bytes = new byte[credential.BlobSize];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { CredFree(pointer); }
    }

    private static void WindowsWrite(string target, byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new WindowsCredential { Type = 1, TargetName = target, UserName = "TermLume",
                Blob = pointer, BlobSize = (uint)bytes.Length, Persist = 2 };
            if (!CredWrite(ref credential, 0)) throw CredentialError();
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(pointer, index, 0);
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void WindowsDelete(string target)
    {
        if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw CredentialError();
    }
}
