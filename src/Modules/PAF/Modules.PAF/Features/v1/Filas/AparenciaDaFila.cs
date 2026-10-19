using System.Net;
using FluentValidation;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Aparencia;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas;

/// <summary>
/// Appearance and setor of a fila (RF02). The font color is the text of every button of the fila (selected fila and each
/// prioridade), so each button color is checked against it with the RNF19 floor. The pair spans the fila and its
/// <c>FilaPrioridades</c> rows, so the check runs in the handlers, over the committed side the command does not carry.
/// ponytail: two concurrent edits (font color and a button color) each pass against the other's old value; re-saving
/// either side re-checks the pair. Upgrade: lock the fila row in the four handlers.
/// </summary>
internal static class AparenciaDaFila
{
    public const int FonteMaxLength = 64;

    public static IRuleBuilderOptions<T, string?> CorOpcional<T>(this IRuleBuilder<T, string?> regra) =>
        regra.Must(c => c is null || ContrasteWcag.FormatoValido(c)).WithMessage(ContrasteWcag.MensagemFormato);


    /// <summary>400 naming every button color below the floor, or set without a font color to measure against.</summary>
    public static void ExigirContraste(string? corFonte, IEnumerable<(string Botao, string? Cor)> botoes)
    {
        var comCor = botoes.Where(b => b.Cor is not null).ToList();
        if (comCor.Count == 0)
        {
            return;
        }

        if (corFonte is null)
        {
            throw new CustomException(
                "Informe a cor da fonte da fila para usar cor de botão.",
                comCor.Select(b => $"O botão {b.Botao} tem cor e a fila não tem cor de fonte para medir o contraste."),
                HttpStatusCode.BadRequest);
        }

        var abaixo = comCor
            .Select(b => (b.Botao, b.Cor, Razao: ContrasteWcag.Razao(corFonte, b.Cor!)))
            .Where(b => b.Razao < ContrasteWcag.RazaoMinima)
            .ToList();
        if (abaixo.Count > 0)
        {
            throw new CustomException(
                "Contraste insuficiente entre a cor da fonte e a cor do botão (mínimo 4,5:1).",
                abaixo.Select(b => $"Botão {b.Botao}: {corFonte} sobre {b.Cor} dá {b.Razao:0.00}:1."),
                HttpStatusCode.BadRequest);
        }
    }

    /// <summary>Under the unit filter, a setor of another unidade does not exist here: 404.</summary>
    public static async Task ExigirSetorAsync(PafDbContext db, Guid? setorId, CancellationToken cancellationToken)
    {
        if (setorId is not { } id)
        {
            return;
        }

        bool existe = await db.Setores.AsNoTracking().AnyAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        if (!existe)
        {
            throw new NotFoundException($"Setor {id} não encontrado nesta unidade.");
        }
    }

    /// <summary>Button colors already stored in the fila's grid, labeled by prioridade, optionally skipping one line.</summary>
    public static async Task<List<(string Botao, string? Cor)>> CoresDaGradeAsync(
        PafDbContext db, Guid filaId, Guid? excetoPrioridadeId, CancellationToken cancellationToken)
    {
        var linhas = await db.FilaPrioridades.AsNoTracking()
            .Where(fp => fp.FilaId == filaId && fp.CorBotao != null && (excetoPrioridadeId == null || fp.PrioridadeId != excetoPrioridadeId.Value))
            .Join(db.Prioridades.AsNoTracking(), fp => fp.PrioridadeId, p => p.Id, (fp, p) => new { p.Nome, fp.CorBotao })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return linhas.Select(l => (l.Nome, l.CorBotao)).ToList();
    }
}

/// <summary>Format only; the contrast pairs are <see cref="AparenciaDaFila.ExigirContraste"/>.</summary>
internal sealed class AparenciaFilaValidator : AbstractValidator<AparenciaFilaDto>
{
    public AparenciaFilaValidator()
    {
        RuleFor(x => x.Fonte).MaximumLength(AparenciaDaFila.FonteMaxLength);
        RuleFor(x => x.CorFonte).CorOpcional();
        RuleFor(x => x.CorBotaoSelecionado).CorOpcional();
    }
}
