using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.TempoReal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FSH.Modules.PAF.Features.v1.Atendimentos;

/// <summary>Row of the eligibility query, materialized by EF from raw SQL.</summary>
internal sealed record Candidato(Guid AtendimentoId, Guid FichaId, Guid FilaId, Guid PrioridadeId, string Letra, int Numero);

/// <summary>
/// The core every way of calling a ficha shares (requisitos seção 4, ARQ seção 8 item 5): one eligibility predicate,
/// one call order, and one "mark Chamado + UltimaChamadaEm + notices" block. Chamar-próximo, the chosen ficha
/// (RF04 "selecionar ficha para atendimento") and later the VIP call (F11) differ only in how they pick and lock the
/// row; whatever they pick must pass the same predicate, or the chosen-ficha path would call what the queue never would.
/// </summary>
internal static partial class ChamadaDeFicha
{
    /// <summary>Key of the RF04 guichê block option "selecionar ficha para atendimento" in Estacao.Configuracao.</summary>
    public const string ChaveSelecionarFicha = "selecionarFicha";

    public const string MensagemSemSelecao = "Este guichê não permite selecionar ficha para atendimento.";

    /// <summary>
    /// FROM and WHERE of the candidate filter: fila of the guichê role (active or not), prioridade enabled on the station,
    /// waiting, eligible (today only when RF06 "somente fichas da data atual" is on), ficha older than five seconds, no
    /// other atendimento of the ficha in progress. Aliases a, f, ef, p are what <see cref="OrdemDaChamada"/> and the
    /// SELECT lists read. The date test is a lower bound (">= today", same as "= today" because ElegivelDesde only
    /// takes now()) so DataElegibilidade stays a range condition on IX_Atendimentos_Candidatos with the parameter on or off.
    /// </summary>
    public static FormattableString Elegiveis(string tenantId, Guid unidadeId, Guid estacaoId, bool somenteDataAtual) => $"""
              FROM paf."Atendimentos" a
              JOIN paf."Fichas" f ON f."Id" = a."FichaId"
              JOIN paf."EstacaoFilas" ef
                ON ef."EstacaoId" = {estacaoId} AND ef."Papel" = 'Guiche' AND ef."FilaId" = a."FilaId"
              JOIN paf."EstacaoPrioridades" ep
                ON ep."EstacaoId" = {estacaoId} AND ep."PrioridadeId" = a."PrioridadeId"
              JOIN paf."Prioridades" p ON p."Id" = a."PrioridadeId"
             WHERE a."TenantId" = {tenantId} AND a."UnidadeId" = {unidadeId}
               AND a."Estado" = 'Aguardando'
               AND a."DataElegibilidade" >= CASE WHEN {somenteDataAtual}
                   THEN (now() AT TIME ZONE 'America/Sao_Paulo')::date ELSE '-infinity'::date END
               AND f."CriadoEm" <= now() - interval '5 seconds'
               AND NOT EXISTS (
                   SELECT 1 FROM paf."Atendimentos" o
                    WHERE o."TenantId" = {tenantId} AND o."FichaId" = a."FichaId" AND o."Estado" IN ('Chamado', 'EmAtendimento'))
            """;

    /// <summary>Station's fila order when it respects it, then prioridade value descending, then creation order (requisitos seção 4).</summary>
    public static FormattableString OrdemDaChamada(bool respeitaOrdem) => $"""
             ORDER BY CASE WHEN {respeitaOrdem} THEN ef."Ordem" ELSE 0 END, p."Valor" DESC, a."CriadoEm", a."Id"
            """;

    /// <summary>
    /// Joins SQL fragments into one statement, renumbering each fragment's placeholders after the previous ones' arguments.
    /// Interpolating a FormattableString into another would bind it as a parameter value, not splice its text. Each
    /// fragment stays a compiled literal, so the raw-SQL barrier (PafSqlBrutoTests) still reads every one of them.
    /// </summary>
    public static FormattableString Concatenar(params FormattableString[] partes)
    {
        var formato = new StringBuilder();
        var argumentos = new List<object?>();
        foreach (var parte in partes)
        {
            var deslocamento = argumentos.Count;
            formato.Append(Marcador().Replace(parte.Format, m => m.Groups[1].Success
                ? "{" + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + deslocamento)
                    .ToString(CultureInfo.InvariantCulture)
                : m.Value));
            formato.Append('\n');
            argumentos.AddRange(parte.GetArguments());
        }

