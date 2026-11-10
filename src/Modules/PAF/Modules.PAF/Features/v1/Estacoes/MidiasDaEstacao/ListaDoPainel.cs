using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.MidiasDaEstacao;

/// <summary>Rules and reads shared by the painel list cadastro (M06, RF04 painel block).</summary>
internal static class ListaDoPainel
{
    public const string MensagemSemPapelPainel = "Só uma estação com papel de painel tem lista de exibição de mídias.";

    public const string MensagemMidia = "Escolha mídias ativas, com o arquivo já enviado, desta unidade.";

    /// <summary>The station's tipo through the unit filter: another unidade's or tenant's station is 404.</summary>
    public static async Task<TipoEstacao> TipoAsync(PafDbContext db, Guid estacaoId, CancellationToken ct)
    {
        var tipos = await db.Estacoes.AsNoTracking()
            .Where(e => e.Id == estacaoId)
            .Select(e => e.Tipo)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return tipos.Count == 1 ? tipos[0] : throw new NotFoundException($"Estação {estacaoId} não encontrada.");
    }

    public static void ExigirPapelPainel(TipoEstacao tipo)
    {
        if (!ListasDaEstacao.Papeis(tipo).Contains(PapelEstacao.Painel))
        {
            throw new CustomException(MensagemSemPapelPainel, [], HttpStatusCode.BadRequest);
        }
    }

    /// <summary>
    /// Every distinct mídia must be active, of the current unidade and with its file confirmed (same rule as a
    /// programação, M05). <c>FOR SHARE</c> holds them until commit, so a concurrent disable waits instead of slipping
    /// between this check and the write. One 400 for every refusal, so the answer never tells whether an id exists
    /// elsewhere; the composite FK <c>FK_PainelMidias_Midias_MesmaUnidade</c> is the second barrier for the unidade.
    /// </summary>
    public static async Task ExigirMidiasExibiveisAsync(
        PafDbContext db, string tenantId, Guid unidadeId, IReadOnlyCollection<Guid> midias, CancellationToken ct)
    {
        if (midias.Count == 0)
        {
            return;
        }

        var ids = midias.ToArray();
        var travadas = await db.Database.SqlQuery<Guid>($"""
            SELECT m."Id" AS "Value" FROM paf."Midias" m
              JOIN paf."EnviosMidia" e ON e."TenantId" = {tenantId} AND e."Id" = m."EnvioId"
             WHERE m."TenantId" = {tenantId} AND m."UnidadeId" = {unidadeId} AND m."Id" = ANY({ids})
               AND m."Ativa" AND e."ConfirmadoEm" IS NOT NULL
               FOR SHARE OF m
            """).ToListAsync(ct).ConfigureAwait(false);
        if (travadas.Count != ids.Length)
        {
            throw new CustomException(MensagemMidia, [], HttpStatusCode.BadRequest);
        }
    }

    public static async Task<MidiasDaEstacaoDto> LerAsync(PafDbContext db, Guid estacaoId, CancellationToken ct)
    {
        var itens = await (
                from p in db.PainelMidias.AsNoTracking()
                join m in db.Midias.AsNoTracking() on p.MidiaId equals m.Id
                where p.EstacaoId == estacaoId
                orderby p.Ordem
                select new { p.Id, p.Ordem, MidiaId = m.Id, m.Descricao, m.Tipo, m.DuracaoSegundos, m.Ativa })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new MidiasDaEstacaoDto(
            estacaoId,
            itens.Select(i => new ItemMidiaDaEstacaoDto(i.Id, i.Ordem, i.MidiaId, i.Descricao, i.Tipo.ToString(), i.DuracaoSegundos, i.Ativa)).ToList());
    }
}
