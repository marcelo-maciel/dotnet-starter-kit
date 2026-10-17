# Module: PAF

PAF V2 queue management: the totem emits a ficha, the guiche calls it, the panel shows it. Module `Order = 900`, schema `paf`, `PafDbContext`.

## Rules

- **Domain language is Portuguese without accents** in identifiers (`Ficha`, `Fila`, `Guiche`, `Atendimento`, `Unidade`). Infrastructure (DbContext, initializer, module, endpoints plumbing) stays in English. See ADR 0004.
- **`Unidade` is a PAF entity**, not a kit concept. Every operational table carries `UnidadeId`. See ADR 0002.
- **Unit scope is a NAMED global query filter**, registered in `PafDbContext` with EF Core 10 `HasQueryFilter(name, expression)` directly: the kit's `AppendGlobalQueryFilter` (`src/BuildingBlocks/Persistence/ModelBuilderExtensions.cs`) is `internal` and not reachable from this module. It composes by AND with the Finbuckle tenant filter (also named) and the soft-delete filter. See ADR 0003.
- **Never call bare `IgnoreQueryFilters()`** in this module, and never set `IgnoreQueryFilters` on a specification (the kit's evaluator translates it to the bare form). Cross-unit aggregation disables only the unit filter by name: `IgnoreQueryFilters([PafQueryFilters.Unidade])`.
- **Current unit**: scoped `IUnidadeCorrente` (`Escopo/`), filled by whoever resolves the credential via `Definir`. The filter applies to every entity implementing `IEscopoUnidade` (marker-driven, not a list). Empty current unit = the filter returns NO rows (fail-closed). `Parametro` has its own filter: empresa rows (`UnidadeId` NULL) always visible. No unit filter on `Unidade`, `UsuarioUnidade`, `SessaoEstacao`, `AtividadeSessao` (tenant only).
- **No soft delete** on PAF entities: a soft-deleted `Chamado` would still hold the partial unique index (arquitetura 4.1).
- **Instants come from PostgreSQL** (`now()` defaults); never assign them from C#. `DataOperacional` (Ficha, Atendimento) and `DataElegibilidade` are STORED generated columns in America/Sao_Paulo (`Data/PafSql.cs`), so rewriting `ElegivelDesde` rewrites `DataElegibilidade` in the same write. Every state change of `Atendimento` increments `Versao`.

## Constraints (first migration, `InitialPaf`)

Finbuckle appends `TenantId` to every unique index; names are explicit.

| Name | Table | Columns / predicate |
|---|---|---|
| `UX_Fichas_Numeracao` | Fichas | UnidadeId, FilaId, PrioridadeId, DataOperacional, Numero, TenantId |
| `UX_Fichas_ChaveIdempotencia` | Fichas | EstacaoId, ChaveIdempotencia, TenantId |
| `UX_Atendimentos_FichaEmCurso` | Atendimentos | FichaId, TenantId WHERE Estado IN ('Chamado','EmAtendimento') |
| `UX_Atendimentos_EstacaoEmCurso` | Atendimentos | EstacaoId, TenantId WHERE same states |
| `UX_FilaPrioridades_Letra` | FilaPrioridades | UnidadeId, Letra, TenantId |
| `UX_SessoesEstacao_EstacaoAtiva` / `_SessaoAtiva` / `_ConcessaoAtiva` | SessoesEstacao | EstacaoId / SessaoId / ConcessaoId, TenantId WHERE LiberadoEm IS NULL |
| `CK_SessoesEstacao_DonoUnico` | SessoesEstacao | exactly one of SessaoId, ConcessaoId |

Concurrency and fail-closed tests: `src/Tests/Integration.Tests/Tests/PAF/`.
- `PafDbContext` follows the kit pattern: `base.OnModelCreating` runs LAST, so tenant isolation stays default-ON.
- Migrations live in `src/Host/FSH.Starter.Migrations.PostgreSQL/PAF/`, not in this module. Critical rules are enforced in PostgreSQL and carried by those migrations. See ADR 0005.

## Pending registration

`typeof(FSH.Modules.PAF.Contracts.PafContractsMarker)` is NOT yet in the Mediator `o.Assemblies` list of either host: Mediator's generator fails with MSG0007 on an assembly with no messages. The change that adds the first command or query to `Modules.PAF.Contracts` must add it, right before `typeof(FSH.Modules.PAF.PafModule)`, in both `src/Host/FSH.Starter.Api/Program.cs` and `src/Host/FSH.Starter.DbMigrator/Program.cs`.

## Touching the kit

The kit is touched only at module registration points (both host `Program.cs` and `.csproj`, `src/FSH.Starter.slnx`, the migrations project) and at the other items of the closed list in ADR 0008. Any other kit edit requires amending ADR 0008 first.
