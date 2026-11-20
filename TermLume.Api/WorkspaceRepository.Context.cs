using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TermLume.Shared.Models;
using TermLume.Shared.Services;

namespace TermLume.Api;

public sealed partial class WorkspaceRepository
{
    private readonly NpgsqlDataSource database;
    public WorkspaceRepository(NpgsqlDataSource database) => this.database = database;

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, ApiIdentity identity) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;
        public Guid UserId { get; } = userId;
        public ApiIdentity Identity { get; } = identity;
        public WorkspaceDescriptor Context { get; set; } = null!;
        public Task Run(string sql, CancellationToken token, params object?[] args) => Execute(Connection, Transaction, sql, token, args);
        public Task<object?> Get(string sql, CancellationToken token, params object?[] args) => Scalar(Connection, Transaction, sql, token, args);
        public NpgsqlCommand Query(string sql, params object?[] args) => Command(Connection, Transaction, sql, args);
        public Task Commit(CancellationToken token) => Transaction.CommitAsync(token);
        public async ValueTask DisposeAsync() { await Transaction.DisposeAsync(); await Connection.DisposeAsync(); }
        public Task Audit(string action, string? entity, string summary, CancellationToken token) => Run(
            "INSERT INTO termlume.audit_events(workspace_id,actor_id,actor_name,action,entity_id,summary) VALUES($1,$2,$3,$4,$5,$6)",
            token, Guid.Parse(Context.Id), UserId, Identity.DisplayName, action, entity, summary);
    }

    private async Task<Session> OpenSession(ApiIdentity identity, string platform, string? selected, CancellationToken token, bool requireWorkspace = true)
    {
        if (platform is not ("windows" or "macos")) throw new InvalidDataException("设备平台无效。");
        var connection = await database.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(token);
            // true 将 RLS 身份限定在当前事务，归还连接池后不得残留其他账号的上下文。
            await Execute(connection, transaction, "SELECT set_config('termlume.issuer',$1,true),set_config('termlume.subject',$2,true)", token, identity.Issuer, identity.Subject);
            var userId = (Guid)(await Scalar(connection, transaction,
                "INSERT INTO termlume.app_users(id,issuer,subject,display_name) VALUES($1,$2,$3,$4) ON CONFLICT(issuer,subject) DO UPDATE SET display_name=EXCLUDED.display_name RETURNING id",
                token, Guid.NewGuid(), identity.Issuer, identity.Subject, identity.DisplayName))!;
            var session = new Session(connection, transaction, userId, identity);
            await session.Run("INSERT INTO termlume.workspaces(id,owner_id) VALUES($1,$2) ON CONFLICT(owner_id) WHERE kind='personal' DO NOTHING", token, Guid.NewGuid(), userId);
            await session.Run("INSERT INTO termlume.workspace_members(workspace_id,user_id,role,display_name) SELECT id,$1,'owner',$2 FROM termlume.workspaces WHERE owner_id=$1 AND kind='personal' ON CONFLICT(workspace_id,user_id) DO UPDATE SET display_name=EXCLUDED.display_name", token, userId, identity.DisplayName);
            if (!string.IsNullOrEmpty(identity.GrantId) && (bool)(await session.Get("SELECT EXISTS(SELECT FROM termlume.device_grants WHERE user_id=$1 AND grant_id=$2 AND revoked)", token, userId, identity.GrantId))!)
                throw new WorkspaceAccessException();
            if (identity.DeviceId is { } device)
            {
                var bound = await session.Get("SELECT device_id FROM termlume.device_grants WHERE user_id=$1 AND grant_id=$2", token, userId, identity.GrantId);
                if (bound is Guid boundId && boundId != device) throw new WorkspaceAccessException();
                if ((long)(await session.Get("SELECT count(*) FROM termlume.devices WHERE user_id=$1 AND active AND id<>$2", token, userId, device))! >= 30)
                    throw new InvalidDataException("最多允许 30 台设备，请先移除不再使用的设备。");
                await session.Run("""
                    INSERT INTO termlume.devices(user_id,id,name,platform,version) VALUES($1,$2,$3,$4,$5)
                    ON CONFLICT(user_id,id) DO UPDATE SET last_seen=now(),version=EXCLUDED.version,active=true
                    """, token, userId, device, (platform == "macos" ? "macOS" : "Windows") + " · " + device.ToString("N")[..6], platform, identity.Version);
                await session.Run("INSERT INTO termlume.device_grants(user_id,device_id,grant_id) VALUES($1,$2,$3) ON CONFLICT DO NOTHING", token, userId, device, identity.GrantId);
            }
            if (requireWorkspace)
            {
                if (selected is not null && !Guid.TryParse(selected, out _)) throw new InvalidDataException("工作区标识无效。");
                session.Context = await Describe(session, selected is null ? null : Guid.Parse(selected), token);
            }
            return session;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }

    private static async Task<WorkspaceDescriptor> Describe(Session session, Guid? selected, CancellationToken token)
    {
        await using var command = selected is null
            ? session.Query("SELECT id,name,kind,termlume.workspace_role(id),revision FROM termlume.workspaces WHERE owner_id=$1 AND kind='personal'", session.UserId)
            : session.Query("SELECT id,name,kind,termlume.workspace_role(id),revision FROM termlume.workspaces WHERE id=$1", selected.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new WorkspacePermissionException("你已失去此工作区的访问权限。");
        return new(reader.GetGuid(0).ToString("N"), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
    }

    private static async Task LockWorkspace(Session session, CancellationToken token)
    {
        var id = Guid.Parse(session.Context.Id);
        await session.Run("SELECT pg_advisory_xact_lock(hashtextextended($1,84103))", token, id.ToString("N"));
        session.Context = await Describe(session, id, token);
    }

    private static void RequireWrite(Session session) { if (!session.Context.CanWrite) throw new WorkspacePermissionException("此工作区为只读，你可以连接主机和调整自己的收藏与偏好。"); }
    private static void RequireManage(Session session) { if (!session.Context.CanManage) throw new WorkspacePermissionException("只有工作区所有者或管理员可以执行此操作。"); }
    private static string Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw new InvalidDataException("名称或参数无效，请检查长度和字符。");
        return value.Trim();
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Assets(Workspace value)
    {
        var copy = WorkspaceChanges.Portable(value);
        foreach (var host in copy.Hosts) { host.Favorite = false; host.LastConnectedAt = null; }
        return JsonSerializer.Serialize(new { copy.Groups, copy.Hosts, copy.Snippets }, WorkspaceStore.JsonOptions);
    }
    private static async Task History(Session session, long revision, string operation, Workspace value, CancellationToken token)
    {
        await session.Run("INSERT INTO termlume.workspace_history(workspace_id,revision,actor_name,operation,snapshot) VALUES($1,$2,$3,$4,$5::jsonb) ON CONFLICT DO NOTHING",
            token, Guid.Parse(session.Context.Id), revision, session.Identity.DisplayName, operation, Assets(value));
        await session.Run("DELETE FROM termlume.workspace_history WHERE workspace_id=$1 AND revision NOT IN(SELECT revision FROM termlume.workspace_history WHERE workspace_id=$1 ORDER BY revision DESC LIMIT 50)", token, Guid.Parse(session.Context.Id));
    }
}
