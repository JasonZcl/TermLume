-- 按服务端时钟统一保留七天；查询与清理必须使用同一个截止时间规则。
CREATE FUNCTION termlume.server_log_cutoff() RETURNS timestamptz
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT statement_timestamp()-interval '7 days'
$$;

CREATE INDEX activity_received_at ON termlume.device_activity(received_at);
CREATE INDEX audit_created_at ON termlume.audit_events(created_at);

-- 运行角色不能任意删除审计。此函数只允许清理固定期限以前的日志，
-- 不接收账号、表名或截止时间，也不返回其他账号的数据。
CREATE FUNCTION termlume.prune_expired_logs() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,termlume AS $$
DECLARE cutoff timestamptz := termlume.server_log_cutoff();
BEGIN
 IF NOT pg_try_advisory_xact_lock(732861042) THEN RETURN; END IF;
 DELETE FROM termlume.device_activity WHERE received_at<cutoff;
 DELETE FROM termlume.audit_events WHERE created_at<cutoff;
END $$;

REVOKE ALL ON FUNCTION termlume.server_log_cutoff(),termlume.prune_expired_logs() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION termlume.server_log_cutoff(),termlume.prune_expired_logs() TO termlume_app;
