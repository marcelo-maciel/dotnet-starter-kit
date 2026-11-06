using System.Globalization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

/// <summary>
/// The VIP search (RF19, owner decision 19/10/2026): every ficha of the current unit printed with this senha on this
/// operational date, oldest emission first, for the attendant to pick. Unit-filtered reads, so another unit's or
/// tenant's ficha never appears; nothing found is an empty list, never 404. Same guichê requirement as the VIP filas.
/// </summary>
public sealed class ListFichasVipQueryHandler(PafDbContext db, SessaoCorrente sessao, ConcessaoCorrente concessao)
    : IQueryHandler<ListFichasVipQuery, IReadOnlyList<FichaVipDto>>
{
    public async ValueTask<IReadOnlyList<FichaVipDto>> Handle(ListFichasVipQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await OperacaoGuiche.ExigirAsync(db, DonoPosto.Exigir(sessao, concessao), cancellationToken);
        var leituras = Leituras(Normalizar(query.Senha!));
        var letras = leituras.Select(l => l.Letra).ToList();
        var numeros = leituras.Select(l => l.Numero).ToList();
        var data = query.Data!.Value;
        // The two lists cross in SQL (a few rows of one unit and day); the exact pair is kept here.
        var candidatas = await (
                from f in db.Fichas.AsNoTracking()
                where f.DataOperacional == data && letras.Contains(f.Letra) && numeros.Contains(f.Numero)
                join fila in db.Filas.AsNoTracking() on f.FilaId equals fila.Id
                join prioridade in db.Prioridades.AsNoTracking() on f.PrioridadeId equals prioridade.Id
                orderby f.CriadoEm, f.Id
                select new { f.Id, f.Letra, f.Numero, f.CriadoEm, Fila = fila.Nome, Prioridade = prioridade.Nome })
            .ToListAsync(cancellationToken);
        return candidatas
            .Where(f => leituras.Contains((f.Letra, f.Numero)))
            .Select(f => new FichaVipDto(f.Id, OperacaoGuiche.Senha(f.Letra, f.Numero), f.CriadoEm, f.Fila, f.Prioridade))
            .ToList();
    }

    // Same normalization as the letter at the cadastro (LetraDaSenha), so a lower-case or padded entry finds the senha.
    private static string Normalizar(string senha) => senha.Trim().ToUpperInvariant();

    /// <summary>
    /// Every way to read the senha as a letter (1 to LetraMaxLength characters) followed by the printed number (digits,
    /// no zero padding, seção 4.1). A letter may itself hold digits, so the split is never guessed: all readings are
    /// tried and every matching ficha is listed.
    /// </summary>
    public static IReadOnlyList<(string Letra, int Numero)> Leituras(string senha)
    {
        ArgumentNullException.ThrowIfNull(senha);
        var leituras = new List<(string, int)>();
        for (var tamanho = 1; tamanho <= Math.Min(FilaPrioridade.LetraMaxLength, senha.Length - 1); tamanho++)
        {
            var digitos = senha[tamanho..];
            if (digitos[0] != '0' && digitos.All(char.IsAsciiDigit)
                && int.TryParse(digitos, NumberStyles.None, CultureInfo.InvariantCulture, out var numero))
            {
                leituras.Add((senha[..tamanho], numero));
            }
        }

        return leituras;
    }
}