        return FormattableStringFactory.Create(formato.ToString(), argumentos.ToArray());
    }

    /// <summary>
    /// Marks the locked atendimento Chamado on the guichê with the PostgreSQL clock and stamps the fila's last call. The
    /// state guard in the WHERE makes a lost race visible: null means the row was no longer Aguardando and nothing was written.
    /// </summary>
    public static async Task<int?> MarcarChamadoAsync(PafDbContext db, Guiche guiche, Candidato candidato, CancellationToken ct)
    {
        var versoes = await db.Database.SqlQuery<int>($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Chamado', "EstacaoId" = {guiche.Estacao.Id}, "UsuarioId" = {guiche.UsuarioId},
                   "ChamadoEm" = now(), "Versao" = "Versao" + 1
             WHERE "TenantId" = {guiche.TenantId} AND "Id" = {candidato.AtendimentoId} AND "Estado" = 'Aguardando'
            RETURNING "Versao" AS "Value"
            """).ToListAsync(ct).ConfigureAwait(false);
        if (versoes.Count == 0)
        {
            return null;
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Filas" SET "UltimaChamadaEm" = now()
             WHERE "TenantId" = {guiche.TenantId} AND "Id" = {candidato.FilaId}
            """, ct).ConfigureAwait(false);
        return versoes[0];
    }

    /// <summary>Seção 8 item 6: both notices go to the outbox inside the transaction and straight to the hub only after the commit.</summary>
    public static async Task<ChamadaDto> ConfirmarAsync(
        PafDbContext db, AvisosTempoReal avisos, IDbContextTransaction transacao, Guiche guiche, Candidato candidato, int versao, CancellationToken ct)
    {
        var senha = OperacaoGuiche.Senha(candidato.Letra, candidato.Numero);
        var paineis = await OperacaoGuiche.PaineisDaFilaAsync(db, candidato.FilaId, ct).ConfigureAwait(false);
        var (numeroGuiche, nomeExibicaoGuiche) = Estacoes.ConfiguracaoDaEstacao.IdentidadeDoGuiche(guiche.Estacao.Configuracao);
        var chamada = SenhaChamadaPaf.Criar(guiche.TenantId, OperacaoGuiche.Correlacao(), new SenhaChamada(
            Guid.CreateVersion7(), guiche.Estacao.UnidadeId, candidato.AtendimentoId, candidato.FichaId, versao,
            senha, guiche.Estacao.Id, guiche.Estacao.Nome) { NumeroGuiche = numeroGuiche, NomeExibicaoGuiche = nomeExibicaoGuiche }, paineis);
        var fila = FilaAtualizadaPaf.Criar(guiche.TenantId, OperacaoGuiche.Correlacao(),
            new FilaAtualizada(Guid.CreateVersion7(), guiche.Estacao.UnidadeId, candidato.FilaId));

        await avisos.GravarAsync(chamada, ct).ConfigureAwait(false);
        await avisos.GravarAsync(fila, ct).ConfigureAwait(false);
        await transacao.CommitAsync(ct).ConfigureAwait(false);
        await avisos.EnviarAsync(chamada, ct).ConfigureAwait(false);
        await avisos.EnviarAsync(fila, ct).ConfigureAwait(false);

        return new ChamadaDto(candidato.AtendimentoId, candidato.FichaId, senha, candidato.FilaId, candidato.PrioridadeId, versao);
    }

    /// <summary>
    /// ponytail: the RF04 guichê block has no cadastro screen yet; its boolean options live in the station's JSON
    /// configuration and are off when absent. Move them to columns when the block gets its endpoint (C04).
    /// </summary>
    public static bool OpcaoLigada(string? configuracao, string chave)
    {
        if (string.IsNullOrWhiteSpace(configuracao))
        {
            return false;
        }

        using var json = JsonDocument.Parse(configuracao);
        return json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty(chave, out var valor)
            && valor.ValueKind == JsonValueKind.True;
    }

    // An escaped brace pair stays as is; only "{n" opens a placeholder (alignment and format specifiers follow the index untouched).
    [GeneratedRegex(@"\{\{|\}\}|\{(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Marcador();
}
