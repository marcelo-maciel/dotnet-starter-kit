using System.Runtime.CompilerServices;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Usuarios;
using FSH.Modules.PAF.Provisionamento;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Cadastro de usuários do PAF (RF08, RF22, C12): lista e operações limitadas ao escopo de quem administra, criação
/// com compensação, reordenação sem mexer em sessão, retirada de unidade e desativação com a oferta de liberar os
/// atendimentos em curso, e cópia de perfil.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafUsuariosTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Perfil = $"{TestConstants.IdentityBasePath}/profile";
    private const string Senha = "Test@1234!";
    private readonly PafTenantCliente _tenant = new(factory, "paf-usr");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AdminDaFilial_NaoVeNemEditaDesativaOuCopiaORegional()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var regional = await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        var colega = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);

        var vistos = await PafApi.OkAsync<List<UsuarioPafDto>>(filial.Cliente, HttpMethod.Get, "/usuarios", null, null);
        vistos.Select(u => u.Id).ShouldContain(Guid.Parse(colega.UserId));
        vistos.Select(u => u.Id).ShouldNotContain(Guid.Parse(regional.UserId));
        vistos.ShouldNotContain(u => u.EscopoEmpresa, "o admin do tenant, de escopo empresa, não cabe no escopo da filial");

        (await StatusAsync(filial.Cliente, HttpMethod.Put, $"/usuarios/{regional.UserId}", new UpdateUsuarioPafRequest("Outro", "Nome", null, null)))
            .ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(filial.Cliente, HttpMethod.Post, $"/usuarios/{regional.UserId}/desativar", new DesativarUsuarioPafRequest(true)))
            .ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(filial.Cliente, HttpMethod.Post, $"/usuarios/{colega.UserId}/copiar-perfil",
            new CopiarPerfilUsuarioPafRequest(Guid.Parse(regional.UserId), false))).ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(filial.Cliente, HttpMethod.Post, $"/usuarios/{regional.UserId}/copiar-perfil",
            new CopiarPerfilUsuarioPafRequest(Guid.Parse(colega.UserId), false))).ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(filial.Cliente, HttpMethod.Get, $"/usuarios/{regional.UserId}/atendimentos-em-curso", null))
            .ShouldBe(HttpStatusCode.Forbidden);

        // Nothing moved: the regional keeps his name, his session-capable account and both unidades.
        (await _tenant.LoginAsync(regional.Email, regional.Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UnidadesAsync(Guid.Parse(regional.UserId))).ShouldBe([u1.UnidadeId, u2.UnidadeId]);
        (await KitAsync(Guid.Parse(regional.UserId))).FirstName.ShouldBe("Seeded");
    }

    [Fact]
    public async Task AdminDoTenant_CriaUsuario_LoginEntraECadastroCompleto()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var email = NovoEmail();

        var id = await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, "/usuarios", null, new CreateUsuarioPafCommand(
            "Maria", "Silva", email, Senha, Senha, " Caixa ", "2010", PapeisPadrao.Atendente, [u2.UnidadeId, u1.UnidadeId], false));

        var criado = (await PafApi.OkAsync<List<UsuarioPafDto>>(_tenant.Admin, HttpMethod.Get, "/usuarios", null, null)).Single(u => u.Id == id);
        criado.ShouldBe(criado with
        {
            Nome = "Maria", Sobrenome = "Silva", Email = email, Ativo = true, Funcao = "Caixa", Ramal = "2010",
            EscopoEmpresa = false,
        });
        criado.Unidades.ShouldBe([u2.UnidadeId, u1.UnidadeId]);
        criado.Papeis.ShouldBe([PapeisPadrao.Atendente]);

        var login = await _tenant.LoginAsync(email, Senha);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        (await _tenant.AtividadesAsync(id)).Single().UnidadeId.ShouldBe(u2.UnidadeId, "a principal é a primeira da lista");

        // Same e-mail again: 409 before any kit write.
        (await StatusAsync(_tenant.Admin, HttpMethod.Post, "/usuarios", new CreateUsuarioPafCommand(
            "Outra", "Pessoa", email, Senha, Senha, null, null, PapeisPadrao.Atendente, [u1.UnidadeId], false)))
            .ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Criar_EntradaInvalidaOuPapelAcimaDoChamador_RecusaSemCriarUsuarioNoKit()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);

        var senhasDiferentes = NovoEmail();
        (await StatusAsync(_tenant.Admin, HttpMethod.Post, "/usuarios", new CreateUsuarioPafCommand(
            "A", "B", senhasDiferentes, Senha, "Outra@1234!", null, null, PapeisPadrao.Atendente, [u1.UnidadeId], false)))
            .ShouldBe(HttpStatusCode.BadRequest);
        var papelDoKit = NovoEmail();
        (await StatusAsync(_tenant.Admin, HttpMethod.Post, "/usuarios", new CreateUsuarioPafCommand(
            "A", "B", papelDoKit, Senha, Senha, null, null, "Admin", [u1.UnidadeId], false)))
            .ShouldBe(HttpStatusCode.BadRequest);

        // The filial admin holds the user permissions plus the basic ones every user has (Atendente is within his
        // reach); Gerente would hand out FichasDoDia and the read-only cadastros he does not have.
        var acima = NovoEmail();
        (await StatusAsync(filial.Cliente, HttpMethod.Post, "/usuarios", new CreateUsuarioPafCommand(
            "A", "B", acima, Senha, Senha, null, null, PapeisPadrao.Gerente, [u1.UnidadeId], false)))
            .ShouldBe(HttpStatusCode.Forbidden);
        // Escopo empresa is the tenant admin's alone.
        var empresa = NovoEmail();
        (await StatusAsync(filial.Cliente, HttpMethod.Post, "/usuarios", new CreateUsuarioPafCommand(
            "A", "B", empresa, Senha, Senha, null, null, PapeisPadrao.Atendente, [u1.UnidadeId], true)))
            .ShouldBe(HttpStatusCode.Forbidden);

        // A unidade outside his scope is refused; inside it, the basic role passes.
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var foraDoEscopo = NovoEmail();
        (await StatusAsync(filial.Cliente, HttpMethod.Post, "/usuarios", new CreateUsuarioPafCommand(
            "A", "B", foraDoEscopo, Senha, Senha, null, null, PapeisPadrao.Atendente, [u1.UnidadeId, u2.UnidadeId], false)))
            .ShouldBe(HttpStatusCode.Forbidden);
        var permitido = NovoEmail();
        var criado = await PafApi.OkAsync<Guid>(filial.Cliente, HttpMethod.Post, "/usuarios", null, new CreateUsuarioPafCommand(
            "A", "B", permitido, Senha, Senha, null, null, PapeisPadrao.Atendente, [u1.UnidadeId], false));
        (await UnidadesAsync(criado)).ShouldBe([u1.UnidadeId]);

        foreach (var email in new[] { senhasDiferentes, papelDoKit, acima, empresa, foraDoEscopo })
        {
            (await ExisteNoKitAsync(email)).ShouldBeFalse(email);
        }
    }

    [Fact]
    public async Task FalhaDepoisDoRegister_NaoDeixaUsuarioComLoginAtivoNemEscopo()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var marcador = $"falha-c12-{Guid.NewGuid():N}";
        var sufixo = Guid.NewGuid().ToString("N")[..12];
        // A real PostgreSQL failure on the PAF profile row, the first PAF write after RegisterAsync, only for this
        // test's marker, so the parallel tests of the collection never meet it.
        await DdlAsync($"""
            CREATE FUNCTION public.c12_falha_{sufixo}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."Funcao" = '{marcador}' THEN RAISE EXCEPTION 'falha simulada do cadastro'; END IF;
              RETURN NEW;
            END $$
            """);
        await DdlAsync($"""
            CREATE TRIGGER c12_falha_{sufixo} BEFORE INSERT ON paf."PerfisUsuario"
               FOR EACH ROW EXECUTE FUNCTION public.c12_falha_{sufixo}()
            """);
        var email = NovoEmail();
        try
        {
            var resposta = await PafApi.EnviarAsync(_tenant.Admin, HttpMethod.Post, "/usuarios", null, new CreateUsuarioPafCommand(
                "Falha", "Simulada", email, Senha, Senha, marcador, null, PapeisPadrao.Atendente, [u1.UnidadeId], false));
            resposta.IsSuccessStatusCode.ShouldBeFalse(await resposta.Content.ReadAsStringAsync());
        }
        finally
        {
            await DdlAsync($"""DROP TRIGGER c12_falha_{sufixo} ON paf."PerfisUsuario" """);
            await DdlAsync($"DROP FUNCTION public.c12_falha_{sufixo}()");
        }

        (await ExisteNoKitAsync(email)).ShouldBeTrue("o RegisterAsync passou antes da falha");
        var orfao = await KitPorEmailAsync(email);
        orfao.IsActive.ShouldBeFalse("a compensação desativa o usuário");
        orfao.EmailConfirmed.ShouldBeFalse("a confirmação é o último passo");
        (await UnidadesAsync(Guid.Parse(orfao.Id!))).ShouldBeEmpty();
        (await _tenant.LoginAsync(email, Senha)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reordenar_AtualizaOrdemComIdsPreservadosESemMexerNaSessao()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        var token = await _tenant.EntrarAsync(usuario);
        using var cliente = _tenant.Cliente(token.AccessToken);
        var idsAntes = await LinhasAsync(usuarioId);
        var atividadeAntes = (await _tenant.AtividadesAsync(usuarioId)).Single();

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{usuarioId}/unidades", null,
            new SetUsuarioUnidadesCommand(usuarioId, [u2.UnidadeId, u1.UnidadeId], false));

        var depois = await LinhasAsync(usuarioId);
        depois.ShouldBe([(idsAntes[1].Id, u2.UnidadeId, 0), (idsAntes[0].Id, u1.UnidadeId, 1)]);
        var atividade = (await _tenant.AtividadesAsync(usuarioId)).Single();
        (atividade.SessaoId, atividade.UnidadeId).ShouldBe((atividadeAntes.SessaoId, u1.UnidadeId), "a nova ordem vale no próximo login");
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RetirarUnidadeDaSessaoViva_Revoga()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u2.UnidadeId, u1.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{usuarioId}/unidades", null,
            new SetUsuarioUnidadesCommand(usuarioId, [u1.UnidadeId], false));

        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _tenant.AtividadesAsync(usuarioId)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetirarUnidadeComAtendimentoChamado_CancelaSoQuandoPedido(bool cancelar)
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        var emU2 = await ChamadoPorAsync(u2, usuarioId);
        var emU1 = await ChamadoPorAsync(u1, usuarioId);

        var ofertados = await PafApi.OkAsync<List<AtendimentoEmCursoDoUsuarioDto>>(
            _tenant.Admin, HttpMethod.Get, $"/usuarios/{usuarioId}/atendimentos-em-curso", null, null);
        ofertados.Select(a => a.AtendimentoId).Order().ShouldBe(new[] { emU1, emU2 }.Order());

        // The admin operates in the initial unidade: the cancel in U2 is a cross-unit path.
        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{usuarioId}/unidades", null,
            new SetUsuarioUnidadesCommand(usuarioId, [u1.UnidadeId], false) { CancelarAtendimentosEmCurso = cancelar });

        var (estado, versao) = await EstadoAsync(u2.UnidadeId, emU2);
        estado.ShouldBe(cancelar ? EstadoAtendimento.Cancelado : EstadoAtendimento.Chamado);
        versao.ShouldBe(cancelar ? 2 : 1);
        (await EstadoAsync(u1.UnidadeId, emU1)).Estado.ShouldBe(EstadoAtendimento.Chamado, "U1 continua no escopo");
    }

    /// <summary>
    /// RF08: U2 taken out of the user without cancelling leaves his Chamado there. The user now fits in a filial admin
    /// of [U1], who must still not see (nor be offered to cancel) the atendimento of U2, a unidade he does not hold.
    /// </summary>
    [Fact]
    public async Task UnidadeRetiradaSemCancelar_AdminDaFilialNaoVeOAtendimentoDeForaDoEscopoDele()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        var emU2 = await ChamadoPorAsync(u2, usuarioId);
        var emU1 = await ChamadoPorAsync(u1, usuarioId);
        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{usuarioId}/unidades", null,
            new SetUsuarioUnidadesCommand(usuarioId, [u1.UnidadeId], false) { CancelarAtendimentosEmCurso = false });
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);

        var vistos = await PafApi.OkAsync<List<AtendimentoEmCursoDoUsuarioDto>>(
            filial.Cliente, HttpMethod.Get, $"/usuarios/{usuarioId}/atendimentos-em-curso", null, null);

        vistos.Select(a => a.AtendimentoId).ShouldBe([emU1], "a senha de U2 não é do escopo da filial");
        var doTenant = await PafApi.OkAsync<List<AtendimentoEmCursoDoUsuarioDto>>(
            _tenant.Admin, HttpMethod.Get, $"/usuarios/{usuarioId}/atendimentos-em-curso", null, null);
        doTenant.Select(a => a.AtendimentoId).Order().ShouldBe(new[] { emU1, emU2 }.Order(), "o admin do tenant vê todas as unidades");
    }

    [Fact]
    public async Task Desativar_RecusaProximoLoginDerrubaSessoesEConexoesECancelaEmCurso()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        var token = await _tenant.EntrarAsync(usuario);
        using var cliente = _tenant.Cliente(token.AccessToken);
        var conexao = await new PafOperacao(factory, _tenant).ConectarAsync(token.AccessToken);
        var fechou = Fechamento(conexao);
        var emU2 = await ChamadoPorAsync(u2, usuarioId);

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, $"/usuarios/{usuarioId}/desativar", null, new DesativarUsuarioPafRequest(true));

        (await Task.WhenAny(fechou, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBe(fechou, "a conexão do PafHub cai no mesmo ato");
        await conexao.DisposeAsync();
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessoesAtivasAsync(usuarioId)).ShouldBe(0);
        (await _tenant.AtividadesAsync(usuarioId)).ShouldBeEmpty();
        (await _tenant.LoginAsync(usuario.Email, usuario.Password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await EstadoAsync(u2.UnidadeId, emU2)).Estado.ShouldBe(EstadoAtendimento.Cancelado);
        (await KitAsync(usuarioId)).IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Desativar_SemCancelar_AtendimentoFica()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        var emU1 = await ChamadoPorAsync(u1, usuarioId);

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, $"/usuarios/{usuarioId}/desativar", null, new DesativarUsuarioPafRequest(false));

        (await EstadoAsync(u1.UnidadeId, emU1)).Estado.ShouldBe(EstadoAtendimento.Chamado);
        (await KitAsync(usuarioId)).IsActive.ShouldBeFalse();
    }

    /// <summary>
    /// Pins a kit limit (reported in C12): the kit's deactivation accepts only a kit Admin actor, and PAF roles carry
    /// no kit role, so a filial admin passes the PAF reach check and is then refused by the kit, with nothing changed.
    /// </summary>
    [Fact]
    public async Task Desativar_PorAdminDaFilialSemPapelAdminDoKit_KitRecusaSemEfeito()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var colega = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);

        (await StatusAsync(filial.Cliente, HttpMethod.Post, $"/usuarios/{colega.UserId}/desativar", new DesativarUsuarioPafRequest(false)))
            .ShouldBe(HttpStatusCode.Forbidden);

        (await KitAsync(Guid.Parse(colega.UserId))).IsActive.ShouldBeTrue();
        (await _tenant.LoginAsync(colega.Email, colega.Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Atualizar_NomeFuncaoERamal()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{usuarioId}", null,
            new UpdateUsuarioPafRequest("Joana", "Souza", "Gerente de loja", "33"));
        var editado = (await PafApi.OkAsync<List<UsuarioPafDto>>(_tenant.Admin, HttpMethod.Get, "/usuarios", null, null)).Single(u => u.Id == usuarioId);
        (editado.Nome, editado.Sobrenome, editado.Funcao, editado.Ramal).ShouldBe(("Joana", "Souza", "Gerente de loja", "33"));

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{usuarioId}", null, new UpdateUsuarioPafRequest("Joana", "Souza", " ", null));
        editado = (await PafApi.OkAsync<List<UsuarioPafDto>>(_tenant.Admin, HttpMethod.Get, "/usuarios", null, null)).Single(u => u.Id == usuarioId);
        (editado.Funcao, editado.Ramal).ShouldBe((null, null));
        (await StatusAsync(_tenant.Admin, HttpMethod.Put, $"/usuarios/{usuarioId}", new UpdateUsuarioPafRequest("", "Souza", null, null)))
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CopiarPerfil_CopiaPapeisEEscopoNaOrdem()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var origem = await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, "/usuarios", null, new CreateUsuarioPafCommand(
            "Origem", "Perfil", NovoEmail(), Senha, Senha, null, null, PapeisPadrao.Gerente, [u2.UnidadeId, u1.UnidadeId], false));
        var destino = await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, "/usuarios", null, new CreateUsuarioPafCommand(
            "Destino", "Perfil", NovoEmail(), Senha, Senha, null, null, PapeisPadrao.Atendente, [u1.UnidadeId], false));

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, $"/usuarios/{destino}/copiar-perfil", null,
            new CopiarPerfilUsuarioPafRequest(origem, false));

        var lista = await PafApi.OkAsync<List<UsuarioPafDto>>(_tenant.Admin, HttpMethod.Get, "/usuarios", null, null);
        var copiado = lista.Single(u => u.Id == destino);
        copiado.Unidades.ShouldBe([u2.UnidadeId, u1.UnidadeId]);
        copiado.Papeis.ShouldBe([PapeisPadrao.Gerente], "o papel anterior do PAF sai");
        copiado.Funcao.ShouldBeNull("função e ramal são da pessoa, não do perfil");
        (await StatusAsync(_tenant.Admin, HttpMethod.Post, $"/usuarios/{destino}/copiar-perfil", new CopiarPerfilUsuarioPafRequest(destino, false)))
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private sealed record AdminDaFilial(HttpClient Cliente) : IDisposable
    {
        public void Dispose() => Cliente.Dispose();
    }

    // A filial admin as RF08 describes him: scope [unidade] and the user permissions only, no kit Admin role.
    private async Task<AdminDaFilial> AdminDaFilialAsync(Guid unidade)
    {
        var admin = await _tenant.CriarUsuarioAsync(unidade);
        await _tenant.DarPapelAsync(admin.UserId, PafPermissions.Usuarios.View, PafPermissions.Usuarios.Manage,
            PafPermissions.UsuariosUnidade.View, PafPermissions.UsuariosUnidade.Update);
        return new AdminDaFilial(_tenant.Cliente((await _tenant.EntrarAsync(admin)).AccessToken));
    }

    private static string NovoEmail() => $"c12-{Guid.NewGuid().ToString("N")[..10]}@cliente.test";

    private static async Task<HttpStatusCode> StatusAsync(HttpClient cliente, HttpMethod metodo, string rota, object? corpo)
    {
        using var resposta = await PafApi.EnviarAsync(cliente, metodo, rota, null, corpo);
        return resposta.StatusCode;
    }

    private static Task Fechamento(HubConnection conexao)
    {
        var fechou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conexao.Closed += _ =>
        {
            fechou.TrySetResult();
            return Task.CompletedTask;
        };
        return fechou.Task;
    }

    private Task<int> DdlAsync(string sql) => _tenant.ExecutarAsync(FormattableStringFactory.Create(sql));

    /// <summary>An atendimento Chamado on guichê A of the unidade by the given user, in a fila of its own.</summary>
    private async Task<Guid> ChamadoPorAsync(UnidadeTeste unidade, Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidade.UnidadeId);
        const string letra = "A";
        var fila = Fila.Criar(unidade.UnidadeId, $"Fila {Guid.NewGuid():N}"[..20], "Fila", setorId: null);
        var prioridade = Prioridade.Criar(unidade.UnidadeId, $"Normal {Guid.NewGuid():N}"[..20], valor: 0);
        var ficha = Ficha.Criar(unidade.UnidadeId, fila.Id, prioridade.Id, letra, 1, CanalEmissao.Totem, unidade.GuicheA, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(unidade.UnidadeId, ficha.Id, fila.Id, prioridade.Id, atendimentoOrigemId: null);
        db.Filas.Add(fila);
        db.Prioridades.Add(prioridade);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        (await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Chamado', "EstacaoId" = {unidade.GuicheA}, "UsuarioId" = {usuarioId}, "ChamadoEm" = now(), "Versao" = "Versao" + 1
             WHERE "Id" = {atendimento.Id}
            """)).ShouldBe(1);
        return atendimento.Id;
    }

    private async Task<(EstadoAtendimento Estado, int Versao)> EstadoAsync(Guid unidade, Guid atendimentoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidade);
        var linha = await db.Atendimentos.AsNoTracking().SingleAsync(a => a.Id == atendimentoId);
        return (linha.Estado, linha.Versao);
    }

    private async Task<List<Guid>> UnidadesAsync(Guid usuarioId) =>
        [.. (await LinhasAsync(usuarioId)).Select(l => l.UnidadeId)];

    private async Task<List<(Guid Id, Guid UnidadeId, int Ordem)>> LinhasAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var linhas = await db.UsuariosUnidade.AsNoTracking().Where(u => u.UsuarioId == usuarioId).OrderBy(u => u.Ordem).ToListAsync();
        return [.. linhas.Select(l => (l.Id, l.UnidadeId, l.Ordem))];
    }

    private async Task<FSH.Modules.Identity.Contracts.DTOs.UserDto> KitAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        return await scope.ServiceProvider.GetRequiredService<IUserProfileService>().GetAsync(usuarioId.ToString(), CancellationToken.None);
    }

    private async Task<FSH.Modules.Identity.Contracts.DTOs.UserDto> KitPorEmailAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        return (await scope.ServiceProvider.GetRequiredService<IUserProfileService>().GetListAsync(CancellationToken.None)).Single(u => u.Email == email);
    }

    private async Task<bool> ExisteNoKitAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        return await scope.ServiceProvider.GetRequiredService<IUserProfileService>().ExistsWithEmailAsync(email, null, CancellationToken.None);
    }

    private async Task<int> SessoesAtivasAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        return (await scope.ServiceProvider.GetRequiredService<ISessionService>().GetUserSessionsForAdminAsync(usuarioId.ToString(), CancellationToken.None))
            .Count(s => s.IsActive);
    }
}
