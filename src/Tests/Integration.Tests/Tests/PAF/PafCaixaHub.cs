using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>Collects what one hub method delivers to a connection, and waits for an item matching a criterion.</summary>
internal sealed class Caixa<T> : IDisposable
{
    private readonly ConcurrentQueue<T> _itens = new();
    private readonly SemaphoreSlim _sinal = new(0);
    private readonly IDisposable _inscricao;

    public Caixa(HubConnection conexao, string metodo)
    {
        _inscricao = conexao.On<T>(metodo, item =>
        {
            _itens.Enqueue(item);
            _sinal.Release();
        });
    }

    public IReadOnlyList<T> Recebidos => [.. _itens];

    public async Task<T?> EsperarAsync(Func<T, bool> criterio, TimeSpan limite)
    {
        var prazo = DateTime.UtcNow + limite;
        while (true)
        {
            var achado = _itens.FirstOrDefault(criterio);
            if (achado is not null)
            {
                return achado;
            }

            var resta = prazo - DateTime.UtcNow;
            if (resta <= TimeSpan.Zero || !await _sinal.WaitAsync(resta))
            {
                return _itens.FirstOrDefault(criterio);
            }
        }
    }

    public void Dispose()
    {
        _inscricao.Dispose();
        _sinal.Dispose();
    }
}
