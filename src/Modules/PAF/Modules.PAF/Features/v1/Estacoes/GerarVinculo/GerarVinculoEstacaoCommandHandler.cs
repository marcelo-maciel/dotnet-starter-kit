using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Estacoes;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.GerarVinculo;

public sealed class GerarVinculoEstacaoCommandHandler(
    PafDbContext db,
    ConexoesPaf conexoes,
    AtosImpersonacao atos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<GerarVinculoEstacaoCommand, VinculoEstacaoGeradoDto>
{
    public async ValueTask<VinculoEstacaoGeradoDto> Handle(GerarVinculoEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tenant = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new UnauthorizedException();

        // Unit-filtered: an admin only links stations of the current unit (ADR 0006).
        var estacao = await db.Estacoes
            .AsNoTracking()
            .Where(e => e.Id == command.EstacaoId)
            .Select(e => new { e.UnidadeId, e.Tipo, e.Ativa })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Estação {command.EstacaoId} não encontrada.");

        if (!estacao.Ativa)
        {
            throw new CustomException("Estação inativa não pode ser vinculada.", Array.Empty<string>(), HttpStatusCode.Conflict);
        }

        if (PapeisDaEstacao.ComCredencial(estacao.Tipo).Count == 0)
        {
            throw new CustomException("Só estações de emissão e de painel usam vínculo.", Array.Empty<string>(), HttpStatusCode.BadRequest);
        }

        var token = SegredoEstacao.Gerar();
        var linkHash = SegredoEstacao.Hash(token);
        VinculoEstacao? vinculo = null;

        // A new link replaces the station's device: revoking the previous link and recording the new one
        // commit together, so a failure never leaves the station with two links or with none.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            // The clear also drops the impersonation act noted by the pipeline; it rides this transaction again.
            atos.Anotar(atendimentoId: null);
            vinculo = VinculoEstacao.Criar(estacao.UnidadeId, command.EstacaoId, linkHash);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await RevogacaoVinculo.RevogarVigentesAsync(db, command.EstacaoId, ct).ConfigureAwait(false);
            db.VinculosEstacao.Add(vinculo);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        // The replaced device loses its connections; the new one has none yet.
        await conexoes.EncerrarEstacaoAsync(tenant, command.EstacaoId, cancellationToken).ConfigureAwait(false);
        return new VinculoEstacaoGeradoDto(vinculo!.Id, tenant, token);
    }
}
