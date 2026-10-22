using FSH.Modules.PAF.Sessao;
using StackExchange.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// B4 da revisão da onda 0: o Valkey no ar mas sem atender comandos responde com erro de servidor, não com queda de
/// conexão. <c>BUSY</c> (um script longo passou do busy-reply-threshold) e <c>LOADING</c> (reinício carregando o AOF)
/// seguem o mesmo caminho no cliente; aqui o <c>BUSY</c> é o que dá para provocar de forma determinística. O conjunto de
/// licenças precisa cair no modo degradado, como numa queda, e não devolver 500 no login.
/// </summary>
[Collection(PafHostValkeyColecao.Nome)]
public sealed class PafValkeyOcupadoTests(PafHostValkey host)
{
    // Valkey's default busy-reply-threshold is 5 s: from then on every other client gets BUSY.
    private static readonly TimeSpan AlemDoLimiteDeScript = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task ValkeyOcupadoPorScript_RespondeBusy_ConjuntoDeLicencasCaiNoModoDegradado()
    {
        var principal = host.Services.GetServices<IConnectionMultiplexer>().First();
        var conjunto = host.Services.GetRequiredService<ConjuntoLicencas>();
        await using var travadora = await ConnectionMultiplexer.ConnectAsync(principal.Configuration);
        // Never yields; its own client gives up at the 500 ms asyncTimeout while the server keeps running it.
        var script = travadora.GetDatabase().ScriptEvaluateAsync("while true do end");
        try
        {
            await Task.Delay(AlemDoLimiteDeScript);
            (await conjunto.ReconstruidoAsync(CancellationToken.None)).ShouldBeNull("BUSY do Valkey é indisponível, não erro do script");
        }
        finally
        {
            // From another connection: the busy server still accepts SCRIPT KILL.
            await principal.GetServer(principal.GetEndPoints()[0]).ExecuteAsync("SCRIPT", "KILL");
            await Should.ThrowAsync<RedisTimeoutException>(script);
            // The degraded mode asked for a rebuild: leave the host as the next test expects it.
            (await host.Services.GetRequiredService<ReconstrucaoLicencas>().ReconstruirAsync(CancellationToken.None)).ShouldBeTrue();
        }
    }
}
