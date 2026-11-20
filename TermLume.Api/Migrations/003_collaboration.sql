ALTER TABLE termlume.app_users ADD COLUMN display_name text NOT NULL DEFAULT '';
ALTER TABLE termlume.workspaces DROP CONSTRAINT workspaces_owner_id_key;
ALTER TABLE termlume.workspaces ADD COLUMN name text NOT NULL DEFAULT '个人工作区' CHECK(length(name) BETWEEN 1 AND 100);
ALTER TABLE termlume.workspaces ADD COLUMN kind text NOT NULL DEFAULT 'personal' CHECK(kind IN ('personal','shared'));
CREATE UNIQUE INDEX one_personal_workspace ON termlume.workspaces(owner_id) WHERE kind='personal';

CREATE TABLE termlume.workspace_members (
  workspace_id uuid NOT NULL REFERENCES termlume.workspaces(id) ON DELETE CASCADE,
  user_id uuid NOT NULL REFERENCES termlume.app_users(id) ON DELETE CASCADE,
  role text NOT NULL CHECK(role IN ('owner','admin','editor','viewer')),
  display_name text NOT NULL DEFAULT '',
  joined_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(workspace_id,user_id)
);
INSERT INTO termlume.workspace_members(workspace_id,user_id,role) SELECT id,owner_id,'owner' FROM termlume.workspaces;

CREATE FUNCTION termlume.current_user_id() RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$
 SELECT id FROM termlume.app_users WHERE issuer=current_setting('termlume.issuer',true) AND subject=current_setting('termlume.subject',true)
$$;
CREATE FUNCTION termlume.workspace_role(w uuid) RETURNS text LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$
 SELECT CASE WHEN owner_id=termlume.current_user_id() THEN 'owner'
 ELSE (SELECT role FROM termlume.workspace_members WHERE workspace_id=w AND user_id=termlume.current_user_id()) END
 FROM termlume.workspaces WHERE id=w
