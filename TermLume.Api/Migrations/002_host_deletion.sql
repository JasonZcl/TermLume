-- 工作区删除时，分组、主机和用户主机状态必须在同一事务内完整删除。
ALTER TABLE termlume.hosts DROP CONSTRAINT hosts_workspace_id_group_id_fkey;
ALTER TABLE termlume.hosts ADD CONSTRAINT hosts_workspace_id_group_id_fkey
  FOREIGN KEY (workspace_id, group_id) REFERENCES termlume.host_groups(workspace_id, id) ON DELETE CASCADE;
