# Module: PAF

PAF V2 queue management: the totem emits a ficha, the guiche calls it, the panel shows it. Module `Order = 900`, schema `paf`, `PafDbContext`.

## Rules

- **Domain language is Portuguese without accents** in identifiers (`Ficha`, `Fila`, `Guiche`, `Atendimento`, `Unidade`). Infrastructure (DbContext, initializer, module, endpoints plumbing) stays in English. See ADR 0004.
- **`Unidade` is a PAF entity**, not a kit concept. Every operational table carries `UnidadeId`. See ADR 0002.
- **Unit scope is a NAMED global query filter**, registered in `PafDbContext` with EF Core 10 `HasQueryFilter(name, expression)` directly: the kit's `AppendGlobalQueryFilter` (`src/BuildingBlocks/Persistence/ModelBuilderExtensions.cs`) is `internal` and not reachable from this module. It composes by AND with the Finbuckle tenant filter (also named) and the soft-delete filter. See ADR 0003.
- **Never call bare `IgnoreQueryFilters()`** in this module, and never set `IgnoreQueryFilters` on a specification (the kit's evaluator translates it to the bare form). Cross-unit aggregation disables only the unit filter by name: `IgnoreQueryFilters([<unit filter name>])`.
- `PafDbContext` follows the kit pattern: `base.OnModelCreating` runs LAST, so tenant isolation stays default-ON.
- Migrations live in `src/Host/FSH.Starter.Migrations.PostgreSQL/PAF/`, not in this module. Critical rules are enforced in PostgreSQL and carried by those migrations. See ADR 0005.

## Pending registration

`typeof(FSH.Modules.PAF.Contracts.PafContractsMarker)` is NOT yet in the Mediator `o.Assemblies` list of either host: Mediator's generator fails with MSG0007 on an assembly with no messages. The change that adds the first command or query to `Modules.PAF.Contracts` must add it, right before `typeof(FSH.Modules.PAF.PafModule)`, in both `src/Host/FSH.Starter.Api/Program.cs` and `src/Host/FSH.Starter.DbMigrator/Program.cs`.

## Touching the kit

The kit is touched only at module registration points (both host `Program.cs` and `.csproj`, `src/FSH.Starter.slnx`, the migrations project) and at the other items of the closed list in ADR 0008. Any other kit edit requires amending ADR 0008 first.
