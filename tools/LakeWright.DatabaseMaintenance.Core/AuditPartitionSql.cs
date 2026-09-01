namespace LakeWright.DatabaseMaintenance;

internal static class AuditPartitionSql
{
    internal const string InstallHelpers = """
        CREATE TABLE IF NOT EXISTS lakewright_audit_partition_state (
            "StateKey" boolean PRIMARY KEY DEFAULT true CHECK ("StateKey"),
            "SchemaVersion" integer NOT NULL,
            "Phase" text NOT NULL,
            "UpdatedAt" timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS audit_event_partitions (
            "PartitionName" text PRIMARY KEY,
            "StartsAt" timestamptz NOT NULL UNIQUE,
            "EndsAt" timestamptz NOT NULL UNIQUE,
            CONSTRAINT "CK_audit_event_partitions_bounds" CHECK ("StartsAt" < "EndsAt")
        );

        CREATE OR REPLACE FUNCTION lakewright_create_audit_partition(p_start timestamptz)
        RETURNS boolean
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $function$
        DECLARE
            partition_start timestamptz := date_trunc('month', p_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
            partition_end timestamptz :=
                (partition_start AT TIME ZONE 'UTC' + interval '1 month') AT TIME ZONE 'UTC';
            partition_name text := 'audit_events_' || to_char(partition_start AT TIME ZONE 'UTC', 'YYYY_MM');
            index_name text := partition_name || '_org_occurred';
            already_present boolean;
        BEGIN
            IF partition_start <> p_start THEN
                RAISE EXCEPTION 'partition start must be the first instant of a UTC month';
            END IF;

            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_inherits i
                JOIN pg_catalog.pg_class child ON child.oid = i.inhrelid
                JOIN pg_catalog.pg_class parent ON parent.oid = i.inhparent
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = parent.relnamespace
                WHERE namespace.nspname = 'public'
                  AND parent.relname = 'audit_events'
                  AND child.relname = partition_name
            ) INTO already_present;

            IF NOT already_present THEN
                EXECUTE format(
                    'CREATE TABLE public.%I PARTITION OF public.audit_events '
                    || 'FOR VALUES FROM (%L) TO (%L)',
                    partition_name,
                    partition_start,
                    partition_end);
            END IF;

            EXECUTE format(
                'CREATE INDEX IF NOT EXISTS %I ON public.%I ("OrganizationId", "OccurredAt")',
                index_name,
                partition_name);

            INSERT INTO public.audit_event_partitions
                ("PartitionName", "StartsAt", "EndsAt")
            VALUES (partition_name, partition_start, partition_end)
            ON CONFLICT ("PartitionName") DO UPDATE
            SET "StartsAt" = EXCLUDED."StartsAt", "EndsAt" = EXCLUDED."EndsAt";

            RETURN NOT already_present;
        END;
        $function$;

        CREATE OR REPLACE FUNCTION lakewright_drop_audit_partition(
            p_name text,
            p_start timestamptz,
            p_end timestamptz)
        RETURNS void
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $function$
        BEGIN
            IF p_name <> 'audit_events_' || to_char(p_start AT TIME ZONE 'UTC', 'YYYY_MM')
               OR p_start <> date_trunc('month', p_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
               OR p_end <> (p_start AT TIME ZONE 'UTC' + interval '1 month') AT TIME ZONE 'UTC' THEN
                RAISE EXCEPTION 'refusing non-canonical audit partition %', p_name;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_inherits i
                JOIN pg_catalog.pg_class child ON child.oid = i.inhrelid
                JOIN pg_catalog.pg_class parent ON parent.oid = i.inhparent
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = parent.relnamespace
                WHERE namespace.nspname = 'public'
                  AND parent.relname = 'audit_events'
                  AND child.relname = p_name
            ) THEN
                RAISE EXCEPTION '% is not a partition of public.audit_events', p_name;
            END IF;

            DELETE FROM public.audit_event_ids
            WHERE "OccurredAt" >= p_start AND "OccurredAt" < p_end;
            EXECUTE format('DROP TABLE public.%I', p_name);
            DELETE FROM public.audit_event_partitions WHERE "PartitionName" = p_name;
        END;
        $function$;

        REVOKE ALL ON FUNCTION lakewright_create_audit_partition(timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION lakewright_drop_audit_partition(text, timestamptz, timestamptz) FROM PUBLIC;
        """;

    internal const string CreateParent = """
        CREATE TABLE audit_events (
            "Id" uuid NOT NULL,
            "OrganizationId" uuid NULL,
            "PrincipalId" varchar(200) NOT NULL,
            "Action" varchar(100) NOT NULL,
            "ResourceType" varchar(100) NOT NULL,
            "ResourceId" varchar(200) NULL,
            "OccurredAt" timestamptz NOT NULL,
            "Detail" jsonb NULL
        ) PARTITION BY RANGE ("OccurredAt");

        CREATE TABLE audit_event_ids (
            "Id" uuid PRIMARY KEY,
            "OccurredAt" timestamptz NOT NULL
        );
        CREATE INDEX audit_event_ids_occurred_at ON audit_event_ids ("OccurredAt");
        """;

    internal const string CopyRows = """
        INSERT INTO audit_events
            ("Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail")
        SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
        FROM audit_events_unpartitioned_backup;

        INSERT INTO audit_event_ids ("Id", "OccurredAt")
        SELECT "Id", "OccurredAt" FROM audit_events_unpartitioned_backup;
        """;

