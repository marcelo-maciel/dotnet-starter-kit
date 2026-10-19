using System.Text.RegularExpressions;
using FSH.Modules.PAF.Midias;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PAF.Tests;

/// <summary>
/// M02: the RF03 format table, the media object key (ADR 0007: new key per upload, outside <c>public/</c>, nothing
/// from the uploaded name) and the <c>PAF:Midia</c> options with the kit prefix guard.
/// </summary>
public sealed partial class MidiaChaveEFormatosTests
{
    private static readonly Guid Unidade = Guid.Parse("0199f0a1-2b3c-7d4e-8f90-a1b2c3d4e5f6");

    [GeneratedRegex(@"\Apaf-midia/[A-Za-z0-9_-]+/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{32}\.(mp4|webm|jpg|jpeg|png|webp|gif|mp3|m4a)\z")]
    private static partial Regex FormaDaChave();

    [Fact]
    public void DoisEnvios_GeramChavesDiferentes_ComIdV7()
    {
        var formato = FormatosMidia.PorNomeDeArquivo("institucional.mp4")!;

        var primeiro = ChaveObjetoMidia.NovoEnvio("cliente-a", Unidade, formato);
        var segundo = ChaveObjetoMidia.NovoEnvio("cliente-a", Unidade, formato);

        primeiro.Chave.ShouldNotBe(segundo.Chave);
        primeiro.EnvioId.ShouldNotBe(segundo.EnvioId);
        primeiro.EnvioId.Version.ShouldBe(7);
        primeiro.Chave.ShouldBe($"paf-midia/cliente-a/{Unidade:D}/{primeiro.EnvioId:N}.mp4");
    }

    [Fact]
    public void ChaveFicaForaDaRaizPublica()
    {
        var chave = ChaveObjetoMidia.NovoEnvio("public", Unidade, FormatosMidia.Aceitos[0]).Chave;

        chave.ShouldStartWith("paf-midia/");
        chave.ShouldNotStartWith("public/");
    }

    [Theory]
    [InlineData("VIDEO.MP4", ".mp4")]
    [InlineData("foto.JpEg", ".jpeg")]
    [InlineData("../../etc/passwd.Mp4", ".mp4")]
    [InlineData("vídeo de abertura 😀.mp4", ".mp4")]
    [InlineData("C:\\pasta\\audio.M4A", ".m4a")]
    public void NomeHostil_SoDeixaAExtensaoDaTabelaNaChave(string nome, string extensao)
    {
        var formato = FormatosMidia.PorNomeDeArquivo(nome);

        formato.ShouldNotBeNull();
        formato.Extensao.ShouldBe(extensao);
        var chave = ChaveObjetoMidia.NovoEnvio("cliente-a", Unidade, formato).Chave;
        chave.ShouldMatch(FormaDaChave().ToString());
        chave.ShouldEndWith(extensao);
    }

    [Theory]
    [InlineData("sem-extensao")]
    [InlineData("filme.avi")]
    [InlineData("filme.mov")]
    [InlineData("filme.flv")]
    [InlineData("filme.mp4.exe")]
    [InlineData("filme.mp4 ")]
    [InlineData("filme.mp4/../x")]
    [InlineData("filme.mp😀4")]
    [InlineData("filme.mpé4")]
    [InlineData("filme.ｍｐ4")]
    [InlineData(".")]
    [InlineData("")]
    public void ExtensaoForaDaTabela_EhRecusada(string nome)
    {
        FormatosMidia.PorNomeDeArquivo(nome).ShouldBeNull();
    }

    [Theory]
    [InlineData("../outro")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("tenánt")]
    [InlineData("com espaço")]
    [InlineData("emoji😀")]
    [InlineData("root\n")]
    [InlineData("-comeca-com-hifen")]
    [InlineData("")]
    public void TenantForaDoFormato_EhRecusado(string tenant)
    {
        Should.Throw<ArgumentException>(() => ChaveObjetoMidia.NovoEnvio(tenant, Unidade, FormatosMidia.Aceitos[0]));
    }

    [Fact]
    public void UnidadeVazia_EhRecusada()
    {
        Should.Throw<ArgumentException>(() => ChaveObjetoMidia.NovoEnvio("cliente-a", Guid.Empty, FormatosMidia.Aceitos[0]));
    }

