using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Licença da sessão (RF22). Cascata pelo escopo efetivo (<see cref="EscopoUsuario"/>): a primeira unidade ativa,
/// com contrato vigente e com saldo, ou com ultrapassagem contratada, paga. A decisão é do conjunto de licenças no
/// Valkey (<see cref="ConjuntoLicencas"/>); <see cref="AtividadeSessao"/> continua a fonte da verdade e é gravada em
/// toda ocupação. Só com o Valkey fora, ou antes da reconstrução dos conjuntos, a contagem é a das linhas frescas de
/// <see cref="AtividadeSessao"/>, sob os bloqueios consultivos de todas as candidatas (seção 9, modo degradado).
/// </summary>
public sealed class Licencas(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    ConjuntoLicencas conjunto,
    EscopoUsuario escopo,
    ILogger<Licencas> logger)
{
    // ponytail: initial values of seção 9 as constants; move to options when measurement asks for others.
    public const int PrazoMarcaMinutos = 10;
    public const int IntervaloMarcaMinutos = 1;

    // Covers the token issue between reservation and confirmation; an abandoned reservation frees itself in this time.
    public static readonly TimeSpan PrazoReserva = TimeSpan.FromMinutes(1);

    // Above the Valkey call deadline, so the call gives up by its own timeout and logs, never by cancellation.
    private static readonly TimeSpan PrazoLimpeza = TimeSpan.FromSeconds(2);

    public const string MensagemSemUnidadeAtiva =
        "Nenhuma unidade do seu acesso está ativa. Procure o administrador da empresa.";
    public const string MensagemSemContrato =
        "Nenhuma unidade do seu acesso tem contrato ativo. Procure o administrador da empresa.";
    public const string MensagemLimite =
        "O limite de licenças foi atingido em todas as unidades do seu acesso. Aguarde outro usuário sair ou procure o administrador.";
    public const string MensagemDestinoSemContrato =
        "Esta unidade não tem contrato ativo. Você continua na unidade atual.";
    public const string MensagemDestinoLimite =
        "O limite de licenças desta unidade foi atingido. Você continua na unidade atual.";

    /// <summary>
    /// Whether the PAF has reached the tenant: its initial unidade exists (RF23). Until then no user of the tenant can
    /// have a unidade, the kit's login stands as it is, and refusing it would lock the initial admin out (plano L23).
    /// </summary>
    public Task<bool> PafProvisionadoAsync(CancellationToken ct) =>
        db.Unidades.AsNoTracking().AnyAsync(u => u.Inicial, ct);

    /// <summary>
    /// Abre a transação da ocupação e reserva a vaga na primeira candidata da cascata: no conjunto do Valkey por um
    /// membro provisório, que a confirmação troca pela sessão, ou, no modo degradado, contando sob os bloqueios das
    /// candidatas. Nulo quando o usuário não tem unidade no PAF: ele não opera o PAF, e o filtro de unidade continua
    /// fechado para ele.
    /// </summary>
    public async Task<ReservaLicenca?> ReservarAsync(string tenantId, Guid usuarioId, CancellationToken ct)
    {
        var escopoEfetivo = await escopo.EscopoEfetivoAsync(usuarioId, ct);
        if (escopoEfetivo.Count == 0)
        {
            return null;
        }

        var transacao = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Login and refresh of the same user serialize here: a refresh of a session that a login on another
            // machine is revoking must not confirm after that login and release the new session (sessão única).
            // The unit lock below did this alone before the Valkey set; the user lock keeps it with the set.
            await BloquearAsync($"paf-licenca-usuario:{tenantId}:{usuarioId}", ct);
            var candidatas = await CandidatasAsync(tenantId, escopoEfetivo, ct);

            var membro = ConjuntoLicencas.NovaReserva();
            var ocupacao = await conjunto.OcuparAsync(
                tenantId, membro, candidatas, await SessoesDoUsuarioAsync(tenantId, usuarioId, ct), [], PrazoReserva, ct);
            string? membroReserva = null;
            CandidataLicenca candidata;
            var ocupadasNoBanco = 0;
            switch (ocupacao.Desfecho)
            {
                case DesfechoOcupacao.Ocupada:
                    candidata = candidatas.First(c => c.UnidadeId == ocupacao.UnidadeId);
                    membroReserva = membro;
                    break;
                case DesfechoOcupacao.SemSaldo:
                    throw new ForbiddenException(MensagemLimite);
                default:
                    // S14: a stalled Valkey would still run this occupation when it returns and hold a ghost
                    // reservation for PrazoReserva; the release queued behind it undoes it.
                    conjunto.SoltarOcupacaoSemResposta(tenantId, membro);
                    (candidata, ocupadasNoBanco) = await EscolherNoBancoAsync(tenantId, usuarioId, candidatas, ct);
                    break;
            }

            unidadeCorrente.Definir(candidata.UnidadeId);
            return new ReservaLicenca(
                this, transacao, tenantId, usuarioId, candidata, membroReserva, ocupacao.Desfecho, ocupadasNoBanco);
        }
        catch
        {
            await transacao.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Ocupa a licença para a sessão e solta, no mesmo ato, licenças e guichês das sessões anteriores do usuário
    /// e o guichê que a própria sessão ainda tiver, porque a licença dela já tinha sido solta.
    /// </summary>
    internal async Task ConfirmarAsync(ReservaLicenca reserva, Guid sessaoId, IReadOnlyCollection<Guid> sessoesAnteriores, CancellationToken ct)
    {
        var tenantId = reserva.TenantId;
        var unidadeId = reserva.Candidata.UnidadeId;
        var membro = ConjuntoLicencas.Membro(sessaoId);
        var ocupouAgora = false;
        var restaurar = reserva.Desfecho == DesfechoOcupacao.NaoReconstruido;
        // Seats of the unit with this occupation; null when the session already held its seat (no new occupation).
        int? ocupadas = reserva.OcupadasNoBanco;
        if (reserva.MembroReserva is { } membroReserva)
        {
            // The reservation, the user's earlier sessions and the given ones leave in the same script that
            // occupies, so the seat passes from the reservation to the session without a window.
            var soltar = (await SessoesDoUsuarioAsync(tenantId, reserva.UsuarioId, ct))
                .Concat(sessoesAnteriores.Select(ConjuntoLicencas.Membro))
                .Where(s => s != membro)
                .Append(membroReserva)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var ocupacao = await conjunto.OcuparAsync(tenantId, membro, [reserva.Candidata], [], soltar, conjunto.PrazoMembro, ct);
            switch (ocupacao.Desfecho)
            {
                case DesfechoOcupacao.Ocupada:
                    unidadeId = ocupacao.UnidadeId!.Value;
                    ocupadas = ocupacao.Ocupadas;
                    ocupouAgora = true;
                    break;
                case DesfechoOcupacao.JaOcupava:
                    unidadeId = ocupacao.UnidadeId!.Value;
                    ocupadas = null;
                    break;
                case DesfechoOcupacao.SemSaldo:
                    // The reservation outlived PrazoReserva and its seat was taken meanwhile.
                    throw new ForbiddenException(MensagemLimite);
                default:
                    ocupadas = (await EscolherNoBancoAsync(tenantId, reserva.UsuarioId, [reserva.Candidata], ct)).Ocupadas;
                    restaurar = ocupacao.Desfecho == DesfechoOcupacao.NaoReconstruido;
                    break;
            }
        }

        var soltas = sessoesAnteriores.Append(sessaoId).ToArray();
        try
        {
            if (ocupadas is { } naUnidade && naUnidade > reserva.Candidata.Limite)
            {
                await RegistrarExcedenteAsync(tenantId, unidadeId, sessaoId, naUnidade, reserva.Candidata.Limite, ct);
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now()
                 WHERE "TenantId" = {tenantId} AND "SessaoId" = ANY({soltas}) AND "LiberadoEm" IS NULL
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM paf."AtividadesSessao"
                 WHERE "TenantId" = {tenantId} AND "UsuarioId" = {reserva.UsuarioId} AND "SessaoId" <> {sessaoId}
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO paf."AtividadesSessao" ("SessaoId", "UsuarioId", "UnidadeId", "UltimaAtividadeEm", "TenantId")
                VALUES ({sessaoId}, {reserva.UsuarioId}, {unidadeId}, now(), {tenantId})
                ON CONFLICT ("SessaoId") DO UPDATE SET "UnidadeId" = EXCLUDED."UnidadeId", "UltimaAtividadeEm" = now()
                """, ct);
            await reserva.Transacao.CommitAsync(ct);
        }
        catch
        {
            // The PostgreSQL row is the truth: without it the seat taken in the set is given back.
            if (ocupouAgora)
            {
                await SoltarNaLimpezaAsync(tenantId, membro);
            }

            throw;
        }

        if (restaurar)
        {
            // Decided in the degraded mode; when the Valkey is up but not yet rebuilt the member goes in now,
            // otherwise the rebuild on its return puts it there.
            await conjunto.RestaurarAsync(tenantId, membro, unidadeId, conjunto.PrazoMembro, ct);
        }
    }

    /// <summary>Reserva descartada sem confirmação: o membro provisório sai do conjunto.</summary>
    internal async Task DescartarAsync(ReservaLicenca reserva)
    {
        if (reserva.MembroReserva is { } membroReserva)
        {
            await SoltarNaLimpezaAsync(reserva.TenantId, membroReserva);
        }
    }

    // Cleanup runs after a failure, often the request's own cancellation: with that token it would throw and
    // replace the original error, and the seat would stay taken until it expires. Its own deadline instead.
    private async Task SoltarNaLimpezaAsync(string tenantId, string membro)
    {
        using var prazo = new CancellationTokenSource(PrazoLimpeza);
        await conjunto.LiberarAsync(tenantId, membro, prazo.Token);
    }

    /// <summary>
    /// Candidatas da cascata na ordem do escopo efetivo: só as unidades ativas e com contrato vigente, porque a
    /// unidade sem contrato não tem saldo qualquer que seja a política (RF22). A recusa diz a causa: nenhuma ativa,
    /// ou nenhuma ativa com contrato.
    /// </summary>
    private async Task<List<CandidataLicenca>> CandidatasAsync(string tenantId, IReadOnlyList<Guid> escopoEfetivo, CancellationToken ct)
    {
        var ativas = await db.Unidades.AsNoTracking()
            .Where(u => escopoEfetivo.Contains(u.Id) && u.Ativa)
            .Select(u => u.Id)
            .ToListAsync(ct);
        if (ativas.Count == 0)
        {
            throw new ForbiddenException(MensagemSemUnidadeAtiva);
        }

        var contratos = await ContratosVigentesAsync(tenantId, [.. ativas], ct);
        var candidatas = escopoEfetivo
            .Where(contratos.ContainsKey)
            .Select(id => contratos[id])
            .ToList();
        return candidatas.Count == 0 ? throw new ForbiddenException(MensagemSemContrato) : candidatas;
    }

    // Validity is a calendar day in America/Sao_Paulo, taken from the PostgreSQL clock.
    private async Task<Dictionary<Guid, CandidataLicenca>> ContratosVigentesAsync(string tenantId, Guid[] ids, CancellationToken ct) =>
        (await db.Database.SqlQuery<ContratoVigente>($"""
            SELECT DISTINCT ON ("UnidadeId") "UnidadeId", "LimiteSessoes", "Politica"
              FROM paf."ContratosUnidade"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = ANY({ids})
               AND "InicioVigencia" <= (now() AT TIME ZONE 'America/Sao_Paulo')::date
               AND ("FimVigencia" IS NULL OR "FimVigencia" >= (now() AT TIME ZONE 'America/Sao_Paulo')::date)
             ORDER BY "UnidadeId", "InicioVigencia" DESC
            """).ToListAsync(ct))
        .ToDictionary(
            c => c.UnidadeId,
            c => new CandidataLicenca(
                c.UnidadeId,
                c.LimiteSessoes,
                string.Equals(c.Politica, nameof(PoliticaLimiteLicenca.UltrapassarComAviso), StringComparison.Ordinal)));

    /// <summary>
    /// Troca manual de unidade (S07, RF22, ARQ seção 9): a licença sai da unidade de origem e entra na de destino,
    /// dentro da transação já aberta pelo chamador, ou a troca é recusada e nada muda. Decide o conjunto do Valkey
    /// numa operação só; com o Valkey fora ou antes da reconstrução, a contagem das linhas frescas sob os bloqueios
    /// de origem e destino em ordem canônica. Grava a unidade e o instante na linha de <see cref="AtividadeSessao"/>
    /// e o excedente da ultrapassagem contratada. Quem chama confirma com <see cref="ConcluirMigracaoAsync"/> depois
    /// do commit, ou desfaz com <see cref="DesfazerMigracaoAsync"/> se a transação não for confirmada.
    /// </summary>
    public async Task<MigracaoLicenca> MigrarAsync(string tenantId, Guid usuarioId, Guid sessaoId, Guid destino, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A migração da licença exige transação aberta.");
        }

        // Same user lock as the reservation: a reoccupation or refresh of this user confirming in the middle would
        // put the member back in the origin after it moved.
        await BloquearAsync($"paf-licenca-usuario:{tenantId}:{usuarioId}", ct);

        // Under the user lock, link before row: the order of ConfirmarAsync (user, link, row) and of LiberarAsync
        // (link, row). A login elsewhere or a logout of this session running alongside waits instead of deadlocking.
        await VinculosGuiche.TravarAtivoDaSessaoAsync(db, tenantId, sessaoId, ct);

        // The row lock keeps the renewal (RenovarMarcaAsync) from restoring the member in the origin before commit.
        var linhas = await db.Database.SqlQuery<Guid>($"""
            SELECT "UnidadeId" AS "Value" FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
               FOR UPDATE
            """).ToListAsync(ct);
        if (linhas.Count == 0)
        {
            // Released meanwhile (logout, grace): the next request re-occupies through the cascade.
            throw new UnauthorizedException(SessionCheckMiddleware.MensagemSessaoInvalida);
        }

        var origem = linhas[0];
        if (origem == destino)
        {
            return new MigracaoLicenca(tenantId, sessaoId, origem, destino, DesfechoOcupacao.JaOcupava);
        }

        var contrato = (await ContratosVigentesAsync(tenantId, [destino], ct)).GetValueOrDefault(destino)
            ?? throw new CustomException(MensagemDestinoSemContrato, [], HttpStatusCode.Conflict);

        var ocupacao = await conjunto.MoverAsync(tenantId, ConjuntoLicencas.Membro(sessaoId), contrato, conjunto.PrazoMembro, ct);
        var migracao = new MigracaoLicenca(tenantId, sessaoId, origem, destino, ocupacao.Desfecho);
        try
        {
            var ocupadas = ocupacao.Desfecho switch
            {
                DesfechoOcupacao.Ocupada => ocupacao.Ocupadas,
                // Already a member of the destination (a retry after a failed commit): no new seat, no new excess.
                DesfechoOcupacao.JaOcupava => 0,
                DesfechoOcupacao.SemSaldo => throw new CustomException(MensagemDestinoLimite, [], HttpStatusCode.Conflict),
                _ => await OcupadasNoDestinoAsync(tenantId, sessaoId, origem, contrato, ct),
            };

            if (ocupadas > contrato.Limite)
            {
                await RegistrarExcedenteAsync(tenantId, destino, sessaoId, ocupadas, contrato.Limite, ct);
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE paf."AtividadesSessao" SET "UnidadeId" = {destino}, "UltimaAtividadeEm" = now()
                 WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
                """, ct);
            return migracao;
        }
        catch
        {
            await DesfazerMigracaoAsync(migracao);
            throw;
        }
    }

    /// <summary>
    /// Depois do commit: decidida no modo degradado com o Valkey de pé e ainda não reconstruído, o membro entra no
    /// destino agora; com o Valkey fora, a reconstrução na volta dele o põe lá.
    /// </summary>
    public async Task ConcluirMigracaoAsync(MigracaoLicenca migracao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(migracao);
        if (migracao.Desfecho == DesfechoOcupacao.NaoReconstruido)
        {
            await RestaurarMembroAsync(db, conjunto, migracao.TenantId, migracao.SessaoId, migracao.Destino, ct);
        }
    }

    /// <summary>
    /// A transação da troca não foi confirmada: o membro que o conjunto já moveu volta à origem, que é a unidade que
    /// a linha do PostgreSQL continua dizendo. Prazo próprio, como a limpeza da reserva.
    /// </summary>
    public async Task DesfazerMigracaoAsync(MigracaoLicenca migracao)
    {
        ArgumentNullException.ThrowIfNull(migracao);
        if (migracao.Desfecho != DesfechoOcupacao.Ocupada)
        {
            return;
        }

        using var prazo = new CancellationTokenSource(PrazoLimpeza);
        await conjunto.RestaurarAsync(
            migracao.TenantId, ConjuntoLicencas.Membro(migracao.SessaoId), migracao.Origem, conjunto.PrazoMembro, prazo.Token);
    }

    /// <summary>
    /// Modo degradado da troca: bloqueios de origem e destino em ordem canônica por id, como a cascata, e a contagem
    /// das linhas frescas do destino sem a própria sessão. Devolve as vagas do destino com esta sessão.
    /// </summary>
    private async Task<int> OcupadasNoDestinoAsync(
        string tenantId, Guid sessaoId, Guid origem, CandidataLicenca destino, CancellationToken ct)
    {
        foreach (var unidadeId in new[] { origem, destino.UnidadeId }.Order())
        {
            await BloquearAsync($"paf-licenca:{tenantId}:{unidadeId}", ct);
        }

        var naUnidade = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = {destino.UnidadeId} AND "SessaoId" <> {sessaoId}
               AND "UltimaAtividadeEm" > now() - make_interval(mins => {PrazoMarcaMinutos})
            """).SingleAsync(ct);
        if (naUnidade >= destino.Limite && !destino.UltrapassaComAviso)
        {
            throw new CustomException(MensagemDestinoLimite, [], HttpStatusCode.Conflict);
        }

        return naUnidade + 1;
    }

    /// <summary>
    /// Modo degradado: a cascata pela contagem das linhas frescas. Os bloqueios de todas as candidatas são tomados
    /// antes de contar qualquer uma, em ordem canônica por id, e seguram até o fim da transação: escopos [A, B] e
    /// [B, A] disputando ao mesmo tempo esperam um pelo outro em vez de travar em ciclo.
    /// </summary>
    private async Task<EscolhaNoBanco> EscolherNoBancoAsync(
        string tenantId, Guid usuarioId, IReadOnlyList<CandidataLicenca> candidatas, CancellationToken ct)
    {
        var ids = candidatas.Select(c => c.UnidadeId).Order().ToArray();
        foreach (var unidadeId in ids)
        {
            await BloquearAsync($"paf-licenca:{tenantId}:{unidadeId}", ct);
        }

        // The user's own earlier sessions are released in the same act, so they do not count against him.
        var ocupadas = await db.Database.SqlQuery<OcupadasDaUnidade>($"""
            SELECT "UnidadeId", count(*)::int AS "Ocupadas"
              FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = ANY({ids}) AND "UsuarioId" <> {usuarioId}
               AND "UltimaAtividadeEm" > now() - make_interval(mins => {PrazoMarcaMinutos})
             GROUP BY "UnidadeId"
            """).ToDictionaryAsync(o => o.UnidadeId, o => o.Ocupadas, ct);

        foreach (var candidata in candidatas)
        {
            var naUnidade = ocupadas.GetValueOrDefault(candidata.UnidadeId);
            if (naUnidade < candidata.Limite || candidata.UltrapassaComAviso)
            {
                return new EscolhaNoBanco(candidata, naUnidade + 1);
            }
        }

        throw new ForbiddenException(MensagemLimite);
    }

    private Task BloquearAsync(string chave, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", ct);

    // Members of the user's sessions holding a licence: they count as free for him, released when he confirms.
    private async Task<List<string>> SessoesDoUsuarioAsync(string tenantId, Guid usuarioId, CancellationToken ct) =>
        (await db.Database.SqlQuery<Guid>($"""
            SELECT "SessaoId" AS "Value" FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "UsuarioId" = {usuarioId}
            """).ToListAsync(ct))
        .Select(ConjuntoLicencas.Membro)
        .ToList();

    /// <summary>
    /// Linha de excedente em <see cref="ConsumoLicenca"/> (RF22, ARQ seção 9), na transação da ocupação: a ocupação
    /// que não for gravada não deixa registro, e a gravada sempre deixa. É também o que a tela lê para avisar o
    /// usuário que entrou acima do limite (D03).
    /// </summary>
    private async Task RegistrarExcedenteAsync(
        string tenantId, Guid unidadeId, Guid sessaoId, int ocupadas, int limite, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."ConsumosLicenca" ("Id", "UnidadeId", "Tipo", "Ocupadas", "Limite", "SessaoId", "RegistradoEm", "TenantId")
            VALUES ({Guid.CreateVersion7()}, {unidadeId}, {nameof(TipoConsumoLicenca.Excedente)}, {ocupadas}, {limite}, {sessaoId}, now(), {tenantId})
            """, ct);
        logger.LogWarning(
            "Licença acima do limite contratado na unidade {UnidadeId} do tenant {TenantId}: {Ocupadas} de {Limite}",
            unidadeId, tenantId, ocupadas, limite);
    }

    /// <summary>Unidade da licença quando a marca de sessão viva está dentro do prazo; nulo caso contrário.</summary>
    public Task<Guid?> UnidadeDaMarcaVivaAsync(string tenantId, Guid sessaoId, CancellationToken ct) =>
        db.Database.SqlQuery<Guid?>($"""
            SELECT "UnidadeId" AS "Value"
              FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
               AND "UltimaAtividadeEm" > now() - make_interval(mins => {PrazoMarcaMinutos})
            """).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Unidade da última licença registrada da sessão, viva ou vencida; nulo depois de solta. É a unidade que a tela
    /// mostrava quando a sessão é revivida sem estado de unidade no Valkey.
    /// </summary>
    public Task<Guid?> UnidadeDaUltimaMarcaAsync(string tenantId, Guid sessaoId, CancellationToken ct) =>
        db.Database.SqlQuery<Guid?>($"""
            SELECT "UnidadeId" AS "Value"
              FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
            """).FirstOrDefaultAsync(ct);

    public Task<bool> UsuarioDoPafAsync(Guid usuarioId, CancellationToken ct) =>
        db.UsuariosUnidade.AsNoTracking().AnyAsync(u => u.UsuarioId == usuarioId, ct);

    /// <summary>
    /// Renova a marca de sessão viva e o membro do conjunto, no máximo uma escrita por intervalo; verdadeiro
    /// quando escreveu. O membro volta à unidade da linha mesmo se tinha sumido do Valkey, sem cascata (seção 9):
    /// é por aqui que a sessão viva entra de novo no conjunto antes da reconstrução.
    /// </summary>
    public async Task<bool> RenovarMarcaAsync(string tenantId, Guid sessaoId, CancellationToken ct)
    {
        var unidades = await db.Database.SqlQuery<Guid>($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now()
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
               AND "UltimaAtividadeEm" < now() - make_interval(mins => {IntervaloMarcaMinutos})
            RETURNING "UnidadeId" AS "Value"
            """).ToListAsync(ct);
        if (unidades.Count == 0)
        {
            return false;
        }

        await RestaurarMembroAsync(db, conjunto, tenantId, sessaoId, unidades[0], ct);
        return true;
    }

    /// <summary>
    /// Devolve o membro da sessão à unidade da marca recém-renovada. Um logout ou uma soltura que apague a linha e
    /// solte o membro entre a renovação e esta restauração deixaria um membro fantasma por um prazo inteiro: a linha é
    /// relida depois de restaurar e, se sumiu, o membro sai de novo. <see cref="LiberarAsync"/> só toca o Valkey
    /// depois de apagar a linha, então toda ordem com ele termina sem o membro. A confirmação de um login novo solta
    /// os membros das sessões anteriores antes de apagar as linhas delas: ali a janela continua, até o prazo do membro.
    /// </summary>
    internal static async Task RestaurarMembroAsync(
        PafDbContext db, ConjuntoLicencas conjunto, string tenantId, Guid sessaoId, Guid unidadeId, CancellationToken ct)
    {
        var membro = ConjuntoLicencas.Membro(sessaoId);
        await conjunto.RestaurarAsync(tenantId, membro, unidadeId, conjunto.PrazoMembro, ct);
        var segue = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM paf."AtividadesSessao"
                            WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}) AS "Value"
            """).SingleAsync(ct);
        if (!segue)
        {
            await conjunto.LiberarAsync(tenantId, membro, ct);
        }
    }

    /// <summary>Solta licença e guichê da sessão numa transação (logout), e depois o membro do conjunto.</summary>
    public async Task LiberarAsync(string tenantId, Guid sessaoId, CancellationToken ct)
    {
        await using (var transacao = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now()
                 WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId} AND "LiberadoEm" IS NULL
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM paf."AtividadesSessao" WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
                """, ct);
            await transacao.CommitAsync(ct);
        }

        // Without Valkey the member stays until it expires; the rebuild on its return drops it earlier.
        await conjunto.LiberarAsync(tenantId, ConjuntoLicencas.Membro(sessaoId), ct);
    }

    private sealed record ContratoVigente(Guid UnidadeId, int LimiteSessoes, string Politica);

    private sealed record OcupadasDaUnidade(Guid UnidadeId, int Ocupadas);

    private sealed record EscolhaNoBanco(CandidataLicenca Candidata, int Ocupadas);
}

/// <summary>
/// Licença movida pela troca manual de unidade, ainda dentro da transação. <c>Desfecho</c> diz quem decidiu:
/// <c>Ocupada</c> moveu no conjunto (desfazer devolve à origem), <c>JaOcupava</c> não mudou nada, e
/// <c>NaoReconstruido</c> ou <c>Indisponivel</c> decidiram pelo modo degradado.
/// </summary>
public sealed record MigracaoLicenca(string TenantId, Guid SessaoId, Guid Origem, Guid Destino, DesfechoOcupacao Desfecho);