$$;
CREATE FUNCTION termlume.can_read(w uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$ SELECT termlume.workspace_role(w) IS NOT NULL $$;
CREATE FUNCTION termlume.can_write(w uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$ SELECT COALESCE(termlume.workspace_role(w) IN ('owner','admin','editor'),false) $$;
CREATE FUNCTION termlume.can_manage(w uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$ SELECT COALESCE(termlume.workspace_role(w) IN ('owner','admin'),false) $$;

DROP POLICY own_workspace ON termlume.workspaces;
CREATE POLICY workspace_read ON termlume.workspaces FOR SELECT USING(owner_id=termlume.current_user_id() OR termlume.can_read(id));
CREATE POLICY workspace_create ON termlume.workspaces FOR INSERT WITH CHECK(owner_id=termlume.current_user_id());
CREATE POLICY workspace_update ON termlume.workspaces FOR UPDATE USING(termlume.can_manage(id)) WITH CHECK(termlume.can_manage(id));
CREATE POLICY workspace_delete ON termlume.workspaces FOR DELETE USING(kind='shared' AND termlume.workspace_role(id)='owner');
ALTER TABLE termlume.workspace_members ENABLE ROW LEVEL SECURITY;
CREATE POLICY members_read ON termlume.workspace_members FOR SELECT USING(termlume.can_read(workspace_id));
CREATE POLICY members_insert ON termlume.workspace_members FOR INSERT WITH CHECK(termlume.can_manage(workspace_id));
CREATE POLICY members_update ON termlume.workspace_members FOR UPDATE USING(termlume.can_manage(workspace_id)) WITH CHECK(termlume.can_manage(workspace_id));
CREATE POLICY members_delete ON termlume.workspace_members FOR DELETE USING(termlume.can_manage(workspace_id) OR (user_id=termlume.current_user_id() AND role<>'owner'));

DROP POLICY own_groups ON termlume.host_groups;
CREATE POLICY groups_read ON termlume.host_groups FOR SELECT USING(termlume.can_read(workspace_id));
CREATE POLICY groups_write ON termlume.host_groups FOR ALL USING(termlume.can_write(workspace_id)) WITH CHECK(termlume.can_write(workspace_id));
DROP POLICY own_hosts ON termlume.hosts;
CREATE POLICY hosts_read ON termlume.hosts FOR SELECT USING(termlume.can_read(workspace_id));
CREATE POLICY hosts_write ON termlume.hosts FOR ALL USING(termlume.can_write(workspace_id)) WITH CHECK(termlume.can_write(workspace_id));
DROP POLICY own_snippets ON termlume.snippets;
CREATE POLICY snippets_read ON termlume.snippets FOR SELECT USING(termlume.can_read(workspace_id));
CREATE POLICY snippets_write ON termlume.snippets FOR ALL USING(termlume.can_write(workspace_id)) WITH CHECK(termlume.can_write(workspace_id));

CREATE FUNCTION termlume.check_workspace_owner() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$
DECLARE w uuid; o uuid; k text;
BEGIN
 IF TG_TABLE_NAME='workspaces' THEN w=COALESCE(NEW.id,OLD.id); ELSE w=COALESCE(NEW.workspace_id,OLD.workspace_id); END IF;
 SELECT owner_id,kind INTO o,k FROM termlume.workspaces WHERE id=w;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF NOT EXISTS(SELECT FROM termlume.workspace_members WHERE workspace_id=w AND user_id=o AND role='owner')
 OR (SELECT count(*) FROM termlume.workspace_members WHERE workspace_id=w AND role='owner')<>1
 OR (k='personal' AND (SELECT count(*) FROM termlume.workspace_members WHERE workspace_id=w)<>1) THEN
 RAISE EXCEPTION 'Invalid workspace owner' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER workspace_owner_members AFTER INSERT OR UPDATE OR DELETE ON termlume.workspace_members
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION termlume.check_workspace_owner();
CREATE CONSTRAINT TRIGGER workspace_owner_workspace AFTER INSERT OR UPDATE ON termlume.workspaces
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION termlume.check_workspace_owner();

CREATE FUNCTION termlume.bump_revision(w uuid) RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$
DECLARE r bigint;
BEGIN
 IF NOT termlume.can_read(w) THEN RAISE EXCEPTION 'Workspace access denied' USING ERRCODE='42501'; END IF;
 UPDATE termlume.workspaces SET revision=revision+1,updated_at=now() WHERE id=w RETURNING revision INTO r;
 RETURN r;
END $$;

CREATE TABLE termlume.workspace_invites (
 id uuid PRIMARY KEY,workspace_id uuid NOT NULL REFERENCES termlume.workspaces(id) ON DELETE CASCADE,
 token_hash text NOT NULL UNIQUE,role text NOT NULL CHECK(role IN ('admin','editor','viewer')),
 expires_at timestamptz NOT NULL,created_by uuid NOT NULL REFERENCES termlume.app_users(id) ON DELETE CASCADE,
 used_by uuid REFERENCES termlume.app_users(id) ON DELETE SET NULL,revoked boolean NOT NULL DEFAULT false,created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE termlume.workspace_invites ENABLE ROW LEVEL SECURITY;
CREATE POLICY invites_manage ON termlume.workspace_invites FOR ALL USING(termlume.can_manage(workspace_id)) WITH CHECK(termlume.can_manage(workspace_id));
CREATE FUNCTION termlume.redeem_invite(h text) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$
DECLARE i termlume.workspace_invites; u uuid; n text;
BEGIN
 u=termlume.current_user_id(); IF u IS NULL THEN RAISE EXCEPTION 'Account required' USING ERRCODE='42501'; END IF;
 SELECT * INTO i FROM termlume.workspace_invites WHERE token_hash=h;
 IF NOT FOUND THEN RETURN NULL; END IF;
 -- 与 API 工作区操作使用同一把锁，串行核验人数、邀请撤销及成员变更。
 PERFORM pg_advisory_xact_lock(hashtextextended(replace(i.workspace_id::text,'-',''),84103));
 SELECT * INTO i FROM termlume.workspace_invites WHERE token_hash=h FOR UPDATE;
 IF NOT FOUND OR i.revoked THEN RETURN NULL; END IF;
 IF i.used_by=u AND EXISTS(SELECT FROM termlume.workspace_members WHERE workspace_id=i.workspace_id AND user_id=u) THEN RETURN i.workspace_id; END IF;
 IF i.used_by IS NOT NULL OR i.expires_at<=now() THEN RETURN NULL; END IF;
 IF EXISTS(SELECT FROM termlume.workspace_members WHERE workspace_id=i.workspace_id AND user_id=u) THEN RETURN i.workspace_id; END IF;
 IF (SELECT count(*) FROM termlume.workspace_members WHERE workspace_id=i.workspace_id)>=100 THEN RETURN NULL; END IF;
 SELECT display_name INTO n FROM termlume.app_users WHERE id=u;
 INSERT INTO termlume.workspace_members(workspace_id,user_id,role,display_name) VALUES(i.workspace_id,u,i.role,n);
 UPDATE termlume.workspace_invites SET used_by=u WHERE id=i.id;
 RETURN i.workspace_id;
END $$;
CREATE FUNCTION termlume.transfer_workspace(w uuid, successor uuid) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
SET search_path=pg_catalog,termlume AS $$
DECLARE previous uuid;
BEGIN
 SELECT owner_id INTO previous FROM termlume.workspaces WHERE id=w AND kind='shared' FOR UPDATE;
 IF previous IS NULL OR previous<>termlume.current_user_id() OR previous=successor
 OR NOT EXISTS(SELECT FROM termlume.workspace_members WHERE workspace_id=w AND user_id=successor) THEN RETURN false; END IF;
 UPDATE termlume.workspaces SET owner_id=successor WHERE id=w;
 UPDATE termlume.workspace_members SET role=CASE WHEN user_id=successor THEN 'owner' ELSE 'admin' END WHERE workspace_id=w AND user_id IN(previous,successor);
 RETURN true;
END $$;

CREATE TABLE termlume.devices (
 user_id uuid NOT NULL REFERENCES termlume.app_users(id) ON DELETE CASCADE,id uuid NOT NULL,
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),platform text NOT NULL CHECK(platform IN ('windows','macos')),
 version text NOT NULL CHECK(length(version)<=40),active boolean NOT NULL DEFAULT true,
 first_seen timestamptz NOT NULL DEFAULT now(),last_seen timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(user_id,id)
);
CREATE TABLE termlume.device_grants (
 user_id uuid NOT NULL,device_id uuid NOT NULL,grant_id text NOT NULL CHECK(length(grant_id) BETWEEN 1 AND 255),
 revoked boolean NOT NULL DEFAULT false,PRIMARY KEY(user_id,grant_id),
 FOREIGN KEY(user_id,device_id) REFERENCES termlume.devices(user_id,id) ON DELETE CASCADE
);
CREATE TABLE termlume.audit_events (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,workspace_id uuid REFERENCES termlume.workspaces(id) ON DELETE SET NULL,
 actor_id uuid NOT NULL REFERENCES termlume.app_users(id) ON DELETE CASCADE,actor_name text NOT NULL,
 action text NOT NULL,entity_id text,summary text NOT NULL,created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX audit_workspace_time ON termlume.audit_events(workspace_id,id DESC);
CREATE TABLE termlume.workspace_history (
 workspace_id uuid NOT NULL REFERENCES termlume.workspaces(id) ON DELETE CASCADE,revision bigint NOT NULL,
 actor_name text NOT NULL,operation text NOT NULL,snapshot jsonb NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(workspace_id,revision)
);
CREATE TABLE termlume.applied_operations (
 workspace_id uuid NOT NULL REFERENCES termlume.workspaces(id) ON DELETE CASCADE,user_id uuid NOT NULL REFERENCES termlume.app_users(id) ON DELETE CASCADE,
 operation_id uuid NOT NULL,request_hash text NOT NULL,revision bigint NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(workspace_id,user_id,operation_id)
);
CREATE TABLE termlume.device_activity (
 user_id uuid NOT NULL,device_id uuid NOT NULL,entry_id uuid NOT NULL,workspace_id uuid NOT NULL REFERENCES termlume.workspaces(id) ON DELETE CASCADE,
 event_time timestamptz NOT NULL,entry jsonb NOT NULL,received_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(user_id,device_id,entry_id),
 FOREIGN KEY(user_id,device_id) REFERENCES termlume.devices(user_id,id) ON DELETE CASCADE
);
CREATE INDEX activity_workspace_time ON termlume.device_activity(user_id,workspace_id,event_time DESC);
CREATE TABLE termlume.trust_references (
 workspace_id uuid NOT NULL REFERENCES termlume.workspaces(id) ON DELETE CASCADE,endpoint text NOT NULL,
 entry jsonb NOT NULL,published_by text NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(workspace_id,endpoint)
);
ALTER TABLE termlume.devices ENABLE ROW LEVEL SECURITY;
ALTER TABLE termlume.device_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE termlume.audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE termlume.workspace_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE termlume.applied_operations ENABLE ROW LEVEL SECURITY;
ALTER TABLE termlume.device_activity ENABLE ROW LEVEL SECURITY;
ALTER TABLE termlume.trust_references ENABLE ROW LEVEL SECURITY;
CREATE POLICY devices_own ON termlume.devices FOR ALL USING(user_id=termlume.current_user_id()) WITH CHECK(user_id=termlume.current_user_id());
CREATE POLICY grants_own ON termlume.device_grants FOR ALL USING(user_id=termlume.current_user_id()) WITH CHECK(user_id=termlume.current_user_id());
CREATE POLICY audit_read ON termlume.audit_events FOR SELECT USING(actor_id=termlume.current_user_id() OR termlume.can_manage(workspace_id));
CREATE POLICY audit_append ON termlume.audit_events FOR INSERT WITH CHECK(actor_id=termlume.current_user_id() AND (workspace_id IS NULL OR termlume.can_read(workspace_id)));
CREATE POLICY history_read ON termlume.workspace_history FOR SELECT USING(termlume.can_read(workspace_id));
CREATE POLICY history_write ON termlume.workspace_history FOR ALL USING(termlume.can_write(workspace_id)) WITH CHECK(termlume.can_write(workspace_id));
CREATE POLICY operations_own ON termlume.applied_operations FOR ALL USING(user_id=termlume.current_user_id() AND termlume.can_read(workspace_id)) WITH CHECK(user_id=termlume.current_user_id() AND termlume.can_read(workspace_id));
CREATE POLICY activity_own ON termlume.device_activity FOR ALL USING(user_id=termlume.current_user_id() AND termlume.can_read(workspace_id)) WITH CHECK(user_id=termlume.current_user_id() AND termlume.can_read(workspace_id));
CREATE POLICY trust_read ON termlume.trust_references FOR SELECT USING(termlume.can_read(workspace_id));
CREATE POLICY trust_write ON termlume.trust_references FOR ALL USING(termlume.can_manage(workspace_id)) WITH CHECK(termlume.can_manage(workspace_id));
GRANT SELECT,INSERT,UPDATE,DELETE ON termlume.workspace_members,termlume.workspace_invites,termlume.devices,termlume.device_grants,
 termlume.workspace_history,termlume.applied_operations,termlume.device_activity,termlume.trust_references TO termlume_app;
GRANT SELECT,INSERT ON termlume.audit_events TO termlume_app;
GRANT USAGE ON SEQUENCE termlume.audit_events_id_seq TO termlume_app;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA termlume FROM PUBLIC;
GRANT EXECUTE ON FUNCTION termlume.current_user_id(),termlume.workspace_role(uuid),termlume.can_read(uuid),termlume.can_write(uuid),termlume.can_manage(uuid),
 termlume.bump_revision(uuid),termlume.redeem_invite(text),termlume.transfer_workspace(uuid,uuid) TO termlume_app;

-- 所有权和修订号只能通过核验权限的函数更新，管理员只能直接改名称。
REVOKE UPDATE ON termlume.workspaces FROM termlume_app;
GRANT UPDATE(name) ON termlume.workspaces TO termlume_app;