    [Fact]
    public void TabelaDoRf03_TemOsNoveFormatosETiposCertos()
    {
        FormatosMidia.Aceitos.Select(f => f.Extensao).ShouldBe([".mp4", ".webm", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".mp3", ".m4a"]);
        FormatosMidia.Aceitos.Where(f => f.Tipo == TipoMidia.Video).Select(f => f.Mime).ShouldBe(["video/mp4", "video/webm"]);
        FormatosMidia.Aceitos.Where(f => f.Tipo == TipoMidia.Audio).Select(f => f.Mime).ShouldBe(["audio/mpeg", "audio/mp4"]);
        FormatosMidia.Aceitos.ShouldAllBe(f => !f.Extensao.Any(char.IsUpper));
        FormatosMidia.ListaAceitos.ShouldBe(".mp4, .webm, .jpg, .jpeg, .png, .webp, .gif, .mp3, .m4a");
    }

    [Fact]
    public void Opcoes_SemConfiguracao_UsamOsValoresIniciais()
    {
        var opcoes = Resolver([]);

        opcoes.ValidadeUrlEnvio.ShouldBe(TimeSpan.FromMinutes(15));
        opcoes.MargemLeitura.ShouldBe(TimeSpan.FromMinutes(10));
        opcoes.TetoBytes(TipoMidia.Video).ShouldBe(500L * 1024 * 1024);
        opcoes.TetoBytes(TipoMidia.Imagem).ShouldBe(20L * 1024 * 1024);
        opcoes.TetoBytes(TipoMidia.Audio).ShouldBe(50L * 1024 * 1024);
    }

    [Fact]
    public void Opcoes_ValoresVemDaSecaoPafMidia()
    {
        var opcoes = Resolver(new()
        {
            ["PAF:Midia:ValidadeUrlEnvio"] = "00:05:00",
            ["PAF:Midia:TetoVideoBytes"] = "1073741824",
            ["Storage:S3:Prefix"] = "app",
        });

        opcoes.ValidadeUrlEnvio.ShouldBe(TimeSpan.FromMinutes(5));
        opcoes.TetoBytes(TipoMidia.Video).ShouldBe(1073741824L);
    }

    [Theory]
    [InlineData("PAF:Midia:ValidadeUrlEnvio", "00:00:00", "ValidadeUrlEnvio")]
    [InlineData("PAF:Midia:ValidadeUrlEnvio", "7.00:00:01", "ValidadeUrlEnvio")]
    [InlineData("PAF:Midia:TetoVideoBytes", "0", "tetos por tipo")]
    [InlineData("PAF:Midia:TetoAudioBytes", "-1", "tetos por tipo")]
    [InlineData("PAF:Midia:MargemLeitura", "-00:00:01", "MargemLeitura")]
    [InlineData("PAF:Midia:MargemLeitura", "7.00:00:00", "MargemLeitura")]
    [InlineData("Storage:S3:Prefix", "paf", "Storage:S3:Prefix")]
    [InlineData("Storage:S3:Prefix", "PAF-midia/x", "Storage:S3:Prefix")]
    [InlineData("Storage:S3:Prefix", "public", "Storage:S3:Prefix")]
    [InlineData("Storage:S3:Prefix", "Public/app/", "Storage:S3:Prefix")]
    public void ValorForaDaFaixa_FalhaNaInicializacao(string chave, string valor, string mensagem)
    {
        using var servicos = Montar(new() { [chave] = valor });

        var erro = Should.Throw<OptionsValidationException>(() => servicos.GetRequiredService<IStartupValidator>().Validate());

        erro.Message.ShouldContain(mensagem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/")]
    [InlineData("app")]
    [InlineData("app/")]
    [InlineData("private")]
    [InlineData("public-assets")]
    public void PrefixoDoKitQueNaoColide_EhAceito(string? prefixo)
    {
        OpcoesMidia.PrefixoDoKitCompativel(prefixo).ShouldBeTrue();
    }

    private static OpcoesMidia Resolver(Dictionary<string, string?> valores)
    {
        using var servicos = Montar(valores);
        servicos.GetRequiredService<IStartupValidator>().Validate();
        return servicos.GetRequiredService<IOptions<OpcoesMidia>>().Value;
    }

    private static ServiceProvider Montar(Dictionary<string, string?> valores)
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var servicos = new ServiceCollection();
        servicos.AddSingleton<IConfiguration>(configuracao);
        OpcoesMidia.Registrar(servicos);
        return servicos.BuildServiceProvider();
    }
}
