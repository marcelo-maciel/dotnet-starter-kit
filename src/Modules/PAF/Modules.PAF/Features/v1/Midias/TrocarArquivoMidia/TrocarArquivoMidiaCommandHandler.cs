using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Midias.PedirEnvioMidia;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Midias.TrocarArquivoMidia;

/// <summary>
/// Points the mídia to a new, confirmed envio (RF03). Under the mídia row lock the version decides: of two swaps read at
/// the same version one wins and the other gets 409 with its envio left unreferenced for the cleanup. The old object is
/// left in the store (30-day lock, key never reused).
/// </summary>
public sealed class TrocarArquivoMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    ConfirmacaoEnvioMidia confirmacao)
    : ICommandHandler<TrocarArquivoMidiaCommand, MidiaDto>
{
    public async ValueTask<MidiaDto> Handle(TrocarArquivoMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        var lida = await db.Midias.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw MidiaTravada.NaoEncontrada(command.Id);
        var envio = await confirmacao.LerAsync(command.EnvioId, cancellationToken).ConfigureAwait(false);
        if (lida.EnvioId == envio.Id)
        {
            // The same swap sent again after it went through.
            return MidiaTravada.ParaDto(lida);
        }

        if (envio.Tipo != lida.Tipo)
        {
            throw new CustomException(PedirEnvioMidiaCommandHandler.MensagemTipoDaTroca, [], HttpStatusCode.BadRequest);
        }

        // Cheap refusal before touching the store; the version is checked again under the lock.
        MidiaTravada.ExigirVersao(lida, command.Versao);
        await confirmacao.ConferirAsync(envio, tenantId, cancellationToken).ConfigureAwait(false);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var midia = await MidiaTravada.TravarAsync(db, tenantId, unidadeId, command.Id, cancellationToken).ConfigureAwait(false);
        if (midia.EnvioId == envio.Id)
        {
            return MidiaTravada.ParaDto(midia);
        }

        MidiaTravada.ExigirVersao(midia, command.Versao);
        if (!await confirmacao.MarcarConfirmadoAsync(envio, tenantId, cancellationToken).ConfigureAwait(false))
        {
            await transacao.RollbackAsync(cancellationToken).ConfigureAwait(false);
            ConfirmacaoEnvioMidia.ExigirAberto(await confirmacao.LerAsync(envio.Id, cancellationToken).ConfigureAwait(false));
            throw new InvalidOperationException($"Envio {envio.Id} still open after losing its confirmation stamp.");
        }

        midia.TrocarArquivo(envio);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MidiaTravada.ParaDto(midia);
    }
}
