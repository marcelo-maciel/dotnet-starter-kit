using FSH.Modules.PAF.Contracts.v1;
using Mediator;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Cadastro side of D28: every <see cref="IEscritaDeCadastro"/> command under the Codefi's impersonation notes its
/// <c>AtoImpersonacao</c> before the handler runs, so the handler's own <c>SaveChanges</c> writes it in the same
/// transaction as the change. The guichê writes (raw SQL in <c>OperacaoGuiche</c>) call
/// <see cref="AtosImpersonacao.GravarAsync"/> themselves instead.
/// </summary>
public sealed class AtoImpersonacaoBehavior<TMessage, TResponse>(AtosImpersonacao atos)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage, IEscritaDeCadastro
{
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        atos.Anotar(atendimentoId: null);
        var resposta = await next(message, cancellationToken).ConfigureAwait(false);

        // A marked handler that wrote around the PAF context (ExecuteUpdate, raw SQL without GravarAsync) or cleared
        // the tracker would leave an impersonated write with no act: fail loudly so the matrix gap shows in tests.
        if (atos.Pendente)
        {
            throw new InvalidOperationException(
                $"{typeof(TMessage).Name} is marked IEscritaDeCadastro but its AtoImpersonacao was not saved with the write.");
        }

        return resposta;
    }
}
