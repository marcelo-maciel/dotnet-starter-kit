-- One-off cleanup after the Catalog and Tickets (Helpdesk) modules were removed (Marcelo, 04/11/2026; ADR 0009 do PAF
-- (PAFV2/Analise/adr, fora deste repo)). Run by hand, once per environment, only AFTER the release without them is
-- live: the previous release's catalog and tickets routes and module code still query these schemas, so dropping
-- them under that release breaks those routes and the moves of product images (its Catalog handler of
-- FileStorageKeyChangedIntegrationEvent reads catalog."ProductImages" for OwnerType "Product" only; avatars are not
-- affected). /health/ready would NOT show it: the db:catalog and db:tickets health checks only test the connection.
--
-- One transaction on purpose: schemas, migration rows and permission claims go together. If only the schemas went,
-- a rollback to the previous release would find the 13 rows applied and never recreate the tables; with the rows
-- gone too, the previous release's migrator recreates empty catalog and tickets schemas and the rollback works.
-- After such a rollback, the previous release re-adds the Permissions.Catalog.* and Permissions.Tickets.* claims;
-- if the new release goes live again, run this file again.
--
-- It cleans only the shared database fsh. A tenant with its own database (none in homologação or produção on
-- 05/11/2026) would need it run there too.
--
-- Usage on the host: docker exec -i paf-<ambiente>-postgres-1 sh -c 'psql -U "$POSTGRES_USER" -d fsh -v ON_ERROR_STOP=1' < remover-catalogo-helpdesk.sql
BEGIN;

DROP SCHEMA IF EXISTS catalog CASCADE;
DROP SCHEMA IF EXISTS tickets CASCADE;

-- Exact ids, never LIKE: __EFMigrationsHistory is shared by every module.
DELETE FROM public."__EFMigrationsHistory"
WHERE "MigrationId" IN (
  '20260430033202_InitialCatalog',
  '20260430033849_AddCategories',
  '20260430034535_AddProducts',
  '20260430103838_AddSoftDelete',
  '20260512142004_AddProductImages',
  '20260512144952_ProductImageValueGenerationNever',
  '20260512162229_DropProductImagesThumbnailUniqueIndex',
  '20260517105359_CatalogTenantIsolation',
  '20260626195522_CatalogMoneyValueObject',
  '20260930050256_ProductImageFileAssetIdIndex',
  '20260430102356_InitialTickets',
  '20260430103849_AddSoftDelete',
  '20260517105416_TicketsTenantIsolation'
);

-- RolePermissionSyncer leaves claims of permissions no longer registered in place, so they are removed here.
DELETE FROM identity."RoleClaims"
WHERE "ClaimType" = 'permission'
  AND ("ClaimValue" LIKE 'Permissions.Catalog.%' OR "ClaimValue" LIKE 'Permissions.Tickets.%');

COMMIT;

-- After COMMIT on purpose: it reads the committed state, so 0|0|0|0 proves the cleanup landed.
SELECT
  (SELECT count(*) FROM pg_namespace WHERE nspname = 'catalog') AS schema_catalog,
  (SELECT count(*) FROM pg_namespace WHERE nspname = 'tickets') AS schema_tickets,
  (SELECT count(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId" IN (
    '20260430033202_InitialCatalog', '20260430033849_AddCategories', '20260430034535_AddProducts',
    '20260430103838_AddSoftDelete', '20260512142004_AddProductImages', '20260512144952_ProductImageValueGenerationNever',
    '20260512162229_DropProductImagesThumbnailUniqueIndex', '20260517105359_CatalogTenantIsolation',
    '20260626195522_CatalogMoneyValueObject', '20260930050256_ProductImageFileAssetIdIndex',
    '20260430102356_InitialTickets', '20260430103849_AddSoftDelete', '20260517105416_TicketsTenantIsolation'))
    AS historico_catalogo_helpdesk,
  (SELECT count(*) FROM identity."RoleClaims"
    WHERE "ClaimType" = 'permission'
      AND ("ClaimValue" LIKE 'Permissions.Catalog.%' OR "ClaimValue" LIKE 'Permissions.Tickets.%'))
    AS permissoes_catalogo_helpdesk;
