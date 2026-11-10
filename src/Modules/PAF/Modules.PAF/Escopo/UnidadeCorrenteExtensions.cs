using System.Net;
using FSH.Framework.Core.Exceptions;

namespace FSH.Modules.PAF.Escopo;

public static class UnidadeCorrenteExtensions
{
    /// <summary>
    /// The current unidade for a unit-scoped write. Writes take the unidade from here and never from the
    /// request body, so a caller cannot write into a unidade outside its scope.
    /// </summary>
    public static Guid Exigir(this IUnidadeCorrente unidadeCorrente)
    {
        ArgumentNullException.ThrowIfNull(unidadeCorrente);
        return unidadeCorrente.UnidadeId
            ?? throw new CustomException(
                $"Unidade corrente não definida: selecione a unidade da sessão ou, sem sessão do PAF, informe o cabeçalho {UnidadeCorrenteMiddleware.Cabecalho}.",
                (IEnumerable<string>?)null,
                HttpStatusCode.BadRequest);
    }
}
