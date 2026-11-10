using System.Diagnostics;
using FSH.Modules.PAF.TempoReal;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Revogação de uma sessão cujo registro no Valkey ainda lista uma conexão que nenhum servidor segura: a instância
/// dela morreu por SIGKILL e a entrada só vence depois da validade, ou a conexão fechou e a saída do registro ainda
/// não rodou. Pelo backplane, a saída de grupo de uma conexão remota espera a confirmação da instância que a segura,
/// que não vem. Medido em homologação em 21/10/2026: o login que revoga a sessão anterior levou 29 s e a entrada
/// ficava no registro, repetindo a espera na revogação seguinte.
/// </summary>
[Collection(PafHostValkeyColecao.Nome)]
public sealed class PafConexaoSemDonoTests(PafHostValkey host)
{
    // A live instance confirms a group action in milliseconds; the login waits on this path.
    private static readonly TimeSpan TetoRevogacao = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task RevogarSessaoComConexaoSemDono_TerminaDentroDoTeto_EEsqueceOsGruposDela()
    {
        var tenantId = $"paf-sd-{Guid.NewGuid().ToString("N")[..8]}";
        var sessaoId = Guid.NewGuid();
        var semDono = $"sem-dono-{Guid.NewGuid():N}";
        var registro = host.Services.GetRequiredService<RegistroConexoes>();
        var conexoes = host.Services.GetRequiredService<ConexoesPaf>();
        (await registro.RegistrarAsync(semDono, new ConexaoPessoa(tenantId, "usuario-1", false, sessaoId, null), CancellationToken.None))
            .ShouldBeTrue("o Valkey do host precisa estar no ar");
        (await registro.AdicionarGrupoAsync(tenantId, semDono, $"{tenantId}:painel", CancellationToken.None)).ShouldBeTrue();
        (await registro.AdicionarGrupoAsync(tenantId, semDono, $"{tenantId}:guiche", CancellationToken.None)).ShouldBeTrue();

        var relogio = Stopwatch.StartNew();
        var alcancadas = await conexoes.EncerrarSessaoAsync(tenantId, sessaoId, "teste", CancellationToken.None);
        relogio.Stop();

        alcancadas.ShouldBe(1);
        relogio.Elapsed.ShouldBeLessThan(TetoRevogacao);
        (await registro.GruposAsync(tenantId, semDono, CancellationToken.None)).ShouldBeEmpty();
    }

    // EntrarNoGuiche and the guichê selection move every connection of the session: a connection left by a dead
    // instance must not hold the operator's screen for the backplane's ack timer.
    [Fact]
    public async Task MoverSessaoParaGuicheEParaUnidade_ComConexaoSemDono_TerminaDentroDoTeto()
    {
        var tenantId = $"paf-sd-{Guid.NewGuid().ToString("N")[..8]}";
        var sessaoId = Guid.NewGuid();
        var semDono = $"sem-dono-{Guid.NewGuid():N}";
        var registro = host.Services.GetRequiredService<RegistroConexoes>();
        var conexoes = host.Services.GetRequiredService<ConexoesPaf>();
        (await registro.RegistrarAsync(semDono, new ConexaoPessoa(tenantId, "usuario-1", false, sessaoId, null), CancellationToken.None))
            .ShouldBeTrue("o Valkey do host precisa estar no ar");

        var relogio = Stopwatch.StartNew();
        await conexoes.MoverSessaoParaGuicheAsync(tenantId, sessaoId, Guid.NewGuid(), CancellationToken.None);
        relogio.Elapsed.ShouldBeLessThan(TetoRevogacao);

        relogio.Restart();
        (await conexoes.MoverSessaoParaUnidadeAsync(tenantId, sessaoId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).ShouldBe(1);
        relogio.Elapsed.ShouldBeLessThan(TetoRevogacao);
        (await registro.GruposAsync(tenantId, semDono, CancellationToken.None)).ShouldBeEmpty();
    }
}
