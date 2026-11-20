using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using TermLume.Shared.Models;
using TermLume.Shared.Services;

namespace TermLume.Api;

public sealed partial class WorkspaceRepository
{
    public Task<WorkspaceSnapshot> ReadAsync(ApiIdentity identity, string platform, CancellationToken token, string? selected = null) => ExecuteAsync(identity, platform, null, null, token, selected);
    public Task<WorkspaceSnapshot> ChangeAsync(ApiIdentity identity, string platform, WorkspaceChange change, CancellationToken token, string? selected = null) => ExecuteAsync(identity, platform, change, null, token, selected);
    public Task<WorkspaceSnapshot> ImportAsync(ApiIdentity identity, string platform, WorkspaceImport import, CancellationToken token, string? selected = null) => ExecuteAsync(identity, platform, null, import, token, selected);

    private async Task<WorkspaceSnapshot> ExecuteAsync(ApiIdentity identity, string platform, WorkspaceChange? change, WorkspaceImport? import, CancellationToken token, string? selected)
    {
        await using var session = await OpenSession(identity, platform, selected, token);
        await LockWorkspace(session, token);
        var workspaceId = Guid.Parse(session.Context.Id);
        var revision = session.Context.Revision;
        var workspace = await ReadWorkspace(session.Connection, session.Transaction, session.UserId, workspaceId, platform, token);
        if (change is not null || import is not null)
        {
            var personal = change?.Operation is "favorite" or "connected" or "preferences";
            if (!personal) RequireWrite(session);
            var operationId = change?.OperationId is null ? (Guid?)null : Guid.TryParseExact(change.OperationId, "N", out var parsed) ? parsed : throw new InvalidDataException("操作标识无效。");
            var requestHash = change is null ? "" : Hash(JsonSerializer.Serialize(change with { ExpectedRevision = 0 }, WorkspaceStore.JsonOptions));
            var applied = operationId is null ? null : await session.Get("SELECT request_hash FROM termlume.applied_operations WHERE workspace_id=$1 AND user_id=$2 AND operation_id=$3", token, workspaceId, session.UserId, operationId.Value) as string;
            if (applied is not null)
            {
                if (applied != requestHash) throw new InvalidDataException("重复操作标识对应的内容不一致。");
                await session.Commit(token);
                return new WorkspaceSnapshot(revision, workspace, session.Context);
            }
            if ((change?.ExpectedRevision ?? import!.ExpectedRevision) != revision) throw new WorkspaceConflictException();
            if (!personal) await History(session, revision, "baseline", workspace, token);
            if (change is not null) workspace = WorkspaceChanges.Apply(workspace, change.Operation, change.Payload, platform == "macos", change.NewId);
            else workspace = MergeImport(workspace, import!.Workspace, revision, platform == "macos");
            await SaveWorkspace(session.Connection, session.Transaction, session.UserId, workspaceId, platform, workspace, token, !personal);
            revision = (long)(await session.Get("SELECT termlume.bump_revision($1)", token, workspaceId))!;
            if (!personal)
            {
                await History(session, revision, change?.Operation ?? "import", workspace, token);
                await session.Audit("workspace." + (change?.Operation ?? "import"), null, "工作区配置已更新。", token);
            }
            if (operationId is not null) await session.Run("INSERT INTO termlume.applied_operations(workspace_id,user_id,operation_id,request_hash,revision) VALUES($1,$2,$3,$4,$5)", token, workspaceId, session.UserId, operationId.Value, requestHash, revision);
        }
        await session.Commit(token);
        return new WorkspaceSnapshot(revision, workspace, session.Context with { Revision = revision });
    }

