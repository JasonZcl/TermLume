CREATE SCHEMA IF NOT EXISTS termlume;

CREATE TABLE termlume.app_users (
  id uuid PRIMARY KEY,
  issuer text NOT NULL,
  subject text NOT NULL CHECK (length(subject) BETWEEN 1 AND 255),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (issuer, subject)
);
CREATE TABLE termlume.workspaces (
  id uuid PRIMARY KEY,
  owner_id uuid NOT NULL UNIQUE REFERENCES termlume.app_users(id) ON DELETE CASCADE,
  revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE termlume.host_groups (
  id uuid NOT NULL,
  workspace_id uuid NOT NULL REFERENCES termlume.workspaces(id) ON DELETE CASCADE,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
  sort_order integer NOT NULL,
  PRIMARY KEY (workspace_id, id),
  UNIQUE (workspace_id, name)
);
CREATE TABLE termlume.hosts (
  id uuid NOT NULL,
  workspace_id uuid NOT NULL,
  group_id uuid NOT NULL,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
  address text NOT NULL CHECK (length(address) BETWEEN 1 AND 255),
  port integer NOT NULL CHECK (port BETWEEN 1 AND 65535),
  username text NOT NULL CHECK (length(username) BETWEEN 1 AND 100),
  auth_type text NOT NULL CHECK (auth_type IN ('password', 'key')),
  color text NOT NULL,
  note text NOT NULL CHECK (length(note) <= 2000),
  sort_order integer NOT NULL,
  PRIMARY KEY (workspace_id, id),
  FOREIGN KEY (workspace_id, group_id) REFERENCES termlume.host_groups(workspace_id, id)
);
CREATE TABLE termlume.user_host_state (
  user_id uuid NOT NULL REFERENCES termlume.app_users(id) ON DELETE CASCADE,
  workspace_id uuid NOT NULL,
  host_id uuid NOT NULL,
  favorite boolean NOT NULL DEFAULT false,
  last_connected_at timestamptz,
  PRIMARY KEY (user_id, workspace_id, host_id),
  FOREIGN KEY (workspace_id, host_id) REFERENCES termlume.hosts(workspace_id, id) ON DELETE CASCADE
);
CREATE TABLE termlume.snippets (
  id uuid NOT NULL,
  workspace_id uuid NOT NULL REFERENCES termlume.workspaces(id) ON DELETE CASCADE,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
  command text NOT NULL CHECK (length(command) BETWEEN 1 AND 20000),
  sort_order integer NOT NULL,
  PRIMARY KEY (workspace_id, id)
);
CREATE TABLE termlume.user_preferences (
  user_id uuid NOT NULL REFERENCES termlume.app_users(id) ON DELETE CASCADE,
  platform text NOT NULL CHECK (platform IN ('common', 'windows', 'macos')),
  settings jsonb NOT NULL,
  PRIMARY KEY (user_id, platform)
);

ALTER TABLE termlume.app_users ENABLE ROW LEVEL SECURITY;
CREATE POLICY own_user ON termlume.app_users
 USING (issuer = current_setting('termlume.issuer', true) AND subject = current_setting('termlume.subject', true))
 WITH CHECK (issuer = current_setting('termlume.issuer', true) AND subject = current_setting('termlume.subject', true));
ALTER TABLE termlume.workspaces ENABLE ROW LEVEL SECURITY;
CREATE POLICY own_workspace ON termlume.workspaces
 USING (owner_id IN (SELECT id FROM termlume.app_users))
 WITH CHECK (owner_id IN (SELECT id FROM termlume.app_users));
ALTER TABLE termlume.host_groups ENABLE ROW LEVEL SECURITY;
CREATE POLICY own_groups ON termlume.host_groups
 USING (workspace_id IN (SELECT id FROM termlume.workspaces))
 WITH CHECK (workspace_id IN (SELECT id FROM termlume.workspaces));
ALTER TABLE termlume.hosts ENABLE ROW LEVEL SECURITY;
CREATE POLICY own_hosts ON termlume.hosts
 USING (workspace_id IN (SELECT id FROM termlume.workspaces))
 WITH CHECK (workspace_id IN (SELECT id FROM termlume.workspaces));
ALTER TABLE termlume.user_host_state ENABLE ROW LEVEL SECURITY;
CREATE POLICY own_host_state ON termlume.user_host_state
 USING (user_id IN (SELECT id FROM termlume.app_users) AND workspace_id IN (SELECT id FROM termlume.workspaces))
 WITH CHECK (user_id IN (SELECT id FROM termlume.app_users) AND workspace_id IN (SELECT id FROM termlume.workspaces));
ALTER TABLE termlume.snippets ENABLE ROW LEVEL SECURITY;
CREATE POLICY own_snippets ON termlume.snippets
 USING (workspace_id IN (SELECT id FROM termlume.workspaces))
 WITH CHECK (workspace_id IN (SELECT id FROM termlume.workspaces));
ALTER TABLE termlume.user_preferences ENABLE ROW LEVEL SECURITY;
CREATE POLICY own_preferences ON termlume.user_preferences
 USING (user_id IN (SELECT id FROM termlume.app_users))
 WITH CHECK (user_id IN (SELECT id FROM termlume.app_users));

GRANT USAGE ON SCHEMA termlume TO termlume_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA termlume TO termlume_app;
