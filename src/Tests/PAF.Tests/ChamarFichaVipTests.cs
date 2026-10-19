using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

namespace PAF.Tests;

/// <summary>RF19 VIP (D02): the printed senha is read as every letter/number split, never a guessed one.</summary>
public sealed class ChamarFichaVipTests
{
    private static readonly ChamarFichaVipCommandValidator Validador = new();
    private static readonly DateOnly Dia = new(2026, 10, 19);

    [Fact]
    public void Leituras_LetraSimples_UmaLeitura()
    {
        ChamarFichaVipCommandHandler.Leituras("A12").ShouldBe([("A", 12), ("A1", 2)]);
    }

    [Fact]
    public void Leituras_LetraDeCincoCaracteres_NumeroLongo()
    {
        ChamarFichaVipCommandHandler.Leituras("ABCDE1234567").ShouldBe([("ABCDE", 1234567)]);
    }

    [Fact]
    public void Leituras_LetraComDigito_TodasAsDivisoes()
    {
        ChamarFichaVipCommandHandler.Leituras("A1B23").ShouldBe([("A1B", 23), ("A1B2", 3)]);
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
        ChamarFichaVipCommandHandler.Leituras(senha).ShouldBeEmpty();
    }

    [Fact]
    public void Leituras_NumeroAlemDoInt_SoAsDivisoesQueCabem()
    {
        // 11 digits overflow an int with a one-character letter; only the splits whose number fits remain.
        var leituras = ChamarFichaVipCommandHandler.Leituras("A99999999999");
        leituras.ShouldNotContain(l => l.Letra == "A" || l.Letra == "A9");
        leituras.ShouldContain(("A99", 999999999));
    }

    [Fact]
    public void Leituras_ZeroNoMeio_Aceito()
    {
        ChamarFichaVipCommandHandler.Leituras("A10").ShouldBe([("A", 10)]);
    }

    [Fact]
    public void Validador_PedidoCompleto_Valido()
    {
        Validador.Validate(new ChamarFichaVipCommand("A1", Dia, Guid.NewGuid())).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validador_CamposVaziosOuLongos_Invalidos()
    {
        var erros = Validador.Validate(new ChamarFichaVipCommand("  ", default, Guid.Empty)).Errors.Select(e => e.PropertyName).ToList();
        erros.ShouldBe(["Senha", "Data", "FilaId"], ignoreOrder: true);

        Validador.Validate(new ChamarFichaVipCommand(new string('A', ChamarFichaVipCommandValidator.SenhaMaxLength + 1), Dia, Guid.NewGuid()))
            .IsValid.ShouldBeFalse();
        Validador.Validate(new ChamarFichaVipCommand($" {new string('A', ChamarFichaVipCommandValidator.SenhaMaxLength)} ", Dia, Guid.NewGuid()))
            .IsValid.ShouldBeTrue("padding is trimmed before the length check");
    }
}
