using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Midias;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Midias.PedirEnvioMidia;

/// <summary>
/// One upload of the current unidade (RF03): the envio row comes first, so every key ever handed to a browser has a row
/// the cleanup can find, even when the URL is never used.
/// </summary>
public sealed class PedirEnvioMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    ArmazenamentoMidia armazenamento)
    : ICommandHandler<PedirEnvioMidiaCommand, EnvioMidiaDto>, ICommandHandler<PedirEnvioTrocaMidiaCommand, EnvioMidiaDto>
{
    public const string MensagemTipoDaTroca = "A troca de arquivo mantém o tipo da mídia. Envie um arquivo do mesmo tipo.";

    public ValueTask<EnvioMidiaDto> Handle(PedirEnvioMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new ValueTask<EnvioMidiaDto>(CriarEnvioAsync(command.NomeArquivo, tipoExigido: null, cancellationToken));
    }

    public async ValueTask<EnvioMidiaDto> Handle(PedirEnvioTrocaMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();
        var tipo = await db.Midias.AsNoTracking()
            .Where(m => m.Id == command.MidiaId)
            .Select(m => (TipoMidia?)m.Tipo)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw MidiaTravada.NaoEncontrada(command.MidiaId);
        return await CriarEnvioAsync(command.NomeArquivo, tipo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvioMidiaDto> CriarEnvioAsync(string nomeArquivo, TipoMidia? tipoExigido, CancellationToken ct)
    {
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        // The validator already refused an unknown format; the name is used for nothing else.
        var formato = FormatosMidia.PorNomeDeArquivo(nomeArquivo)
            ?? throw new CustomException(RegrasMidia.MensagemFormato, [], HttpStatusCode.BadRequest);
        if (tipoExigido is { } tipo && tipo != formato.Tipo)
        {
            throw new CustomException(MensagemTipoDaTroca, [], HttpStatusCode.BadRequest);
        }

        var chave = ChaveObjetoMidia.NovoEnvio(tenantId, unidadeId, formato);
        var envio = EnvioMidia.Criar(chave.EnvioId, unidadeId, chave.Chave, formato.Tipo, formato.Mime);
        db.EnviosMidia.Add(envio);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var url = await armazenamento.GerarUrlEnvioAsync(envio, ct).ConfigureAwait(false);
        return new EnvioMidiaDto(envio.Id, url.Url, url.RequiredHeaders, url.ExpiresAt, armazenamento.TetoBytes(envio.Tipo));
    }
}
