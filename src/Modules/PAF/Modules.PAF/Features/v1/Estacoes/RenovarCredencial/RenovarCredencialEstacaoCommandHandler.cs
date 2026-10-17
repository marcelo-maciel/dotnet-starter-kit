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

namespace FSH.Modules.PAF.Features.v1.Estacoes.RenovarCredencial;

/// <summary>
/// Renovação pelo segredo de longa duração, sem exigir token de acesso válido: um painel desligado à noite
/// volta a operar de manhã, porque a estação não cai por expiração enquanto vale no cadastro (RF04).
/// Revogação e desativação chegam ao aparelho aqui também. Toda recusa é a mesma, genérica.
/// </summary>
public sealed class RenovarCredencialEstacaoCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    EmissorAcessoEstacao emissor)
    : ICommandHandler<RenovarCredencialEstacaoCommand, AcessoEstacaoDto>
{
    public async ValueTask<AcessoEstacaoDto> Handle(RenovarCredencialEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tenant = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();
        var hash = SegredoEstacao.Hash(command.Segredo);

        var vinculo = await db.VinculosEstacao
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .AsNoTracking()
            .Where(v => v.RevogadoEm == null && (v.CredencialEmissaoHash == hash || v.CredencialPainelHash == hash))
            .Select(v => new { v.Id, v.UnidadeId, v.EstacaoId, Emissao = v.CredencialEmissaoHash == hash })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedException();

        unidadeCorrente.Definir(vinculo.UnidadeId);

        var ativa = await db.Estacoes
            .AnyAsync(e => e.Id == vinculo.EstacaoId && e.Ativa, cancellationToken)
            .ConfigureAwait(false);
        if (!ativa)
        {
            throw new UnauthorizedException();
        }

        var papel = vinculo.Emissao ? PapelEstacao.Emissao : PapelEstacao.Painel;
        return await emissor
            .EmitirAsync(tenant, vinculo.Id, vinculo.EstacaoId, vinculo.UnidadeId, papel, cancellationToken)
            .ConfigureAwait(false);
    }
}
