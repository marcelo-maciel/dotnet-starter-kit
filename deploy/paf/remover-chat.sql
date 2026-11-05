-- One-off cleanup after the Chat module was removed (Marcelo, 04/11/2026; ADR 0009 do PAF, in PAFV2/Analise/adr,
-- outside this repo). Run by hand, once per environment, only AFTER the release without Chat is live: the previous
-- release's AppHub queries chat."ChannelMembers" on every connection and its chat routes query the schema, so
-- dropping it under that release breaks realtime for every user. /health/ready would NOT show it: db:chat only
-- tests the connection.
--
-- One transaction on purpose: schema, migration rows and permission claims go together. If only the schema went,
-- a rollback to the previous release would find the 7 rows applied and never recreate the tables; with the rows
-- gone too, the previous release's migrator recreates an empty chat schema and the rollback works. After such a
-- rollback, the previous release re-adds the Permissions.Chat.* claims; if the new release goes live again, run
-- this file again.
--
-- It cleans only the shared database fsh. A tenant with its own database (none in homologação or produção on
-- 05/11/2026) would need it run there too.
--
-- Usage on the host: docker exec -i paf-<ambiente>-postgres-1 sh -c 'psql -U "$POSTGRES_USER" -d fsh -v ON_ERROR_STOP=1' < remover-chat.sql
BEGIN;

DROP SCHEMA IF EXISTS chat CASCADE;

-- Exact ids, never LIKE: __EFMigrationsHistory is shared by every module.
DELETE FROM public."__EFMigrationsHistory"
WHERE "MigrationId" IN (
  '20260512193158_InitialChat',
  '20260512205427_AddMessageMentions',
  '20260512210921_AddMessageReactions',
  '20260512211141_AddMessagesFullTextSearch',
  '20260513115215_AddMessagePinning',
  '20260517105429_ChatTenantIsolation',
  '20260612201021_DropChannelMemberIsMuted'
);

-- RolePermissionSyncer leaves claims of permissions no longer registered in place, so they are removed here.
DELETE FROM identity."RoleClaims" WHERE "ClaimType" = 'permission' AND "ClaimValue" LIKE 'Permissions.Chat.%';

COMMIT;

-- After COMMIT on purpose: it reads the committed state, so 0|0|0 proves the cleanup landed.
SELECT
  (SELECT count(*) FROM pg_namespace WHERE nspname = 'chat') AS schema_chat,
  (SELECT count(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId" IN (
    '20260512193158_InitialChat', '20260512205427_AddMessageMentions', '20260512210921_AddMessageReactions',
    '20260512211141_AddMessagesFullTextSearch', '20260513115215_AddMessagePinning',
    '20260517105429_ChatTenantIsolation', '20260612201021_DropChannelMemberIsMuted')) AS historico_chat,
  (SELECT count(*) FROM identity."RoleClaims"
    WHERE "ClaimType" = 'permission' AND "ClaimValue" LIKE 'Permissions.Chat.%') AS permissoes_chat;
