using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Storage;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Midias.CreateMidia;
using FSH.Modules.PAF.Features.v1.Midias.PedirEnvioMidia;
using FSH.Modules.PAF.Midias;
using NSubstitute;
using Microsoft.Extensions.Options;

namespace PAF.Tests;

/// <summary>
/// M03 rules that need no database: the RF03 request validation (extension and MIME), the judgment of the stored object
/// (type, empty, ceiling) and the RNF05 deadline of a store call (the real S3 client against a silent server is in Integration.Tests).
/// </summary>
public sealed class MidiaCadastroTests
{
    private const long Teto = 1000;

    [Theory]
    [InlineData("abertura.avi", "video/x-msvideo")]
    [InlineData("abertura.mov", "video/quicktime")]
    [InlineData("abertura.flv", "video/x-flv")]
    [InlineData("abertura", "video/mp4")]
    [InlineData("abertura.", "video/mp4")]
    [InlineData("abertura.mp4.avi", "video/mp4")]
    [InlineData("abertura.mp4 ", "video/mp4")]
    public void PedirEnvio_FormatoForaDaTabela_RecusadoComAListaDosAceitos(string nome, string mime)
    {
        var resultado = new PedirEnvioMidiaCommandValidator().Validate(new PedirEnvioMidiaCommand(nome, mime));

        resultado.IsValid.ShouldBeFalse();
        resultado.Errors.ShouldContain(e => e.PropertyName == "NomeArquivo" && e.ErrorMessage.Contains(FormatosMidia.ListaAceitos, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("abertura.mp4", "image/png")]
    [InlineData("abertura.mp4", "video/webm")]
    [InlineData("logo.png", "image/jpeg")]
    [InlineData("logo.png", "")]
    public void PedirEnvio_MimeTrocadoOuVazio_Recusado(string nome, string mime)
    {
        var resultado = new PedirEnvioMidiaCommandValidator().Validate(new PedirEnvioMidiaCommand(nome, mime));

        resultado.IsValid.ShouldBeFalse();
        resultado.Errors.ShouldContain(e => e.PropertyName == "ContentType");
    }

    [Theory]
    [InlineData("Abertura da Loja.MP4", "video/mp4")]
    [InlineData("promo.v2.webm", "VIDEO/WEBM")]
    [InlineData("logo.png", "image/png; charset=binary")]
    [InlineData("jingle.m4a", "audio/mp4")]
    public void PedirEnvio_FormatoDaTabela_Aceito(string nome, string mime) =>
        new PedirEnvioMidiaCommandValidator().Validate(new PedirEnvioMidiaCommand(nome, mime)).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("", 10)]
    [InlineData("   ", 10)]
    [InlineData("Abertura", 0)]
    [InlineData("Abertura", -5)]
    [InlineData("Abertura", Midia.DuracaoMaximaSegundos + 1)]
    public void CriarMidia_DescricaoVaziaOuDuracaoInvalida_Recusada(string descricao, int duracao) =>
        new CreateMidiaCommandValidator().Validate(new CreateMidiaCommand(Guid.NewGuid(), descricao, duracao)).IsValid.ShouldBeFalse();

    [Fact]
    public void CriarMidia_LimitesDaDuracao_Aceitos()
    {
        var validador = new CreateMidiaCommandValidator();
        validador.Validate(new CreateMidiaCommand(Guid.NewGuid(), "Abertura", 1)).IsValid.ShouldBeTrue();
        validador.Validate(new CreateMidiaCommand(Guid.NewGuid(), "Abertura", Midia.DuracaoMaximaSegundos)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Avaliar_ObjetoAusenteTipoVazioETeto()
    {
        var envio = Envio(TipoMidia.Video, "video/mp4");

        ArmazenamentoMidia.Avaliar(envio, null, Teto).ShouldBe(DesfechoConferencia.NaoRecebido);
        ArmazenamentoMidia.Avaliar(envio, Objeto(10, "image/png"), Teto).ShouldBe(DesfechoConferencia.RecusadoTipo);
        ArmazenamentoMidia.Avaliar(envio, Objeto(0, "video/mp4"), Teto).ShouldBe(DesfechoConferencia.RecusadoVazio);
        ArmazenamentoMidia.Avaliar(envio, Objeto(Teto + 1, "video/mp4"), Teto).ShouldBe(DesfechoConferencia.RecusadoTeto);
        ArmazenamentoMidia.Avaliar(envio, Objeto(Teto, "VIDEO/MP4; codecs=avc1"), Teto).ShouldBe(DesfechoConferencia.Aceito);
    }

    [Fact]
    public void TrocarArquivo_OutroTipoOuOutraUnidade_Recusado_EVersaoSobeACadaEscrita()
    {
        var unidade = Guid.CreateVersion7();
        var midia = Midia.Criar(Envio(TipoMidia.Video, "video/mp4", unidade), "Abertura", 30);

        Should.Throw<ArgumentException>(() => midia.TrocarArquivo(Envio(TipoMidia.Imagem, "image/png", unidade)));
        Should.Throw<ArgumentException>(() => midia.TrocarArquivo(Envio(TipoMidia.Video, "video/mp4", Guid.CreateVersion7())));
        midia.Versao.ShouldBe(0);

        var novo = Envio(TipoMidia.Video, "video/mp4", unidade);
        midia.TrocarArquivo(novo);
        midia.Editar("Abertura nova", 45);
        midia.Desativar();
        midia.Ativar();

        (midia.EnvioId, midia.ChaveObjeto, midia.Versao).ShouldBe((novo.Id, novo.ChaveObjeto, 4));
    }

    [Fact]
    public async Task ProvedorQueIgnoraOToken_AindaCortadoPeloPrazo()
    {
        var nuncaResponde = new TaskCompletionSource<StoredObjectMetadata?>();
        var storage = Substitute.For<FSH.Framework.Storage.Services.IStorageService>();
        storage.HeadObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(nuncaResponde.Task);
        var armazenamento = new ArmazenamentoMidia(storage, Options.Create(new OpcoesMidia { PrazoArmazenamento = TimeSpan.FromMilliseconds(200) }));

        var erro = await Should.ThrowAsync<CustomException>(() => armazenamento.ConferirAsync(Envio(TipoMidia.Imagem, "image/png"), default));

        erro.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    private static EnvioMidia Envio(TipoMidia tipo, string mime) => Envio(tipo, mime, Guid.CreateVersion7());

    private static EnvioMidia Envio(TipoMidia tipo, string mime, Guid unidade)
    {
        var formato = FormatosMidia.Aceitos.First(f => f.Tipo == tipo);
        var chave = ChaveObjetoMidia.NovoEnvio("tenant-m03", unidade, formato);
        return EnvioMidia.Criar(chave.EnvioId, unidade, chave.Chave, tipo, mime);
    }

    private static StoredObjectMetadata Objeto(long tamanho, string mime) => new(tamanho, mime, DateTimeOffset.UtcNow, ETag: null);
}