    internal const string InstallIdentityTrigger = """
        CREATE OR REPLACE FUNCTION lakewright_register_audit_event_id()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        BEGIN
            INSERT INTO public.audit_event_ids ("Id", "OccurredAt")
            VALUES (NEW."Id", NEW."OccurredAt");
            RETURN NEW;
        END;
        $function$;

        REVOKE ALL ON FUNCTION lakewright_register_audit_event_id() FROM PUBLIC;
        DROP TRIGGER IF EXISTS lakewright_register_audit_event_id ON audit_events;
        CREATE TRIGGER lakewright_register_audit_event_id
            BEFORE INSERT ON audit_events
            FOR EACH ROW EXECUTE FUNCTION lakewright_register_audit_event_id();
        """;

    internal const string CopySecurity = """
        DO $block$
        DECLARE
            source_table regclass := 'public.audit_events_unpartitioned_backup'::regclass;
            grant_row record;
            policy_row record;
            command_name text;
            role_list text;
        BEGIN
            FOR grant_row IN
                SELECT grantee, privilege_type, is_grantable
                FROM information_schema.role_table_grants
                WHERE table_schema = 'public'
                  AND table_name = 'audit_events_unpartitioned_backup'
                  AND grantee <> current_user
            LOOP
                IF grant_row.privilege_type NOT IN
                    ('SELECT', 'INSERT', 'REFERENCES', 'TRIGGER') THEN
                    RAISE EXCEPTION 'unsupported audit_events privilege %', grant_row.privilege_type;
                END IF;

                EXECUTE format(
                    'GRANT %s ON TABLE public.audit_events TO %s%s',
                    grant_row.privilege_type,
                    CASE WHEN grant_row.grantee = 'PUBLIC' THEN 'PUBLIC'
                         ELSE format('%I', grant_row.grantee) END,
                    CASE WHEN grant_row.is_grantable = 'YES' THEN ' WITH GRANT OPTION' ELSE '' END);
                EXECUTE format(
                    'REVOKE ALL ON TABLE public.audit_events_unpartitioned_backup FROM %s',
                    CASE WHEN grant_row.grantee = 'PUBLIC' THEN 'PUBLIC'
                         ELSE format('%I', grant_row.grantee) END);
            END LOOP;

            FOR policy_row IN
                SELECT p.polname,
                       p.polpermissive,
                       p.polcmd,
                       pg_catalog.pg_get_expr(p.polqual, p.polrelid) AS using_expression,
                       pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid) AS check_expression,
                       ARRAY(
                           SELECT CASE WHEN role_oid = 0 THEN 'PUBLIC'
                                       ELSE format('%I', role.rolname) END
                           FROM unnest(p.polroles) role_oid
                           LEFT JOIN pg_catalog.pg_roles role ON role.oid = role_oid
                       ) AS roles
                FROM pg_catalog.pg_policy p
                WHERE p.polrelid = source_table
            LOOP
                command_name := CASE policy_row.polcmd
                    WHEN 'r' THEN 'SELECT'
                    WHEN 'a' THEN 'INSERT'
                    WHEN 'w' THEN 'UPDATE'
                    WHEN 'd' THEN 'DELETE'
                    WHEN '*' THEN 'ALL'
                    ELSE NULL
                END;

                IF command_name IS NULL THEN
                    RAISE EXCEPTION 'unsupported row-security command %', policy_row.polcmd;
                END IF;

                SELECT string_agg(role_name, ', ') INTO role_list
                FROM unnest(policy_row.roles) role_name;

                EXECUTE format(
                    'CREATE POLICY %I ON public.audit_events AS %s FOR %s TO %s%s%s',
                    policy_row.polname,
                    CASE WHEN policy_row.polpermissive THEN 'PERMISSIVE' ELSE 'RESTRICTIVE' END,
                    command_name,
                    role_list,
                    CASE WHEN policy_row.using_expression IS NULL THEN ''
                         ELSE ' USING (' || policy_row.using_expression || ')' END,
                    CASE WHEN policy_row.check_expression IS NULL THEN ''
                         ELSE ' WITH CHECK (' || policy_row.check_expression || ')' END);
            END LOOP;

        END;
        $block$;
        """;

    internal const string RestoreSecurityAfterRollback = """
        DO $block$
        DECLARE
            grant_row record;
            target_role text;
        BEGIN
            FOR grant_row IN
                SELECT grantee, privilege_type, is_grantable
                FROM information_schema.role_table_grants
                WHERE table_schema = 'public'
                  AND table_name = 'audit_events_partitioned_rollback'
                  AND grantee <> current_user
            LOOP
                IF grant_row.privilege_type NOT IN
                    ('SELECT', 'INSERT', 'REFERENCES', 'TRIGGER') THEN
                    RAISE EXCEPTION 'unsafe audit_events privilege % appeared after migration',
                        grant_row.privilege_type;
                END IF;
                target_role := CASE WHEN grant_row.grantee = 'PUBLIC' THEN 'PUBLIC'
                                    ELSE format('%I', grant_row.grantee) END;
                EXECUTE format(
                    'GRANT %s ON TABLE public.audit_events TO %s%s',
                    grant_row.privilege_type,
                    target_role,
                    CASE WHEN grant_row.is_grantable = 'YES' THEN ' WITH GRANT OPTION' ELSE '' END);
                EXECUTE format(
                    'REVOKE ALL ON TABLE public.audit_events_partitioned_rollback FROM %s',
                    target_role);
            END LOOP;
        END;
        $block$;
        """;
}
