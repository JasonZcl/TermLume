using System.Runtime.InteropServices;

namespace TermLume.Shared.Services;

public sealed partial class SystemAegisSessionStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string KeychainService = "TermLume Aegis-IAM";
    private static readonly Lazy<IntPtr> securityLibrary = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<IntPtr> foundationLibrary = new(() => NativeLibrary.Load(CoreFoundation));
    private static IntPtr SecConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(securityLibrary.Value, name));
    private static IntPtr CfConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(foundationLibrary.Value, name));

    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, [MarshalAs(UnmanagedType.LPWStr)] string text, nint length);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);

    private static IntPtr MacDictionary(params (string Key, IntPtr Value)[] pairs) => CFDictionaryCreate(IntPtr.Zero,
        pairs.Select(pair => SecConstant(pair.Key)).ToArray(), pairs.Select(pair => pair.Value).ToArray(), pairs.Length,
        NativeLibrary.GetExport(foundationLibrary.Value, "kCFTypeDictionaryKeyCallBacks"),
        NativeLibrary.GetExport(foundationLibrary.Value, "kCFTypeDictionaryValueCallBacks"));

    private static IntPtr MacQuery(string target, bool returnData = false)
    {
        var service = CFStringCreateWithCharacters(IntPtr.Zero, KeychainService, KeychainService.Length);
        var account = CFStringCreateWithCharacters(IntPtr.Zero, target, target.Length);
        try
        {
            var pairs = new List<(string, IntPtr)> { ("kSecClass", SecConstant("kSecClassGenericPassword")),
                ("kSecAttrService", service), ("kSecAttrAccount", account) };
            if (returnData)
            {
                pairs.Add(("kSecReturnData", CfConstant("kCFBooleanTrue")));
                pairs.Add(("kSecMatchLimit", SecConstant("kSecMatchLimitOne")));
            }
            return MacDictionary(pairs.ToArray());
        }
        finally { CFRelease(service); CFRelease(account); }
    }

    private static void CheckMacStatus(int status)
    {
        if (status != 0) throw new IOException("无法访问 macOS 钥匙串（状态 " + status + "）。");
    }

    private static byte[]? MacRead(string target)
    {
        var query = MacQuery(target, true);
        try
        {
            var status = SecItemCopyMatching(query, out var data);
            if (status == -25300) return null;
            CheckMacStatus(status);
            try
            {
                var length = CFDataGetLength(data);
                if (length is < 1 or > 2560) throw new IOException("保存的登录会话无效。");
                var bytes = new byte[(int)length];
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
                return bytes;
            }
            finally { CFRelease(data); }
        }
        finally { CFRelease(query); }
    }

    private static void MacWrite(string target, byte[] bytes)
    {
        var query = MacQuery(target);
        var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        var changes = MacDictionary(("kSecValueData", data));
        try
        {
            var status = SecItemUpdate(query, changes);
            if (status == -25300)
            {
                var service = CFStringCreateWithCharacters(IntPtr.Zero, KeychainService, KeychainService.Length);
                var account = CFStringCreateWithCharacters(IntPtr.Zero, target, target.Length);
                try
                {
                    var attributes = MacDictionary(("kSecClass", SecConstant("kSecClassGenericPassword")),
                        ("kSecAttrService", service), ("kSecAttrAccount", account), ("kSecValueData", data));
                    try { status = SecItemAdd(attributes, IntPtr.Zero); }
                    finally { CFRelease(attributes); }
                }
                finally { CFRelease(service); CFRelease(account); }
            }
            CheckMacStatus(status);
        }
        finally { CFRelease(changes); CFRelease(data); CFRelease(query); }
    }

    private static void MacDelete(string target)
    {
        var query = MacQuery(target);
        try { var status = SecItemDelete(query); if (status != -25300) CheckMacStatus(status); }
        finally { CFRelease(query); }
    }
}
