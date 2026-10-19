using FSH.Modules.PAF.Domain;

namespace PAF.Tests;

public sealed class MidiaDominioTests
{
    private static readonly Guid Unidade = Guid.CreateVersion7();
    private static readonly DateOnly Dia = new(2026, 10, 18);

    private static EnvioMidia Envio() => EnvioMidia.Criar(Guid.CreateVersion7(), Unidade, "k.mp4", TipoMidia.Video, "video/mp4");

    // The schedule reader tests a day by shifting 1 by DayOfWeek; a bit out of that order shows the wrong days.
    [Theory]
    [InlineData(DayOfWeek.Sunday, DiasSemana.Domingo)]
    [InlineData(DayOfWeek.Monday, DiasSemana.Segunda)]
    [InlineData(DayOfWeek.Tuesday, DiasSemana.Terca)]
    [InlineData(DayOfWeek.Wednesday, DiasSemana.Quarta)]
    [InlineData(DayOfWeek.Thursday, DiasSemana.Quinta)]
    [InlineData(DayOfWeek.Friday, DiasSemana.Sexta)]
    [InlineData(DayOfWeek.Saturday, DiasSemana.Sabado)]
    public void DiasSemana_BitDeCadaDia_SegueDayOfWeek(DayOfWeek dia, DiasSemana esperado)
    {
        ((DiasSemana)(1 << (int)dia)).ShouldBe(esperado);
    }

    [Fact]
    public void DiasSemana_Todos_EhAUniaoDosSeteDias()
    {
        var uniao = Enum.GetValues<DayOfWeek>().Aggregate(DiasSemana.None, (acc, d) => acc | (DiasSemana)(1 << (int)d));
        uniao.ShouldBe(DiasSemana.Todos);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Midia.DuracaoMaximaSegundos + 1)]
    public void Midia_DuracaoForaDoIntervalo_Recusa(int duracao)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Midia.Criar(Envio(), "Vídeo", duracao));
    }

    [Fact]
    public void Midia_DescricaoVazia_Recusa()
    {
        Should.Throw<ArgumentException>(() => Midia.Criar(Envio(), " ", 10));
    }

    [Fact]
    public void Midia_CopiaUnidadeTipoEnvioEChaveDoEnvio()
    {
        var envio = EnvioMidia.Criar(Guid.CreateVersion7(), Unidade, "k.png", TipoMidia.Imagem, "image/png");
        var midia = Midia.Criar(envio, "Logo", 5);
        (midia.UnidadeId, midia.Tipo, midia.EnvioId, midia.ChaveObjeto).ShouldBe((Unidade, TipoMidia.Imagem, envio.Id, "k.png"));
    }

    [Fact]
    public void EnvioMidia_IdVazio_Recusa()
    {
        Should.Throw<ArgumentException>(() => EnvioMidia.Criar(Guid.Empty, Unidade, "k.mp4", TipoMidia.Video, "video/mp4"));
    }

    [Theory]
    [InlineData(1, 1, 127)]
    [InlineData(0, 0, 127)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 1, 128)]
    public void ProgramacaoMidia_ForaDasRegras_Recusa(int diasAntesDoFinal, int repeticoes, int dias)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ProgramacaoMidia.Criar(
            Unidade, Guid.CreateVersion7(), "Manhã", Dia.AddDays(diasAntesDoFinal), Dia, new TimeOnly(9, 0), repeticoes, (DiasSemana)dias));
    }

    [Fact]
    public void ProgramacaoMidia_NosLimites_Aceita()
    {
        var p = ProgramacaoMidia.Criar(Unidade, Guid.CreateVersion7(), "Manhã", Dia, Dia, new TimeOnly(0, 0), 1, DiasSemana.Domingo);
        p.Ativa.ShouldBeTrue();
        p.Dias.ShouldBe(DiasSemana.Domingo);
    }
}
