using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Estacoes;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ResgatarVinculo;

/// <summary>
/// Resgate anônimo do link (RF04). Tenant inexistente, link desconhecido, já resgatado ou revogado e estação
/// inativa recebem a mesma recusa genérica, pela razão do RF01: a resposta não revela qual foi o caso.
/// </summary>
public sealed class ResgatarVinculoEstacaoCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    EmissorAcessoEstacao emissor)
    : ICommandHandler<ResgatarVinculoEstacaoCommand, CredenciaisEstacaoDto>
{
    public async ValueTask<CredenciaisEstacaoDto> Handle(ResgatarVinculoEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tenant = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();
        var linkHash = SegredoEstacao.Hash(command.Token);

        // The anonymous caller has no current unit yet; only the unit filter is lifted, the tenant filter stays.
        var vinculo = await db.VinculosEstacao
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .AsNoTracking()
            .Where(v => v.LinkHash == linkHash && v.ResgatadoEm == null && v.RevogadoEm == null)
            .Select(v => new { v.Id, v.UnidadeId, v.EstacaoId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedException();

        unidadeCorrente.Definir(vinculo.UnidadeId);

        var tipo = await db.Estacoes
            .AsNoTracking()
            .Where(e => e.Id == vinculo.EstacaoId && e.Ativa)
            .Select(e => (TipoEstacao?)e.Tipo)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedException();

        var papeis = PapeisDaEstacao.ComCredencial(tipo);
        if (papeis.Count == 0)
        {
            throw new UnauthorizedException();
        }

        var segredoEmissao = papeis.Contains(PapelEstacao.Emissao) ? SegredoEstacao.Gerar() : null;
        var segredoPainel = papeis.Contains(PapelEstacao.Painel) ? SegredoEstacao.Gerar() : null;
        var hashEmissao = segredoEmissao is null ? null : SegredoEstacao.Hash(segredoEmissao);
        var hashPainel = segredoPainel is null ? null : SegredoEstacao.Hash(segredoPainel);

        // Consumption is one conditional UPDATE: of two devices opening the same link at once, the row lock
        // lets exactly one see ResgatadoEm still NULL. Tokens are signed only after it, so the loser holds none.
        var consumidos = await db.VinculosEstacao
            .Where(v => v.Id == vinculo.Id && v.ResgatadoEm == null && v.RevogadoEm == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.ResgatadoEm, v => DateTimeOffset.UtcNow)
                .SetProperty(v => v.CredencialEmissaoHash, hashEmissao)
                .SetProperty(v => v.CredencialPainelHash, hashPainel), cancellationToken)
            .ConfigureAwait(false);
        if (consumidos != 1)
        {
            throw new UnauthorizedException();
        }

        var credenciais = new List<CredencialEstacaoDto>(papeis.Count);
        foreach (var papel in papeis)
        {
            var segredo = papel == PapelEstacao.Emissao ? segredoEmissao! : segredoPainel!;
            var acesso = await emissor
                .EmitirAsync(tenant, vinculo.Id, vinculo.EstacaoId, vinculo.UnidadeId, papel, cancellationToken)
                .ConfigureAwait(false);
            credenciais.Add(new CredencialEstacaoDto(acesso.Papel, segredo, acesso.AccessToken, acesso.AccessTokenExpiresAt));
        }

        return new CredenciaisEstacaoDto(credenciais);
    }
}
