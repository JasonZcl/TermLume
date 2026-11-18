namespace TermLume.Shared.Abstractions;

// 刷新凭据仅交给操作系统凭据库，不属于工作区或桌面桥消息。
public sealed record SavedAegisSession(string Issuer, string ClientId, string Subject, string RefreshToken)
{
    public override string ToString() => nameof(SavedAegisSession);
}

public interface IAegisSessionStore : IDisposable
{
    SavedAegisSession? Read();
    void Write(SavedAegisSession session);
    void Delete();
}
