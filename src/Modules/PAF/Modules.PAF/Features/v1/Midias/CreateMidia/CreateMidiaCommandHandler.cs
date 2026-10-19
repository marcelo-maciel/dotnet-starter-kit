using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Midias.CreateMidia;

/// <summary>
/// Confirms the envio against the store and creates the mídia (RF03). A second confirmation of the same envio, racing or
/// late, returns the mídia the first one created: the conditional <c>ConfirmadoEm</c> stamp lets only one through.
/// </summary>
public sealed class CreateMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    ConfirmacaoEnvioMidia confirmacao)
    : ICommandHandler<CreateMidiaCommand, MidiaDto>
{
    public async ValueTask<MidiaDto> Handle(CreateMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        var envio = await confirmacao.LerAsync(command.EnvioId, cancellationToken).ConfigureAwait(false);
        if (await DoEnvioAsync(envio.Id, cancellationToken).ConfigureAwait(false) is { } existente)
        {
            return existente;
        }

        await confirmacao.ConferirAsync(envio, tenantId, cancellationToken).ConfigureAwait(false);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await confirmacao.MarcarConfirmadoAsync(envio, tenantId, cancellationToken).ConfigureAwait(false))
        {
            await transacao.RollbackAsync(cancellationToken).ConfigureAwait(false);
            if (await DoEnvioAsync(envio.Id, cancellationToken).ConfigureAwait(false) is { } vencedora)
            {
                return vencedora;
            }

            ConfirmacaoEnvioMidia.ExigirAberto(await confirmacao.LerAsync(envio.Id, cancellationToken).ConfigureAwait(false));
            throw new InvalidOperationException($"Envio {envio.Id} still open after losing its confirmation stamp.");
        }

        var midia = Midia.Criar(envio, command.Descricao.Trim(), command.DuracaoSegundos);
        db.Midias.Add(midia);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MidiaTravada.ParaDto(midia);
    }

    private async Task<MidiaDto?> DoEnvioAsync(Guid envioId, CancellationToken ct)
    {
        var midia = await db.Midias.AsNoTracking().FirstOrDefaultAsync(m => m.EnvioId == envioId, ct).ConfigureAwait(false);
        return midia is null ? null : MidiaTravada.ParaDto(midia);
    }
}
