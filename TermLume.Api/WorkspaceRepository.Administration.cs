using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TermLume.Shared.Models;
using TermLume.Shared.Services;

namespace TermLume.Api;

public sealed partial class WorkspaceRepository
{
    public async Task<object> AdministrationAsync(ApiIdentity identity, string platform, string? selected, string action, JsonElement payload, CancellationToken token)
    {
        if (string.IsNullOrEmpty(action) || action.Length > 64 || payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("操作参数无效。");
        var global = action is "workspace.list" or "workspace.create" or "workspace.join" or "capabilities" || action.StartsWith("device.", StringComparison.Ordinal);
        await using var session = await OpenSession(identity, platform, global ? null : selected, token);
        await LockWorkspace(session, token);
        var workspaceId = Guid.Parse(session.Context.Id);
        string Field(string name, int maximum = 100) => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Text(value.GetString()!, maximum) : throw new InvalidDataException("操作参数缺失或类型无效。");
        Guid Id(string name = "id") => Guid.TryParse(Field(name), out var value) ? value : throw new InvalidDataException("记录标识无效。");
        long Number(string name, long fallback = 0) => !payload.TryGetProperty(name, out var field) ? fallback : field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var value) ? value : throw new InvalidDataException("数值参数无效。");
        object result;
        switch (action)
        {
            case "capabilities": result = new { version = TermLumeApiModule.ApiVersion, collaboration = true, offlineOperations = true }; break;
            case "workspace.list":
            {
                var list = new List<WorkspaceDescriptor>();
                await using var command = session.Query("SELECT id,name,kind,termlume.workspace_role(id),revision FROM termlume.workspaces ORDER BY kind,name,id");
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(new(reader.GetGuid(0).ToString("N"), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
                result = list; break;
            }
            case "workspace.create":
            {
                if ((long)(await session.Get("SELECT count(*) FROM termlume.workspaces WHERE owner_id=$1", token, session.UserId))! >= 21) throw new InvalidDataException("最多可创建 20 个共享工作区。");
                var id = Guid.NewGuid(); var name = Field("name");
                await session.Run("INSERT INTO termlume.workspaces(id,owner_id,name,kind) VALUES($1,$2,$3,'shared')", token, id, session.UserId, name);
                await session.Run("INSERT INTO termlume.workspace_members(workspace_id,user_id,role,display_name) VALUES($1,$2,'owner',$3)", token, id, session.UserId, identity.DisplayName);
                session.Context = await Describe(session, id, token);
                await session.Audit("workspace.created", id.ToString("N"), "已创建共享工作区。", token);
                result = session.Context; break;
            }
            case "workspace.rename":
                RequireManage(session);
                await session.Run("UPDATE termlume.workspaces SET name=$1 WHERE id=$2", token, Field("name"), workspaceId);
                await session.Audit("workspace.renamed", session.Context.Id, "工作区已重命名。", token);
                result = await Describe(session, workspaceId, token); break;
            case "workspace.delete":
                if (session.Context.Role != "owner" || session.Context.Kind != "shared") throw new WorkspacePermissionException("只有所有者可以删除共享工作区；个人工作区不能删除。");
                await session.Audit("workspace.deleted", session.Context.Id, "共享工作区已删除。", token);
                await session.Run("DELETE FROM termlume.workspaces WHERE id=$1", token, workspaceId); result = true; break;
            case "workspace.invite":
            {
                RequireManage(session);
                if (session.Context.Kind != "shared") throw new InvalidDataException("个人工作区不能邀请成员。");
                var role = Field("role"); if (role is not ("admin" or "editor" or "viewer")) throw new InvalidDataException("成员权限无效。");
                var hours = Number("hours", 24); if (hours is < 1 or > 168) throw new InvalidDataException("邀请有效期须为 1 至 168 小时。");
                if ((long)(await session.Get("SELECT count(*) FROM termlume.workspace_invites WHERE workspace_id=$1 AND NOT revoked AND used_by IS NULL AND expires_at>now()", token, workspaceId))! >= 50)
                    throw new InvalidDataException("请先撤销未使用的邀请。");
                var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var id = Guid.NewGuid(); var expires = DateTimeOffset.UtcNow.AddHours(hours);
                await session.Run("INSERT INTO termlume.workspace_invites(id,workspace_id,token_hash,role,expires_at,created_by) VALUES($1,$2,$3,$4,$5,$6)", token, id, workspaceId, Hash(code), role, expires.UtcDateTime, session.UserId);
                await session.Audit("member.invited", id.ToString("N"), "已创建一次性成员邀请。", token);
                result = new { id = id.ToString("N"), code, role, expiresAt = expires }; break;
            }
            case "workspace.invites":
            {
                RequireManage(session); var list = new List<object>();
                await using var command = session.Query("SELECT id,role,expires_at,used_by IS NOT NULL,revoked FROM termlume.workspace_invites WHERE workspace_id=$1 ORDER BY created_at DESC LIMIT 100", workspaceId);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(new { id = reader.GetGuid(0).ToString("N"), role = reader.GetString(1), expiresAt = reader.GetDateTime(2), used = reader.GetBoolean(3), revoked = reader.GetBoolean(4) });
                result = list; break;
            }
            case "workspace.revokeInvite":
                RequireManage(session); await session.Run("UPDATE termlume.workspace_invites SET revoked=true WHERE workspace_id=$1 AND id=$2", token, workspaceId, Id());
                await session.Audit("member.invitationRevoked", Id().ToString("N"), "成员邀请已撤销。", token); result = true; break;
            case "workspace.join":
            {
                var code = Field("code", 64).ToUpperInvariant();
                if (!Regex.IsMatch(code, "\\A[0-9A-F]{64}\\z")) throw new InvalidDataException("邀请码无效或已过期。");
                var joined = await session.Get("SELECT termlume.redeem_invite($1)", token, Hash(code));
                if (joined is not Guid joinedId) throw new InvalidDataException("邀请码无效、已使用或已过期。");
                session.Context = await Describe(session, joinedId, token);
                await session.Audit("member.joined", session.UserId.ToString("N"), "新成员已加入工作区。", token);
                result = session.Context; break;
            }
            case "workspace.members":
            {
                var list = new List<object>();
                await using var command = session.Query("SELECT user_id,display_name,role,joined_at FROM termlume.workspace_members WHERE workspace_id=$1 ORDER BY joined_at,user_id", workspaceId);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(new { id = reader.GetGuid(0).ToString("N"), name = reader.GetString(1), role = reader.GetString(2), joinedAt = reader.GetDateTime(3), current = reader.GetGuid(0) == session.UserId });
                result = list; break;
            }
            case "workspace.memberRole":
            case "workspace.removeMember":
            {
                RequireManage(session); var target = Id();
                var current = await session.Get("SELECT role FROM termlume.workspace_members WHERE workspace_id=$1 AND user_id=$2", token, workspaceId, target) as string;
                if (current is null) throw new KeyNotFoundException(); if (current == "owner") throw new InvalidDataException("请先转让工作区所有权。");
                if (action == "workspace.memberRole")
                {
                    var role = Field("role"); if (role is not ("admin" or "editor" or "viewer")) throw new InvalidDataException("成员权限无效。");
                    await session.Run("UPDATE termlume.workspace_members SET role=$1 WHERE workspace_id=$2 AND user_id=$3", token, role, workspaceId, target);
                }
                else await session.Run("DELETE FROM termlume.workspace_members WHERE workspace_id=$1 AND user_id=$2", token, workspaceId, target);
                await session.Audit(action == "workspace.memberRole" ? "member.roleChanged" : "member.removed", target.ToString("N"), "工作区成员权限已更新。", token);
                result = true; break;
            }
            case "workspace.leave":
                if (session.Context.Role == "owner") throw new InvalidDataException("所有者须先转让所有权，才能退出工作区。");
                await session.Audit("member.left", session.UserId.ToString("N"), "成员已退出工作区。", token);
                await session.Run("DELETE FROM termlume.workspace_members WHERE workspace_id=$1 AND user_id=$2", token, workspaceId, session.UserId); result = true; break;
            case "workspace.transfer":
                if (!(bool)(await session.Get("SELECT termlume.transfer_workspace($1,$2)", token, workspaceId, Id()))!) throw new WorkspacePermissionException("只有所有者可以将共享工作区转让给现有成员。");
                await session.Audit("workspace.transferred", Id().ToString("N"), "工作区所有权已转让。", token); result = true; break;
            case "workspace.history":
            {
                var list = new List<object>();
                await using var command = session.Query("SELECT revision,actor_name,operation,created_at FROM termlume.workspace_history WHERE workspace_id=$1 ORDER BY revision DESC LIMIT 50", workspaceId);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(new { revision = reader.GetInt64(0), actor = reader.GetString(1), operation = reader.GetString(2), createdAt = reader.GetDateTime(3) });
                result = list; break;
            }
            case "workspace.historyItem":
            case "workspace.restore":
            {
                var revision = Number("revision");
                var json = await session.Get("SELECT snapshot::text FROM termlume.workspace_history WHERE workspace_id=$1 AND revision=$2", token, workspaceId, revision) as string ?? throw new KeyNotFoundException();
                if (action == "workspace.historyItem") { result = JsonSerializer.Deserialize<JsonElement>(json); break; }
                RequireWrite(session);
                if (Number("expectedRevision", -1) != session.Context.Revision) throw new WorkspaceConflictException();
                var original = await ReadWorkspace(session.Connection, session.Transaction, session.UserId, workspaceId, platform, token);
                var restored = JsonSerializer.Deserialize<Workspace>(json, WorkspaceStore.JsonOptions)!;
                restored.Preferences = original.Preferences;
                foreach (var host in restored.Hosts)
                { var local = original.Hosts.Find(item => item.Id == host.Id); host.Favorite = local?.Favorite ?? false; host.LastConnectedAt = local?.LastConnectedAt; }
                WorkspaceChanges.Validate(restored, platform == "macos");
                await History(session, session.Context.Revision, "baseline", original, token);
                await SaveWorkspace(session.Connection, session.Transaction, session.UserId, workspaceId, platform, restored, token);
                var next = (long)(await session.Get("SELECT termlume.bump_revision($1)", token, workspaceId))!;
                await History(session, next, "restore", restored, token);
                await session.Audit("workspace.restored", revision.ToString(), "工作区已恢复到选定的历史版本。", token);
                result = new WorkspaceSnapshot(next, restored, session.Context with { Revision = next }); break;
            }
            case "device.list":
            {
                var list = new List<object>();
                await using var command = session.Query("SELECT id,name,platform,version,active,first_seen,last_seen FROM termlume.devices WHERE user_id=$1 ORDER BY last_seen DESC", session.UserId);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(new { id = reader.GetGuid(0).ToString("N"), name = reader.GetString(1), platform = reader.GetString(2), version = reader.GetString(3), active = reader.GetBoolean(4), firstSeen = reader.GetDateTime(5), lastSeen = reader.GetDateTime(6), current = identity.DeviceId == reader.GetGuid(0) });
                result = list; break;
            }
            case "device.rename":
                await session.Run("UPDATE termlume.devices SET name=$1 WHERE user_id=$2 AND id=$3", token, Field("name", 80), session.UserId, Id()); result = true; break;
            case "device.revoke":
                await session.Run("UPDATE termlume.devices SET active=false WHERE user_id=$1 AND id=$2", token, session.UserId, Id());
                await session.Run("UPDATE termlume.device_grants SET revoked=true WHERE user_id=$1 AND device_id=$2", token, session.UserId, Id());
                await session.Audit("device.revoked", Id().ToString("N"), "设备的工作区访问已撤销。", token); result = true; break;
            case "trust.list":
            {
                var list = new List<object>();
                await using var command = session.Query("SELECT entry::text,published_by,created_at FROM termlume.trust_references WHERE workspace_id=$1 ORDER BY endpoint", workspaceId);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(new { entry = JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)), publishedBy = reader.GetString(1), createdAt = reader.GetDateTime(2) });
                result = list; break;
            }
            case "trust.publish":
            {
                RequireManage(session);
                var entry = (payload.TryGetProperty("entry", out var reference) ? reference : throw new InvalidDataException("信任记录缺失。必填参数 entry。")).Deserialize<KnownHostEntry>(WorkspaceStore.JsonOptions) ?? throw new InvalidDataException("信任记录无效。");
                TrustReferenceValidation.Validate(entry);
                entry = entry with { Metadata = new KnownHostMetadata() };
                var existing = await session.Get("SELECT entry::text FROM termlume.trust_references WHERE workspace_id=$1 AND endpoint=$2", token, workspaceId, entry.Endpoint) as string;
                if (existing is not null && JsonSerializer.Deserialize<KnownHostEntry>(existing, WorkspaceStore.JsonOptions)!.Fingerprint != entry.Fingerprint)
                    throw new InvalidDataException("账号中已有不同指纹，请先核验并移除旧记录，不能直接覆盖。");
                await session.Run("INSERT INTO termlume.trust_references(workspace_id,endpoint,entry,published_by) VALUES($1,$2,$3::jsonb,$4) ON CONFLICT DO NOTHING", token, workspaceId, entry.Endpoint, JsonSerializer.Serialize(entry, WorkspaceStore.JsonOptions), identity.DisplayName);
                await session.Audit("trust.published", entry.Endpoint, "已发布经确认的服务器指纹参考记录。", token); result = true; break;
            }
            case "trust.remove":
                RequireManage(session); await session.Run("DELETE FROM termlume.trust_references WHERE workspace_id=$1 AND endpoint=$2", token, workspaceId, Field("endpoint", 500));
                await session.Audit("trust.removed", null, "账号中的服务器指纹参考记录已移除。", token); result = true; break;
            case "activity.upload":
            {
                if (identity.DeviceId is not { } device) throw new InvalidDataException("请使用已登记设备上传日志。");
                var entries = (payload.TryGetProperty("entries", out var batch) ? batch : throw new InvalidDataException("日志数据缺失。必填参数 entries。")).Deserialize<List<ActivityLogEntry>>(WorkspaceStore.JsonOptions) ?? throw new InvalidDataException("日志数据无效。");
                if (entries.Count > 200) throw new InvalidDataException("每批最多上传 200 条日志。");
                foreach (var raw in entries)
                {
                    var entry = ActivityPrivacy.Sanitize(raw);
                    await session.Run("INSERT INTO termlume.device_activity(user_id,device_id,entry_id,workspace_id,event_time,entry) VALUES($1,$2,$3,$4,$5,$6::jsonb) ON CONFLICT DO NOTHING", token,
                        session.UserId, device, Guid.Parse(entry.Id), workspaceId, entry.Timestamp.UtcDateTime, JsonSerializer.Serialize(entry, WorkspaceStore.JsonOptions));
                }
                result = new { accepted = entries.Count }; break;
            }
            case "activity.list":
            case "audit.list":
            {
                if (action == "audit.list") RequireManage(session);
                var limit = (int)Math.Clamp(Number("limit", 100), 1, 200); var offset = (int)Math.Clamp(Number("offset"), 0, 100000);
                var list = new List<object>();
                var sql = action == "activity.list"
                    ? "SELECT a.entry::text,d.name,a.device_id,a.received_at FROM termlume.device_activity a JOIN termlume.devices d ON d.user_id=a.user_id AND d.id=a.device_id WHERE a.user_id=$1 AND a.workspace_id=$2 AND a.received_at>=termlume.server_log_cutoff() ORDER BY a.event_time DESC,a.entry_id LIMIT $3 OFFSET $4"
                    : "SELECT id,action,summary,actor_name,created_at FROM termlume.audit_events WHERE (workspace_id=$2 OR (workspace_id IS NULL AND actor_id=$1)) AND created_at>=termlume.server_log_cutoff() ORDER BY id DESC LIMIT $3 OFFSET $4";
                await using var command = session.Query(sql, session.UserId, workspaceId, limit + 1, offset);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    if (action == "activity.list") list.Add(new { entry = JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)), deviceName = reader.GetString(1), deviceId = reader.GetGuid(2).ToString("N"), receivedAt = reader.GetDateTime(3), source = "device" });
                    else list.Add(new { id = reader.GetInt64(0).ToString(), action = reader.GetString(1), summary = reader.GetString(2), actor = reader.GetString(3), createdAt = reader.GetDateTime(4), source = "server" });
                }
                result = new { entries = list.Take(limit), offset, hasMore = list.Count > limit }; break;
            }
            case "activity.clear":
                await session.Run("DELETE FROM termlume.device_activity WHERE user_id=$1 AND workspace_id=$2", token, session.UserId, workspaceId);
                await session.Audit("activity.cleared", null, "当前账号的跨设备活动日志已清空。", token); result = true; break;
            default: throw new InvalidDataException("不支持的工作区管理操作。");
        }
        await session.Commit(token);
        return result;
    }
}
