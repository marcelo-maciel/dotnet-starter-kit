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

## Registration

`PafContractsMarker` and `PafModule` are both in the Mediator `o.Assemblies` list of both hosts. Permissions (`PafPermissions`, `Permissions.PAF.{Recurso}.{Acao}`) are registered in `PafModule`; `Contratos.Manage` is root-only.

- Slice 2 permission catalog (P01): every endpoint of slice 2 authorizes with a `PafPermissions` constant; only wave 0 (and D01) edits that file. Basic: `Guiches.Selecionar`, `Atendimentos.Operar`, `Fichas.Emitir`, `Atendimentos.Encaminhar`, `Atendimentos.SelecionarFicha`. Root: `Contratos.Manage`, `Unidades.Operar`, and `Estatisticas.ViewPorAtendente` (root only so the kit `RolePermissionSyncer` does not hand it to every client Admin before D10). `PAF.Tests/PafPermissionsTests` pins constants = `All` and both sets.

## Unit scope in requests

- `IUnidadeCorrente` sources, in middleware order: `Conferencia/ConferenciaRequisicaoMiddleware` (station credential, claim `PafClaims.Unidade` = `paf_unidade`, checked against `VinculoEstacao`), `Sessao/SessionCheckMiddleware` (person with a PAF session), then `Escopo/UnidadeCorrenteMiddleware`: header `X-Paf-Unidade` only when neither set the unit (root tenant, root operator in a client tenant), 403 outside the person's `UsuarioUnidade`; a station (recognised by `PafClaims.Papel`, like the other middlewares) sending another unit in the header gets 403. Ponytail: the header goes away when the session covers root.
- Unit-scoped writes take the unidade from `IUnidadeCorrente.Exigir()`, never from the body.
- Explicit cross-unit paths (only `IgnoreQueryFilters([PafQueryFilters.Unidade])`): `ListFilasConsolidadas` (caller's scope) and `UpdateContratoUnidade` (Codefi operator). `Architecture.Tests/PafQueryFilterTests` scans the PAF IL and fails on the bare form or on a specification overriding `IgnoreQueryFilters`.
- Demo data: `Data/PafDemoSeeder`, called by the DbMigrator `seed-demo` verb only, never by tenant provisioning.

## Station credential (`Estacoes/`, `Conferencia/`, `Autorizacao/`)

- Claims in `Contracts/Authorization/PafClaims` (`paf_papel`, `paf_estacao`, `paf_unidade`, `paf_vinculo`, plus the kit's `tenant`, `sub`, `jti`); role values in `PafPapeisEstacao` (`Emissao`, `Painel`). Single definition: never redeclare claim names elsewhere.
- `ConferenciaRequisicaoMiddleware` runs for every authenticated request of the host: 403 when the `tenant` header or `?tenant=` differs from the token's tenant claim (root operators exempt, stations never); for a station token it checks the live `VinculoEstacao` and the active `Estacao`, sets the unit, and marks `HttpContext.Items["paf:estacao-conferida"]`, which `EstacaoAuthorizationHandler` reads for policies `PafEstacaoEmissao` / `PafEstacaoPainel`.

## Realtime (`TempoReal/`)

- `PafHub` at `/api/v1/paf/hub` (`PafHubRota.Caminho`), policy `PafHub`, groups `PafGrupos` (`paf:{tenant}:unidade:{guid}` and siblings). Writes go through `AvisosTempoReal.GravarAsync` inside the transaction and `EnviarAsync` after commit; the outbox stores the full type name of the `*Paf` events, so renaming one orphans stored rows.
- `PafHubFilter` revalidates on connect and on every client call (live `VinculoEstacao` through `Conferencia/CredencialEstacao`, active kit session through `ISessionService`), aborts on refusal, and installs the token's tenant in the hub scope. Registered with `AddSignalR().AddHubOptions<PafHub>`: a bare `Configure<HubOptions<PafHub>>` is silently ignored (SignalR uses per-hub options only when `HubOptionsSetup<PafHub>` marked them set). Test clients make one `EntrarNoGuiche` round trip after `StartAsync` (`PafOperacao.ProntaAsync`), because the handshake returns before the groups are joined: resolve `PafDbContext` lazily inside the hub, never by constructor.
- `ConexoesPaf` is the connection registry (in-memory, ponytail: one instance). A person connection joins the guichê group of its session's active `SessaoEstacao` on connect; the guichê screen invokes `EntrarNoGuiche` (`PafHubChamadas`) after selecting and after every reconnect. Hooks, called after the write is committed: `EncerrarSessao` from `RevogacaoSessao`, `EndSessao` (logout), the login's single session (previous sessions) and the refresh refused for licence; `EncerrarEstacao` from `RevogarVinculo`, `GerarVinculo` (device replaced) and `DisableEstacao`. `MoverSessaoParaGuicheAsync` from `SetGuiche` (the new guichê, and null for a dead occupant it released).
- Every front client connects WebSockets-only with `skipNegotiation: true` (seção 11, 17/10/2026) and invokes nothing while not Connected. The .NET test clients stay on long polling (TestServer closes a WebSocket before the handshake).

## Session and licence (`Sessao/`)

- The access token carries the kit session id in claim `sid` (edit in the Identity token handlers, ADR 0008). The JwtBearer inbound map keeps it as `sid`.
- `GenerateTokenLicenseBehavior` / `RefreshTokenLicenseBehavior` are closed Mediator behaviors (confirmed applied by `PafSessaoLicencaTests`). Root tenant and users with no `UsuarioUnidade` pass straight through: no licence, no single session.
- First slice: no cascade, the principal unit (lowest `Ordem`) pays; the licence does not migrate on unit switch. The seat count is always `AtividadeSessao` rows fresher than 10 min, under `pg_advisory_xact_lock` of the unit.
- `SessionCheckMiddleware` (module middleware) checks `sid` → active `UserSession` → fresh `AtividadeSessao`, fills `SessaoCorrente` and `IUnidadeCorrente` (Valkey state via `EstadoSessao`, else the licence unit), and renews the mark at most once a minute. The current unit must be in the user's `UsuarioUnidade`, else the session is revoked (RF08); a disabled unit is NOT checked (open sessions run until they end). Each renewal also checks the user is active (`IUserProfileService`), revoking otherwise. Root tokens get the `UserSession` check too (read in a root-tenant scope when the request targets a client tenant), never the licence. Tokens with `act_sub` or `paf_papel` (`PafClaims.Papel`, same test as `ConferenciaRequisicaoMiddleware`) are not handled here.
- PAF-born revocation goes through `RevogacaoSessao` (release licence and guichê, drop the Valkey state, revoke the `UserSession`). `SetUsuarioUnidades` uses it for every live session whose current unit was taken out. `CreateUnidade` does NOT add the creator to the new unit's scope.
- Scope grants (`UsuariosUnidade` GET/PUT): the holder of `Unidades.Create` (tenant admin, `EscopoUsuario.AdministraTenantAsync`) reads and edits any user and grants any ACTIVE unidade of the tenant (else 400); everyone else keeps the RF08 subset rule (grant only inside his scope, touch only users whose scope fits in it). Ponytail: slice 2 replaces the criterion with the RF08 "escopo empresa".
- Routes under `api/v1/paf/sessao`: `unidade-corrente`, `guiche` (`Permissions.PAF.Guiches.Selecionar`), `logout`.

## Operational flow (`Features/v1/Fichas`, `Features/v1/Atendimentos`, `Features/v1/Painel`)

- Emission: `POST api/v1/paf/estacao/emissao/fichas` (policy `PafEstacaoEmissao`, header `Idempotency-Key` required, kit `WithIdempotency` in front). Number = day max + 1 (or `NumeroInicial`), day and window from the PostgreSQL clock; `UX_Fichas_Numeracao` retries in a new transaction (max 10, logged); `UX_Fichas_ChaveIdempotencia` returns the stored ficha.
- Guichê (`Permissions.PAF.Atendimentos.Operar`, basic; guichê from the session's `SessaoEstacao`): `POST api/v1/paf/guiche/chamar-proximo`, `POST api/v1/paf/guiche/atendimentos/{id}/{rechamar|iniciar|encerrar|cancelar}`. Call = candidate `FOR UPDATE OF a SKIP LOCKED`, then all rows of the ficha `FOR UPDATE SKIP LOCKED` in id order (savepoint rollback on skip); other operations lock the ficha rows waiting, in id order, and re-validate. Every state UPDATE stamps `now()` and increments `Versao`. Option "respeitar ordem das filas" = key `respeitarOrdemFilas` in `Estacao.Configuracao` (ponytail).
- Guichê link lock: the call and every release of a session's guichê (switch, unit change) take `SELECT ... FOR UPDATE` on the session's `SessoesEstacao` row inside their transaction (`VinculosGuiche.TravarAtivoDaSessaoAsync`, `SoltarSemAtendimentoEmCursoAsync`), so no guichê is released with an atendimento `Chamado` on it. Both throw without an open transaction.
- Guichê screen reads (`Permissions.PAF.Guiches.Selecionar`, basic, no cadastro permission): `GET api/v1/paf/guiche/unidades` (only `Id` and `Nome` of the ACTIVE unidades of the caller's scope), `guiches` (active guichês of the current unit, with the occupant's user name and the senha of another user's pendência), `posto` (current unit, selected guichê, the caller's own atendimento in progress, never another user's).
- Pendência on selection (seção 5, RF04): an atendimento `Chamado`/`EmAtendimento` left on the guichê by a released link. Its own user resumes it by selecting the guichê again; another user gets 409 unless `SetGuicheCommand.LiberarPendencia`, which cancels it (RF12, `Chamado` or `EmAtendimento`) in the selection's transaction, under the ficha lock, with `AtendimentoAtualizadoPaf` after commit.
- Station configuration (`GET api/v1/paf/estacao/{emissao|painel}/configuracao`) carries `Botoes`: filas of the emission role × prioridades, fila and prioridade active, ordered by station then fila order; empty for the painel.
- Painel state: `GET api/v1/paf/estacao/painel/estado` (policy `PafEstacaoPainel`): today's calls of the painel's filas, latest call or recall first, with `Versao`.
- Notices: emission → `FilaAtualizadaPaf`; call → `SenhaChamadaPaf` (guichê + active painéis listing the fila) and `FilaAtualizadaPaf`; recall → `SenhaRechamadaPaf`; start/close/cancel → `AtendimentoAtualizadoPaf`. Always `GravarAsync` before commit, `EnviarAsync` after.
- Raw SQL skips global filters: every raw statement carries `"TenantId"` (and `"UnidadeId"` where it selects).

## Touching the kit

The kit is touched only at module registration points (both host `Program.cs` and `.csproj`, `src/FSH.Starter.slnx`, the migrations project) and at the other items of the closed list in ADR 0008. Any other kit edit requires amending ADR 0008 first.

## Front (`clients/dashboard/src/features/paf`)

- Routes (only `routes.tsx` is touched, ADR 0008): `/paf/estacao/vinculo#tenant=<t>&token=<t>` (link redemption, token in the fragment, wiped after read), `/paf/totem`, `/paf/painel` (top-level, outside `ProtectedRoute` and the inactivity guard), `/paf/guiche` (inside `AppShell`, kit auth). No sidebar entry: `nav-data.ts` is not in ADR 0008.
- Totem and painel never use `lib/api-client.ts` nor `realtime/realtime-context.tsx`: `estacao/estacao-http.ts` and `estacao/hub-estacao.ts` read the station credential from `localStorage` keys `paf.estacao.emissao` / `paf.estacao.painel` (outside `fsh.dashboard.`), send `tenant` = the credential's tenant, renew through the secret on 401 or 60 s before expiry.
- Panel rules live in the pure reducer `painel/painel-estado.ts` (EventoId dedupe, Versao ordering, recall moves to top, cancel drops the pending announcement).
- The guichê page restores from `GET guiche/posto`, never from browser storage. The printed ficha has no QR until the public ficha page exists (RF11, requisitos 7.1).
- Real-API walk: `clients/dashboard/tests/e2e/paf-primeira-fatia.walk.mjs` (fresh `seed-demo` database; speech and print stubbed).