    private static Workspace MergeImport(Workspace current, Workspace imported, long revision, bool macOS)
    {
        WorkspaceChanges.Validate(imported, macOS);
        var merged = WorkspaceChanges.Clone(current);
        merged.Groups = current.Groups.Concat(imported.Groups).Distinct(StringComparer.Ordinal).ToList();
        foreach (var host in imported.Hosts)
        {
            var existing = merged.Hosts.Find(item => item.Id == host.Id);
            if (existing is null) merged.Hosts.Add(host);
            else if (JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(host)) throw new InvalidDataException("旧配置中的主机标识与云端记录冲突，请先导出并整理配置。");
        }
        foreach (var snippet in imported.Snippets)
        {
            var existing = merged.Snippets.Find(item => item.Id == snippet.Id);
            if (existing is null) merged.Snippets.Add(snippet);
            else if (existing.Name != snippet.Name || existing.Command != snippet.Command) throw new InvalidDataException("旧配置中的片段标识与云端记录冲突，请先导出并整理配置。");
        }
        if (revision == 0) merged.Preferences = imported.Preferences;
        WorkspaceChanges.Validate(merged, macOS);
        return merged;
    }

    private static async Task<Workspace> ReadWorkspace(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, Guid workspaceId, string platform, CancellationToken cancellationToken)
    {
        var result = new Workspace();
        await using (var command = Command(connection, transaction, "SELECT name FROM termlume.host_groups WHERE workspace_id=$1 ORDER BY sort_order,id", workspaceId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) result.Groups.Add(reader.GetString(0));
        await using (var command = Command(connection, transaction, """
            SELECT h.id,h.name,h.address,h.port,h.username,g.name,h.color,h.auth_type,h.note,
              COALESCE(s.favorite,false),s.last_connected_at
            FROM termlume.hosts h JOIN termlume.host_groups g ON g.workspace_id=h.workspace_id AND g.id=h.group_id
            LEFT JOIN termlume.user_host_state s ON s.workspace_id=h.workspace_id AND s.host_id=h.id AND s.user_id=$2
            WHERE h.workspace_id=$1 ORDER BY h.sort_order,h.id
            """, workspaceId, userId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) result.Hosts.Add(new HostProfile
            {
                Id = reader.GetGuid(0).ToString("N"), Name = reader.GetString(1), Address = reader.GetString(2), Port = reader.GetInt32(3),
                Username = reader.GetString(4), Group = reader.GetString(5), Color = reader.GetString(6), AuthType = reader.GetString(7),
                Note = reader.GetString(8), Favorite = reader.GetBoolean(9), LastConnectedAt = reader.IsDBNull(10) ? null : new DateTimeOffset(reader.GetDateTime(10))
            });
        await using (var command = Command(connection, transaction, "SELECT id,name,command FROM termlume.snippets WHERE workspace_id=$1 ORDER BY sort_order,id", workspaceId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) result.Snippets.Add(new Snippet { Id = reader.GetGuid(0).ToString("N"), Name = reader.GetString(1), Command = reader.GetString(2) });
        await using (var command = Command(connection, transaction, "SELECT platform,settings::text FROM termlume.user_preferences WHERE user_id=$1 AND platform IN ('common',$2) ORDER BY platform", userId, platform))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            JsonObject combined = [];
            while (await reader.ReadAsync(cancellationToken))
            {
                var settings = JsonNode.Parse(reader.GetString(1))!.AsObject();
                foreach (var pair in settings) combined[pair.Key] = pair.Value?.DeepClone();
            }
            result.Preferences = combined.Deserialize<Preferences>(WorkspaceStore.JsonOptions) ?? new Preferences();
        }
        WorkspaceChanges.Validate(result, platform == "macos");
        return result;
    }

    private static async Task SaveWorkspace(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, Guid workspaceId, string platform, Workspace workspace, CancellationToken cancellationToken, bool assets = true)
    {
        if (assets)
        {
        var groupIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var index = 0; index < workspace.Groups.Count; index++)
        {
            var name = workspace.Groups[index];
            groupIds[name] = (Guid)(await Scalar(connection, transaction,
                "INSERT INTO termlume.host_groups(id,workspace_id,name,sort_order) VALUES($1,$2,$3,$4) ON CONFLICT(workspace_id,name) DO UPDATE SET sort_order=EXCLUDED.sort_order RETURNING id",
                cancellationToken, Guid.NewGuid(), workspaceId, name, index))!;
        }
        await Execute(connection, transaction, "DELETE FROM termlume.hosts WHERE workspace_id=$1 AND NOT(id=ANY($2))", cancellationToken, workspaceId, workspace.Hosts.Select(host => Guid.Parse(host.Id)).ToArray());
        for (var index = 0; index < workspace.Hosts.Count; index++)
        {
            var host = workspace.Hosts[index]; var id = Guid.Parse(host.Id);
            await Execute(connection, transaction, """
                INSERT INTO termlume.hosts(id,workspace_id,group_id,name,address,port,username,auth_type,color,note,sort_order)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                ON CONFLICT(workspace_id,id) DO UPDATE SET group_id=EXCLUDED.group_id,name=EXCLUDED.name,address=EXCLUDED.address,
                port=EXCLUDED.port,username=EXCLUDED.username,auth_type=EXCLUDED.auth_type,color=EXCLUDED.color,note=EXCLUDED.note,sort_order=EXCLUDED.sort_order
                """, cancellationToken, id, workspaceId, groupIds[host.Group], host.Name, host.Address, host.Port, host.Username, host.AuthType, host.Color, host.Note, index);
        }
        await Execute(connection, transaction, "DELETE FROM termlume.host_groups WHERE workspace_id=$1 AND NOT(name=ANY($2))", cancellationToken, workspaceId, workspace.Groups.ToArray());
        await Execute(connection, transaction, "DELETE FROM termlume.snippets WHERE workspace_id=$1 AND NOT(id=ANY($2))", cancellationToken, workspaceId, workspace.Snippets.Select(snippet => Guid.Parse(snippet.Id)).ToArray());
        for (var index = 0; index < workspace.Snippets.Count; index++)
        {
            var snippet = workspace.Snippets[index];
            await Execute(connection, transaction, """
                INSERT INTO termlume.snippets(id,workspace_id,name,command,sort_order) VALUES($1,$2,$3,$4,$5)
                ON CONFLICT(workspace_id,id) DO UPDATE SET name=EXCLUDED.name,command=EXCLUDED.command,sort_order=EXCLUDED.sort_order
                """, cancellationToken, Guid.Parse(snippet.Id), workspaceId, snippet.Name, snippet.Command, index);
        }
        }
        foreach (var host in workspace.Hosts)
            await Execute(connection, transaction, "INSERT INTO termlume.user_host_state(user_id,workspace_id,host_id,favorite,last_connected_at) VALUES($1,$2,$3,$4,$5) ON CONFLICT(user_id,workspace_id,host_id) DO UPDATE SET favorite=EXCLUDED.favorite,last_connected_at=EXCLUDED.last_connected_at", cancellationToken, userId, workspaceId, Guid.Parse(host.Id), host.Favorite, host.LastConnectedAt?.UtcDateTime);
        var common = JsonSerializer.SerializeToNode(workspace.Preferences, WorkspaceStore.JsonOptions)!.AsObject();
        var device = new JsonObject();
        foreach (var key in new[] { "shortcuts", "fontFamily", "sidebarCollapsed" }) { device[key] = common[key]?.DeepClone(); common.Remove(key); }
        foreach (var item in new[] { ("common", common), (platform, device) })
        {
            await using var command = Command(connection, transaction,
                "INSERT INTO termlume.user_preferences(user_id,platform,settings) VALUES($1,$2,$3) ON CONFLICT(user_id,platform) DO UPDATE SET settings=EXCLUDED.settings", userId, item.Item1);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = item.Item2.ToJsonString() });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            if (value is null) command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Unknown, Value = DBNull.Value });
            else command.Parameters.AddWithValue(value);
        }
        return command;
    }
    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token, params object?[] values)
    { await using var command = Command(connection, transaction, sql, values); await command.ExecuteNonQueryAsync(token); }
    private static async Task<object?> Scalar(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token, params object?[] values)
    { await using var command = Command(connection, transaction, sql, values); return await command.ExecuteScalarAsync(token); }
}
