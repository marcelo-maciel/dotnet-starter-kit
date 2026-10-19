using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF04 (F17): the guichê operations act only on an atendimento of the user who called it. A colleague linked to the
/// guichê of someone else's pendência (link written outside the selection, which would refuse it) gets 409 on every
/// operation, with nothing written; the owner resuming the guichê operates normally.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafDonoAtendimentoGuicheTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaSelecao = "/api/v1/paf/sessao/guiche";

    private readonly PafTenantCliente _tenant = new(factory, "paf-dono");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task Colega_NoGuicheDaPendenciaChamada_Recebe409SemEscrita_EDonoRetoma()
    {
        var (c, dono, primeira, chamada) = await ChamadaDoDonoNoGuicheAAsync();
        primeira.Dispose();
        await DeixarPendenteAsync(dono);
        var antes = await _op.AtendimentoAsync(c, chamada.AtendimentoId);
        var (colega, sessaoColega) = await ColegaLigadoAoGuicheAAsync(c);
        using (colega)
        {
            foreach (var acao in new[] { "rechamar", "iniciar", "cancelar" })
            {
                await RecusadoPorOutroUsuarioAsync(colega, chamada.AtendimentoId, acao);
            }

            using var chamar = await PafOperacao.ChamarAsync(colega);
            chamar.StatusCode.ShouldBe(HttpStatusCode.Conflict, "a pendência de outro usuário segura o guichê");
            PafSessaoLicencaTests.Detalhe(await chamar.Content.ReadAsStringAsync()).ShouldBe(ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        var depois = await _op.AtendimentoAsync(c, chamada.AtendimentoId);
        depois.Estado.ShouldBe(EstadoAtendimento.Chamado);
        depois.Versao.ShouldBe(antes.Versao, "nenhuma operação recusada grava no atendimento");
        depois.UltimaRechamadaEm.ShouldBe(antes.UltimaRechamadaEm);
        depois.IniciadoEm.ShouldBeNull();
        depois.CanceladoEm.ShouldBeNull();
        depois.UsuarioId.ShouldBe(antes.UsuarioId);
        (await _op.ContarAsync(c, db => db.Rechamadas.Where(r => r.AtendimentoId == chamada.AtendimentoId))).ShouldBe(0);
        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.UnidadeId == c.UnidadeId))).ShouldBe(1, "a chamada recusada não cria atendimento");

        await SoltarVinculoAsync(sessaoColega);
        using var donoDeNovo = await DonoNoGuicheAAsync(dono, c);
        (await PafOperacao.AgirOkAsync(donoDeNovo, chamada.AtendimentoId, "rechamar")).Versao.ShouldBe(antes.Versao + 1);
        (await PafOperacao.AgirOkAsync(donoDeNovo, chamada.AtendimentoId, "iniciar")).Estado.ShouldBe(nameof(EstadoAtendimento.EmAtendimento));
        (await PafOperacao.AgirOkAsync(donoDeNovo, chamada.AtendimentoId, "encerrar")).Estado.ShouldBe(nameof(EstadoAtendimento.Encerrado));
    }

    [Fact]
    public async Task Colega_NoGuicheDaPendenciaEmAtendimento_Recebe409SemEscrita_EDonoEncerra()
    {
        var (c, dono, primeira, chamada) = await ChamadaDoDonoNoGuicheAAsync();
        using (primeira)
        {
            await PafOperacao.AgirOkAsync(primeira, chamada.AtendimentoId, "iniciar");
        }

        await DeixarPendenteAsync(dono);
        var antes = await _op.AtendimentoAsync(c, chamada.AtendimentoId);
        var (colega, sessaoColega) = await ColegaLigadoAoGuicheAAsync(c);
        using (colega)
        {
            await RecusadoPorOutroUsuarioAsync(colega, chamada.AtendimentoId, "encerrar");
            await RecusadoPorOutroUsuarioAsync(colega, chamada.AtendimentoId, "cancelar");

            // Forwarding enters through the same lock, so it is refused the same way and creates no destination.
            using var encaminhar = await colega.PostAsJsonAsync(
                PafOperacao.Rota(chamada.AtendimentoId, "encaminhar"), new EncaminharAtendimentoRequest([c.Protocolo], c.Normal));
            encaminhar.StatusCode.ShouldBe(HttpStatusCode.Conflict, await encaminhar.Content.ReadAsStringAsync());
            PafSessaoLicencaTests.Detalhe(await encaminhar.Content.ReadAsStringAsync()).ShouldBe(MensagensGuiche.MensagemDeOutroUsuario);
        }

        var depois = await _op.AtendimentoAsync(c, chamada.AtendimentoId);
        depois.Estado.ShouldBe(EstadoAtendimento.EmAtendimento);
        depois.Versao.ShouldBe(antes.Versao, "nenhuma operação recusada grava no atendimento");
        depois.EncerradoEm.ShouldBeNull();
        depois.CanceladoEm.ShouldBeNull();
        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.FichaId == antes.FichaId))).ShouldBe(1, "nenhum destino criado");

        await SoltarVinculoAsync(sessaoColega);
        using var donoDeNovo = await DonoNoGuicheAAsync(dono, c);
        (await PafOperacao.AgirOkAsync(donoDeNovo, chamada.AtendimentoId, "encerrar")).Versao.ShouldBe(antes.Versao + 1);
        (await _op.AtendimentoAsync(c, chamada.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Encerrado);
    }

    // The selection keeps working on another user's pendência: liberarPendencia cancels it through the ficha lock,
    // not through the guichê's owner-checked one.
    [Fact]
    public async Task Selecao_LiberandoPendenciaDeOutroUsuario_ContinuaCancelando()
    {
        var (c, dono, primeira, chamada) = await ChamadaDoDonoNoGuicheAAsync();
        primeira.Dispose();
        await DeixarPendenteAsync(dono);
        using var colega = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);

        using var selecao = await colega.PostAsJsonAsync(RotaSelecao, new { estacaoId = c.GuicheA, liberarPendencia = true });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        (await _op.AtendimentoAsync(c, chamada.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Cancelado);
    }

    private static async Task RecusadoPorOutroUsuarioAsync(HttpClient colega, Guid atendimentoId, string acao)
    {
        using var resposta = await PafOperacao.AgirAsync(colega, atendimentoId, acao);
        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, $"{acao}: {await resposta.Content.ReadAsStringAsync()}");
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(MensagensGuiche.MensagemDeOutroUsuario, acao);
    }

    // The owner calls a ficha on guichê A; the caller keeps the client to act on it before leaving it pending.
    private async Task<(CenarioOperacao Cenario, SeededUser Dono, HttpClient Primeira, ChamadaDto Chamada)> ChamadaDoDonoNoGuicheAAsync()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var dono = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var primeira = await DonoNoGuicheAAsync(dono, c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        return (c, dono, primeira, await PafOperacao.ChamarOkAsync(primeira));
    }

    // A new login releases the old session's link (single session); the atendimento stays on the guichê as a pendência.
    private async Task DeixarPendenteAsync(SeededUser dono) => await _tenant.EntrarAsync(dono);

    private async Task<HttpClient> DonoNoGuicheAAsync(SeededUser dono, CenarioOperacao c)
    {
        var cliente = _tenant.Cliente((await _tenant.EntrarAsync(dono)).AccessToken);
        using var selecao = await cliente.PostAsJsonAsync(RotaSelecao, new { estacaoId = c.GuicheA, liberarPendencia = false });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        return cliente;
    }

    // A link the selection would refuse, written straight into the table: the only way to put another user's
    // session on the guichê of a pendência, which is the state the server-side check must hold against.
    private async Task<(HttpClient Colega, Guid Sessao)> ColegaLigadoAoGuicheAAsync(CenarioOperacao c)
    {
        var token = (await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken;
        var sessao = PafTenantCliente.SessaoDoToken(token);
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            db.SessoesEstacao.Add(SessaoEstacao.CriarParaSessao(c.GuicheA, sessao));
            await db.SaveChangesAsync();
        }

        return (_tenant.Cliente(token), sessao);
    }

    private Task<int> SoltarVinculoAsync(Guid sessao) =>
        _tenant.ExecutarAsync($"""UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now() WHERE "SessaoId" = {sessao} AND "LiberadoEm" IS NULL""");
}
