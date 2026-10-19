using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

namespace PAF.Tests;

/// <summary>RF19 VIP (D02): the printed senha is read as every letter/number split, never a guessed one.</summary>
public sealed class ChamarFichaVipTests
{
    private static readonly ListFichasVipQueryValidator Validador = new();
    private static readonly DateOnly Dia = new(2026, 10, 19);

    [Fact]
    public void Leituras_LetraSimples_UmaLeitura()
    {
        ListFichasVipQueryHandler.Leituras("A12").ShouldBe([("A", 12), ("A1", 2)]);
    }

    [Fact]
    public void Leituras_LetraDeCincoCaracteres_NumeroLongo()
    {
        ListFichasVipQueryHandler.Leituras("ABCDE1234567").ShouldBe([("ABCDE", 1234567)]);
    }

    [Fact]
    public void Leituras_LetraComDigito_TodasAsDivisoes()
    {
        ListFichasVipQueryHandler.Leituras("A1B23").ShouldBe([("A1B", 23), ("A1B2", 3)]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("7")]
    [InlineData("A0")]
    [InlineData("ABCDEF1")]
    [InlineData("A١")]
    public void Leituras_SemNumeroValido_Nenhuma(string senha)
    {
        // Zero padding, a letter past five characters and non-ASCII digits never match a ficha.
        ListFichasVipQueryHandler.Leituras(senha).ShouldBeEmpty();
    }

    [Fact]
    public void Leituras_NumeroAlemDoInt_SoAsDivisoesQueCabem()
    {
        // 11 digits overflow an int with a one-character letter; only the splits whose number fits remain.
        var leituras = ListFichasVipQueryHandler.Leituras("A99999999999");
        leituras.ShouldNotContain(l => l.Letra == "A" || l.Letra == "A9");
        leituras.ShouldContain(("A99", 999999999));
    }

    [Fact]
    public void Leituras_ZeroNoMeio_Aceito()
    {
        ListFichasVipQueryHandler.Leituras("A10").ShouldBe([("A", 10)]);
    }

    [Fact]
    public void Validador_BuscaCompleta_Valida()
    {
        Validador.Validate(new ListFichasVipQuery { Senha = "A1", Data = Dia }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validador_BuscaVaziaOuLonga_Invalida()
    {
        var erros = Validador.Validate(new ListFichasVipQuery { Senha = "  ", Data = null }).Errors.Select(e => e.PropertyName).ToList();
        erros.ShouldBe(["Senha", "Data"], ignoreOrder: true);
        Validador.Validate(new ListFichasVipQuery { Senha = null, Data = default(DateOnly) }).Errors.Select(e => e.PropertyName)
            .ShouldBe(["Senha", "Data"], ignoreOrder: true);

        Validador.Validate(new ListFichasVipQuery { Senha = new string('A', ListFichasVipQueryValidator.SenhaMaxLength + 1), Data = Dia })
            .IsValid.ShouldBeFalse();
        Validador.Validate(new ListFichasVipQuery { Senha = $" {new string('A', ListFichasVipQueryValidator.SenhaMaxLength)} ", Data = Dia })
            .IsValid.ShouldBeTrue("padding is trimmed before the length check");
    }

    [Fact]
    public void ValidadorDaChamada_IdsVazios_Invalidos()
    {
        var chamada = new ChamarFichaVipCommandValidator();
        chamada.Validate(new ChamarFichaVipCommand(Guid.NewGuid(), Guid.NewGuid())).IsValid.ShouldBeTrue();
        chamada.Validate(new ChamarFichaVipCommand(Guid.Empty, Guid.Empty)).Errors.Select(e => e.PropertyName)
            .ShouldBe(["FichaId", "FilaId"], ignoreOrder: true);
    }
}
