# Implantação do PAF

Arquivos novos do PAF fora de `src/`. Não são edição do kit: entram na lista à parte proposta pela decisão D22 do plano da fatia 2, ao lado do ADR 0008.

## Imagem da API (O01, RNF10)

A imagem da API é publicada pelo SDK do .NET, sobre a família `noble` que o `.csproj` do host já declara (Ubuntu completo, com `tzdata`):

```bash
deploy/paf/publicar-imagem-api.sh <tag>   # gera paf-api:<tag> no Docker local
```

Nunca usar `src/Host/FSH.Starter.Api/Dockerfile` do kit para o PAF. Ele roda sobre `aspnet:10.0-noble-chiseled`, que não tem `/usr/share/zoneinfo` (conferido em 17/10/2026 com `docker export | tar t`: zero arquivos de zona na chiseled, presentes na `noble` e na `noble-chiseled-extra`). Sem o arquivo, `PafRelogio` falha na inicialização estática ao resolver `America/Sao_Paulo` e toda emissão responde 500.

O fuso do contêiner não vai embutido na imagem. Contrato com o compose do PAF (O02): todo contêiner da API e do DbMigrator recebe `TZ=America/Sao_Paulo` por variável de ambiente. A imagem do DbMigrator segue o mesmo caminho (SDK, `ContainerFamily` `noble` já no `.csproj`), nunca o Dockerfile chiseled dele.

## Smoke do fuso

```bash
deploy/paf/smoke-fuso.sh paf-api:<tag>
```

Sobe PostgreSQL e Valkey efêmeros numa rede própria, aplica as migrações e o `seed-demo` pelo DbMigrator no host, sobe a API da imagem com `TZ=America/Sao_Paulo` e confere:

- arquivo de zona `America/Sao_Paulo` presente na imagem;
- `date +%Z` dentro do contêiner igual a `-03`;
- emissão no totem com status 200 e `EmitidaEm` terminando em `-03:00`;
- `GET painel/estado` com status 200.

Código de saída 0 só com tudo verde. Remove contêineres e rede ao sair. Precisa de Docker, SDK do .NET e Node 18 ou maior.

Prova negativa (o smoke pega o defeito): a mesma suíte contra a imagem chiseled tem de sair com código diferente de zero.

```bash
dotnet publish src/Host/FSH.Starter.Api/FSH.Starter.Api.csproj -c Release --os linux --arch x64 \
  -t:PublishContainer -p:NuGetAudit=false -p:ContainerRepository=paf-api \
  -p:ContainerImageTags=chiseled \
  -p:ContainerBaseImage=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
deploy/paf/smoke-fuso.sh paf-api:chiseled   # esperado: SMOKE-FUSO-IMAGEM FALHOU, emissão 500
```

A base precisa ir explícita em `ContainerBaseImage`. Com `-p:ContainerFamily=noble-chiseled` o SDK troca sozinho para `noble-chiseled-extra` (a aplicação não é `InvariantGlobalization`), que tem `tzdata` e passa no smoke: não serve como prova negativa.

## Compose por ambiente (O02, RNF02, RNF16, RNF17)

`compose.yml` é um arquivo só para os dois ambientes, e cada ambiente é um projeto do compose. Nada tem nome fixo: sem `name:` no topo, sem `container_name`, e volumes e rede levam o prefixo de `COMPOSE_PROJECT_NAME`, que vem do arquivo do ambiente. Por isso o `down` de um projeto não toca no outro.

```bash
cp deploy/paf/ambientes/producao.env.exemplo deploy/paf/ambientes/producao.env        # fora do git (*.env)
cp deploy/paf/ambientes/homologacao.env.exemplo deploy/paf/ambientes/homologacao.env
deploy/paf/publicar-imagem-api.sh <tag>
deploy/paf/publicar-imagem-migrator.sh <tag>
deploy/paf/verificar-ambiente.sh deploy/paf/ambientes/producao.env deploy/paf/ambientes/homologacao.env
docker compose -f deploy/paf/compose.yml --env-file deploy/paf/ambientes/producao.env up -d
```

O arquivo do ambiente é ao mesmo tempo o `--env-file` do compose e o `env_file` da API e do DbMigrator, então o que o verificador confere é exatamente o que o host .NET lê. Segredos sem `$`: o compose interpola o caractere e corta o valor; o verificador reprova o aviso.

O que cada projeto sobe: PostgreSQL com `timezone=America/Sao_Paulo` na instância, Valkey próprio com senha e AOF (nunca compartilhado entre ambientes, ARQ l.367), o DbMigrator (`apply --seed`), `api-a` e `api-b` nos dois ambientes (o deploy troca uma de cada vez e o atendimento não para), o dashboard e o admin do kit, o Caddy e o `cloudflared` (seção "Túnel e Caddy"). Nenhum serviço fica atrás de perfil. Toda porta publicada fica só em `127.0.0.1`, para o `deploy.sh` conferir saúde e para o `pg_dump` do host; Caddy e `cloudflared` não publicam porta nenhuma. A rede tem subnet fixa por ambiente (`PAF_SUBNET`, ARQ l.380), com uma faixa dinâmica (`PAF_SUBNET_DINAMICA`, a metade de cima) de onde saem os endereços de todos os contêineres menos o Caddy, que tem IP fixo fora dela (`PAF_IP_CADDY`). A API confia só nesse IP (`TrustedProxyOptions:KnownProxies`), e o Caddy confia só na faixa dinâmica, onde fica o `cloudflared`; o gateway da ponte, por onde chega qualquer processo do host, não está em nenhum dos dois. As subnets `172.31.10.0/24` e `172.31.20.0/24` dos exemplos precisam ser conferidas contra as redes que já existem na VPS (`docker network inspect`) antes do primeiro `up`; uma rede já criada com outra configuração de IPAM precisa ser recriada (`down` e `up`) para ganhar a faixa dinâmica.

Prazo de inatividade (D04): `dashboard/config.json.template` é montado por volume sobre o template do kit na imagem do dashboard, com `inactivityIdleMs` vindo de `PAF_INATIVIDADE_MS` (8 horas nos exemplos, o mesmo prazo para todas as telas). A imagem e o entrypoint do kit não mudam.

### verificar-ambiente.sh

Renderiza os dois ambientes com `docker compose config` (todos os perfis ligados) e confere o modelo resolvido. Sai com código diferente de zero se faltar qualquer invariante:

- quotas do kit desligadas explicitamente (`QuotaOptions__Enabled=false`, ARQ l.384);
- `JwtOptions:SigningKey` com 32 caracteres ou mais, só ASCII imprimível, diferente do exemplo e diferente entre os ambientes;
- `AccessTokenMinutes` explícito e igual nos dois ambientes;
- `KnownNetworks` vazio e `KnownProxies` igual ao IP fixo do Caddy; faixa dinâmica dentro da subnet e sem o gateway; IP do Caddy dentro da subnet, fora da faixa dinâmica e diferente do gateway; o Caddy confiando só na faixa dinâmica; subnets sem sobreposição;
- Valkey e PostgreSQL do próprio projeto, com as senhas do arquivo; nomes de projeto, portas e buckets do R2 diferentes;
- `x-paf-unidade` no CORS sem perder nenhum cabeçalho de `appsettings.Production.json`;
- prazos explícitos de PostgreSQL (`Timeout`, `Command Timeout`) e Valkey (`connectTimeout`, `syncTimeout`, `asyncTimeout`) até o teto de 30 s do teste de O19;
- `TZ=America/Sao_Paulo` na API, no DbMigrator e no PostgreSQL; `Storage:Provider=s3` (R2, ADR 0007);
- nenhum valor de exemplo (`<...>`), nenhum `container_name`, nenhum serviço atrás de perfil, toda porta em `127.0.0.1`, Valkey sem porta publicada.
- túnel e Caddy (O03): Caddy sem porta, com o `caddy/Caddyfile` montado só leitura e as três URLs públicas em `https://<host>`; `cloudflared` com 2 réplicas e o `cloudflared/config.yml` montado só leitura; `PAF_TUNEL_ID` em formato UUID; `PAF_ARQUIVO_TUNEL` apontando para o `ambientes/<ambiente>.tunel.env` do próprio ambiente, com `TUNNEL_CRED_CONTENTS` um JSON com `AccountTag`, `TunnelSecret` e `TunnelID` igual a `PAF_TUNEL_ID`; a credencial do túnel ausente do ambiente da API e do migrator; túneis diferentes entre os ambientes.
- observabilidade (O10, RNF06): `OTEL_EXPORTER_OTLP_ENDPOINT` em https, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, `OTEL_EXPORTER_OTLP_HEADERS` com `api-key=`, `OTEL_EXPORTER_OTLP_TIMEOUT` até 30 s, `OTEL_SERVICE_NAME` diferente entre os ambientes, o sink OTLP de `appsettings.Production.json` desligado pelo índice (`Serilog__WriteTo__1__Name=`, senão cada log vai duas vezes) e o console em JSON compacto.

Num host que ainda roda só a homologação (antes de existir a produção), o caminho do `producao.env` é passado mesmo sem o arquivo existir: as conferências da produção e todas as cruzadas saem como `SKIPPED`, nunca como `PASS`, e a última linha é `VERIFICAR-AMBIENTE OK (conferências cruzadas SKIPPED)`. Só a produção pode faltar: sem o `homologacao.env`, o verificador reprova, e o `deploy.sh` da produção sai com 4 antes dele, porque a produção nunca sobe sem as conferências cruzadas. Quando os dois existem, as cruzadas são obrigatórias.

`verificar-ambiente.test.sh` prova cada caso negativo sobre um par gerado dos arquivos `.exemplo` (sobe nenhum contêiner).

### ensaio-ambientes.sh

Ensaio local da topologia, só em projetos `paf-ensaio-*` (roda `down -v`): sobe os dois projetos, confere `SHOW timezone`, o preflight CORS com `X-Paf-Unidade` (204, e a origem do outro ambiente recusada), o `/config.json` do dashboard, que uma emissão na homologação não publica nada no Valkey de produção em 5 s (com a emissão na produção como controle positivo) e que o `down -v` da homologação deixa produção rodando. A massa demo do ensaio entra pelo `seed-demo` em `Development` dentro da rede do projeto; o seed da homologação de verdade é O09 (D19).

## Observabilidade no New Relic (O10)

Logs, traces e métricas saem por OTLP direto para o New Relic, pelo caminho que o kit já tem: com `OTEL_EXPORTER_OTLP_ENDPOINT` definido, `AddHeroLogging` liga o sink de log do Serilog (acrescenta `/v1/logs`) e `AddHeroOpenTelemetry` liga os exportadores do SDK. Endpoint, protocolo e cabeçalho `api-key` com a chave de licença foram conferidos na documentação do New Relic em 19/10/2026 (região US `https://otlp.nr-data.net:4318`; conta na UE usa `https://otlp.eu01.nr-data.net:4318`). Nenhum log leva e-mail: o módulo do PAF ocupa `UserEmail` com `[omitido]` antes do enricher do kit, sem editar o kit.

A chegada dos dados no New Relic é passo manual de Marcelo, depois do primeiro `up` com a chave real: consultar `FROM Log SELECT * WHERE service.name = 'paf-api-homologacao' SINCE 10 minutes ago` e conferir `Tenant`, `UnidadeId`, `trace.id` e `UserEmail` igual a `[omitido]`. Até essa execução, o passo conta como SKIPPED, nunca como verde.

## Prazo de inatividade do dashboard (O15, D04)

O guarda de inatividade do kit desloga quem fica 20 minutos sem interagir, o que soltaria a licença e o guichê do atendente parado no posto. Pela decisão D04 (Marcelo Maciel, 17/10/2026), o prazo passa a ser um só para todas as telas do dashboard, da ordem de um turno: 8 horas de inatividade (`inactivityIdleMs: 28800000`) mais 1 minuto de aviso (`inactivityWarningMs: 60000`). Risco aceito: uma tela de cadastro esquecida aberta continua logada durante esse prazo.

O valor vem de `deploy/paf/dashboard/config.json.template`, que substitui o template do kit sem editá-lo. Contrato com o compose do PAF (O02): o contêiner do dashboard, em todos os ambientes, monta esse arquivo por volume em `/etc/fsh/config.json.template:ro` e recebe `FSH_API_URL` e `FSH_DEFAULT_TENANT`, que o entrypoint do kit substitui por `envsubst` ao subir.

O prazo de inatividade vem de `PAF_INATIVIDADE_MS` (O02) e o aviso é número literal. Uma variável esquecida viraria texto vazio e o `env.ts` do kit cairia em silêncio nos 20 minutos; por isso o compose exige a variável (`${PAF_INATIVIDADE_MS:?}`), o `verificar-ambiente.sh` reprova valor que não seja inteiro positivo e o smoke abaixo confere que o `/config.json` servido traz número. O template também não liga `demoMode`; se a homologação precisar do seletor de conta de demonstração, ela monta um template próprio com a mesma regra.

```bash
docker build -t paf-dashboard:smoke clients/dashboard
deploy/paf/smoke-inatividade.sh paf-dashboard:smoke
```

O smoke sobe a imagem duas vezes, com e sem a montagem, e confere no `/config.json` servido que o prazo é número igual a 8 horas com o template do PAF e que ele não existe sem a montagem. Código de saída 0 só com tudo verde; remove os contêineres ao sair. Prova negativa: com o valor entre aspas no template, o smoke sai com código 1.

## R2 da aplicação: bloqueio da mídia, CORS e conferência no restore (M11)

Roteiro por ambiente, um passo por linha com estado VERIFICADO ou SKIPPED, em `deploy/paf/r2/ROTEIRO.md`. Ferramentas (aplicativos .NET de arquivo único, `dotnet run <arquivo>.cs`):

- `r2/provar-r2.cs <arquivo .env>`: prova de aceite contra o bucket real (bloqueio de 30 dias em `paf-midia/`, DELETE livre fora dele, GET sem assinatura 403, assinatura de 8 dias recusada, CORS do dashboard). Código 0 só com tudo PASS.
- `r2/conferir-midias.cs <arquivo .env>` com `PAF_CONFERENCIA_CONEXAO`: HEAD em lote das chaves de `paf."Midias"` no banco restaurado. Código 0 tudo presente, 1 algum ausente, 2 conferência não rodou. É o passo de mídia do teste de restauração do O11.
- `r2/r2.test.sh`: prova local das duas contra PostgreSQL com as migrações reais e RustFS; remove os contêineres ao sair.

## Túnel e Caddy (O03, RNF02, RNF16)

A entrada pública de cada ambiente é um Cloudflare Tunnel próprio, e o Apache compartilhado da VPS não participa do caminho do PAF. A decisão, as opções comparadas e os critérios de aceite estão em `PAFV2/Analise/PROPOSTA-O03-PROXY.md`, com as decisões de Marcelo de 20/10/2026 na seção 8.1.

O que roda, por ambiente, na rede `paf` do projeto:

- `cloudflared` (imagem `cloudflare/cloudflared:2026.10.2`), duas réplicas do mesmo túnel, sem porta publicada. Só abre conexões de saída para a Cloudflare na porta 7844, e entrega toda requisição ao Caddy. Reiniciar uma réplica não derruba o ambiente, porque a outra continua conectada (CA4).
- `caddy` (imagem `caddy:2.11.7-alpine`), sem porta publicada, em HTTP simples dentro da rede: o TLS termina na borda da Cloudflare, então não há certificado nem ACME na origem. Ele roteia por host e balanceia `api-a` e `api-b`.

O túnel é gerenciado localmente (Q7): a configuração fica versionada em `cloudflared/config.yml`, e o painel da Cloudflare não guarda regra nenhuma. O `cloudflared` lê o UUID do túnel de `PAF_TUNEL_ID`, que não é segredo (é o destino dos CNAMEs), e o conteúdo do JSON de credenciais de `TUNNEL_CRED_CONTENTS`, num arquivo próprio do ambiente (`ambientes/<ambiente>.tunel.env`, apontado por `PAF_ARQUIVO_TUNEL`) que só o serviço `cloudflared` lê: o arquivo de ambiente inteiro vai para a API e o migrator como `env_file`, e a credencial do túnel não pode ir junto. O `--token` não serve aqui: a documentação de parâmetros do `tunnel run` diz que ele vale só para túnel gerenciado remotamente, cuja configuração mora no painel (https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/configure-tunnels/cloudflared-parameters/run-parameters/). O túnel gerenciado localmente roda com o arquivo de credenciais que o `cloudflared tunnel create` grava (https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/local-management/create-local-tunnel/), e `--credentials-contents` (`TUNNEL_CRED_CONTENTS`) é a forma do próprio binário de receber esse conteúdo sem arquivo, conferida no `cloudflared tunnel run --help` da versão 2026.10.2.

`cloudflared/config.yml` tem uma regra só, que é também a regra final obrigatória: todo host roteado para o túnel vai ao Caddy. Os hosts do PAF ficam explícitos no `caddy/Caddyfile`, comparados com `PAF_API_URL`, `PAF_DASHBOARD_URL` e `PAF_ADMIN_URL` do arquivo de ambiente, e qualquer outro host recebe 404 do Caddy. Uma regra por host no `config.yml` exigiria os nomes escritos no arquivo, um por ambiente, porque o `cloudflared` não expande variáveis de ambiente na configuração e a imagem dele não tem shell para gerar o arquivo na partida.

O `caddy/Caddyfile`:

- API: `reverse_proxy` para `api-a:8080` e `api-b:8080`, rodízio sem afinidade (opção B da ARQ seção 11: o `PafHub` conecta só por WebSockets com `SkipNegotiation`), verificação ativa em `/api/v1/paf/saude` a cada 2 s com prazo de 4 s por sondagem (a própria rota de saúde espera o PostgreSQL por até 2 s) e saída do rodízio só depois de 2 falhas seguidas (`health_fails 2`), verificação passiva por 2 s depois de uma falha de conexão e `lb_try_duration 5s`, para que a requisição que encontra uma instância parada tente a outra. Sem keep-alive para as APIs (`keepalive off`): no `SIGTERM` o Kestrel fecha as conexões ociosas, e um `POST` escrito numa delas nesse instante falhava depois de enviado e voltava 502 sem nova tentativa (visto no ensaio de 20/10/2026); com uma conexão por requisição a falha acontece na abertura e o Caddy tenta a outra instância.
- Dashboard e admin: `reverse_proxy` para os contêineres respectivos.
- Log em JSON na saída do contêiner, com a query string removida de `request>uri` em todos os hosts, porque o hub do PAF, o hub do kit e `/notifications` levam `?access_token=`. O filtro vale para o log de acesso e também para o log padrão do Caddy (opção global `log default`), porque os logs de erro (`http.log.error`, por exemplo quando nenhuma instância responde) carregam a requisição inteira; sem ele, o token aparecia no log com as duas APIs paradas (conferido em 20/10/2026). `Authorization` e `Cookie` já saem como `REDACTED` por padrão. Sintaxe do filtro conferida em https://caddyserver.com/docs/caddyfile/directives/log.
- IP do cliente: `trusted_proxies` igual a `PAF_SUBNET_DINAMICA` e `client_ip_headers CF-Connecting-IP`, e a API recebe `X-Forwarded-For` com esse único valor e `X-Forwarded-Proto https`. Como a origem não tem porta aberta, só o `cloudflared` alcança o Caddy pela faixa dinâmica, e o `CF-Connecting-IP` vem da borda da Cloudflare. Um `X-Forwarded-For` enviado pelo cliente nunca é lido. A API confia só no IP fixo do Caddy (`TrustedProxyOptions:KnownProxies`, `ForwardLimit` 1): um processo do host que chama a porta de loopback de uma API chega pelo gateway da ponte, e o `X-Forwarded-For` dele é ignorado.
- Recarregar o Caddyfile fecha todos os WebSockets por padrão. Nenhuma operação de rotina recarrega o Caddy: o deploy troca instâncias pela verificação de saúde. Mudança no `Caddyfile` ou no `config.yml` é passo manual, fora do horário de atendimento: `docker compose ... up -d --force-recreate caddy`, ou, para o `cloudflared`, recriar uma réplica de cada vez.

O `deploy.sh` sobe o Caddy se faltar e nunca o recria; o `cloudflared` ele nunca toca. Atualizar a imagem de qualquer um dos dois é passo humano: trocar a tag no `compose.yml`, recriar o Caddy (os clientes reconectam sozinhos) e o `cloudflared` uma réplica por vez, com o ambiente respondendo entre uma e outra.

### Passo humano na Cloudflare, uma vez por ambiente

Feito para a homologação em 20 e 21/10/2026 pela API da Cloudflare com o token da Codefi, no lugar do `cloudflared tunnel login`: túnel `paf-homologacao` criado com `config_src: local` e segredo gerado na hora (o JSON de credenciais é `{"AccountTag","TunnelSecret","TunnelID"}`), CNAMEs com proxy de `paf-homolog`, `paf-homolog-api` e `paf-homolog-admin` em `codefi.com.br` (um nível só, porque o Universal SSL da zona Free não cobre subdomínio de segundo nível) e a regra de configuração com `security_level` `essentially_off` e `bic` desligado nesses três hosts. A produção segue os mesmos passos depois da homologação estável.

- [ ] Na máquina de quem administra a zona `codefi.com.br`: `cloudflared tunnel login`, depois `cloudflared tunnel create paf-homologacao`. O comando imprime o UUID e grava `<UUID>.json`.
- [ ] No host: `ambientes/homologacao.env` com `PAF_TUNEL_ID=<UUID>` e `ambientes/homologacao.tunel.env` (copiado de `homologacao.tunel.env.exemplo`) com `TUNNEL_CRED_CONTENTS=` seguido do conteúdo de `<UUID>.json` numa linha só, com permissão só do dono (`chmod 600`). Apagar o `.json` local depois.
- [ ] Rotas DNS, uma por host do ambiente: `cloudflared tunnel route dns paf-homologacao <host>` para os hosts de `PAF_API_URL`, `PAF_DASHBOARD_URL` e `PAF_ADMIN_URL`. Cada uma cria um CNAME com proxy para `<UUID>.cfargotunnel.com`. Se o host já tiver registro, anotar o valor anterior antes, para a volta atrás.
- [ ] Regra de configuração da zona para os hosts do PAF, com confirmação de Marcelo (Q6): nível de segurança baixo e verificação de navegador desligada. Bot Fight Mode da zona continua desligado.
- [ ] No host: `bash deploy/paf/verificar-ambiente.sh ambientes/producao.env ambientes/homologacao.env`, depois `docker compose -f deploy/paf/compose.yml --env-file deploy/paf/ambientes/homologacao.env up -d caddy cloudflared` e conferir nos logs das duas réplicas a linha `Registered tunnel connection`.
- [ ] Medir CA1 a CA10 pelo host público (lista abaixo) e registrar o resultado.

Volta atrás (CA10): os CNAMEs dos hosts do PAF são apagados no painel de DNS e os registros anteriores, se havia, são restaurados; depois `docker compose ... stop cloudflared`. O Apache nunca foi tocado, então não há nada a desfazer nele.

### Critérios de aceite (seção 7 da proposta)

Medidos em homologação, pelo host público, antes de produção: CA1 upgrade do hub com 101 sem negociação; CA2 conexão ociosa por 30 minutos e 30 de 30 eventos; CA3 `docker kill` da instância conectada com reconexão em até 30 s e p95 abaixo de 5 s, dez vezes; CA4 reinício de uma réplica do `cloudflared` e do Caddy com retorno em até 30 s; CA5 300 WebSockets por 30 minutos sem afetar os outros sites do host; CA6 zero `access_token=` nos logs do Caddy e do `cloudflared`; CA7 IP auditado igual ao do cliente, com `X-Forwarded-For` forjado sem efeito; CA8 deploy em sequência sem 5xx na emissão e com todo evento no painel; CA9 nenhuma porta do PAF respondendo pelo IP público; CA10 volta atrás em menos de 5 minutos. O `ensaio-deploy.sh` prova localmente CA1, CA3 (três quedas), CA6 no Caddy, CA7 e CA8, sem Cloudflare na frente.

Medição em homologação, 21/10/2026, imagem `558ad658`, pelo host público (script fora do repositório, com cliente SignalR por WebSocket sem negociação e ping de 15 s):

| Critério | Resultado |
|---|---|
| CA1 | PASS: handshake do `PafHub` completo por `wss://paf-homolog-api.codefi.com.br`, sem negociação |
| CA2 | PASS: conexão sem evento de aplicação aberta 30 min sem fechamento. A parte de 30 de 30 eventos não foi medida pelo host público |
| CA3 | PASS em três rodadas: 10 `docker kill` alternados com 20 clientes, 190 de 190 reconexões, p95 de 1,9 s e a maior em 2,0 s |
| CA4 | PASS: reinício de uma réplica do `cloudflared` sem nenhuma queda; reinício do Caddy com 20 de 20 de volta em 1,4 s |
| CA5 | PASS: 300 WebSockets por 30 min sem falha de abertura; p95 de `app.codefi.com.br` pelo Apache do host de 194 para 204 ms (5,1%), memória do host de 4,0 para 4,2 GB. Observado: 18 das 300 conexões fechadas no meio da janela, sem aborto na API e sem erro no `cloudflared`, isto é, vindos da borda ou da rede do cliente; o cliente reconecta (CA3) |
| CA6 | PASS: zero `access_token=` nos logs do Caddy, das duas réplicas do `cloudflared` e das duas APIs |
| CA7 | PASS: IP auditado igual ao IP público de quem chamou, com `X-Forwarded-For` forjado; `CF-Connecting-IP` enviado pelo cliente é recusado pela borda com 403 |
| CA8 | PASS: `deploy.sh homologacao` das duas instâncias com 128 de 128 chamadas autenticadas em 200 e 30 quedas do hub, todas de volta em até 1,6 s. A emissão em laço com entrega ao painel é a do ensaio local |
| CA9 | PASS: portas 25432 e 28080 a 28083 sem resposta pelo IP público da VPS |
| CA10 | PASS: CNAMEs apagados e `cloudflared` parado, PAF fora em 5 s; de volta em 10 s |

Walk no Chrome pelo host público: login, escolha de guichê, `PafHub` por WebSocket, segunda aba na mesma sessão e saída pelo menu, com um POST de logout (204) na aba que saiu e nenhum na outra.

## CI e promoção (O08, RNF16, RNF17)

`.github/workflows/paf.yml` é arquivo novo, registrado no ADR 0008 ("Workflow de CI do PAF"); os workflows do kit não mudam e continuam só em `main`. Roda em push para `main` e `feat/**` e em todo pull request, sem filtro de branch base, porque os PRs do PAF apontam para branches de feature.

Jobs obrigatórios, todos agregados no job `PAF CI`, que fica vermelho se qualquer um falhar, for cancelado ou pulado:

- `Backend PAF`: build da solução, `Architecture.Tests`, `PAF.Tests` e `PAF.Kestrel.Tests`;
- `Integration PAF (C-E | I-M-R-S | demais)`: `Integration.Tests` com o filtro `FullyQualifiedName~Paf` em três jobs paralelos, pela letra depois de `Paf` no nome da classe (Testcontainers sobre o Docker do runner). As classes dividem uma coleção do xUnit e rodam em série, por isso a divisão; a parte `demais` pega tudo que as outras não pegam, então classe nova nunca fica de fora. Rebalancear pelos tempos do `.trx` quando uma parte passar muito das outras;
- `Dashboard PAF`: `npm run lint`, `npm run build` (inclui `tsc -b`) e os testes unitários do PAF por `node --test` (D21);
- `Scripts de implantação`: `exigir-homologacao-verde.test.mjs`, `scripts-ci.test.sh` e `verificar-ambiente.test.sh`;
- `Walk no Chrome (1 | 2 | demais)`: todos os `clients/dashboard/tests/e2e/paf-*.walk.mjs`, divididos em jobs paralelos por `walks-ci.txt`. Cada job roda `walk-ci.sh <parte>`, que sobe uma pilha só (PostgreSQL, Valkey e RustFS efêmeros, build do .NET, servidor do Vite e, se a parte tiver walk que pede o build de produção, `npm run build` com `vite preview`) e, para cada walk, recria o banco, aplica migrações e `seed-demo` com a senha do secret, limpa o Valkey e sobe a API de novo (os caches em memória da API guardariam os ids do banco anterior). Um walk que falha não interrompe os seguintes, e o job fica vermelho se qualquer um falhar. A parte `demais` pega todo walk que o arquivo não lista, então walk novo nunca fica de fora; `scripts-ci.test.sh` falha se a matriz do `paf.yml` e as partes do arquivo divergirem ou se algum walk ficar em duas partes. O `ffmpeg`, que a imagem do runner não traz, é instalado só no job cuja parte tem walk que lê `PAF_FFMPEG`. Os walks rodam no Google Chrome do runner. Sem o secret `PAF_SENHA_DEMO`, ou com ele igual à senha demo pública do kit, o job falha antes de subir qualquer coisa. Rebalancear `walks-ci.txt` pelos tempos dos jobs no CI quando uma parte passar muito das outras.
- `Imagem anterior contra esquema novo` (O07, RNF16): `imagem-anterior-esquema-novo.sh <ref-anterior>` compila a API da revisão anterior (base do PR, ou o commit anterior no push), aplica migrações e `seed-demo` com o DbMigrator atual num PostgreSQL efêmero, sobe a API anterior nesse esquema e roda `imagem-anterior-esquema-novo.mjs`: emissão no totem 200, seleção do guichê 204, chamar próxima 200 com a senha emitida e o painel com a chamada. A senha do seed é aleatória por execução, sem secret. Revisão anterior sem o módulo PAF imprime `NAO-SE-APLICA` e sai 0. `imagem-anterior-esquema-novo.test.sh` cobre os erros de uso (saída 2) sem subir nada. A barreira estática fica em `PAF.Tests` (`Esquema/BarreiraExpandirContrairTests`, `Contrato/ContratoEstacaoTests`).

```bash
PAF_SENHA_DEMO=<senha> deploy/paf/walk-ci.sh 1   # a parte 1 do CI, local; precisa de npm ci em clients/dashboard
deploy/paf/scripts-ci.test.sh                    # caminhos de falha dos scripts de CI, sem subir nada
node --test deploy/paf/exigir-homologacao-verde.test.mjs
```

Promoção:

- `Deploy homologação`: só em push para `main`, depois do `PAF CI` verde, no environment `homologacao`. Publica `paf-api`, `paf-dbmigrator`, `paf-dashboard` e `paf-admin` no GHCR com a tag igual ao SHA completo e chama `implantar-remoto.sh homologacao <sha>`.
- `Deploy produção`: só manual (`workflow_dispatch` a partir de `main`, com o SHA). O job `Exigir SHA verde em homologação` roda antes do environment `producao` pedir aprovação e falha se a implantação mais recente daquele SHA em homologação não terminou em `success` (`exigir-homologacao-verde.mjs`). A produção usa as mesmas imagens que passaram na homologação, sem rebuild.
- `implantar-remoto.sh <ambiente> <sha>` confere os segredos, copia `deploy/paf`, `deploy/docker/postgres-init` e `appsettings.Production.json` (lido pelo `verificar-ambiente.sh`) para o host por SSH (os `ambientes/*.env` do host ficam onde estão) e roda lá `deploy/paf/deploy.sh <ambiente> <sha>` com `PAF_REGISTRO` no ambiente (seção Deploy em sequência).

Configuração do repositório, fora do código:

- [ ] secret de repositório `PAF_SENHA_DEMO` (senha do `seed-demo` do walk, diferente de `Password123!`);
- [ ] environments `homologacao` e `producao`, com revisor obrigatório em `producao`, e em cada um os secrets `PAF_SSH_HOST`, `PAF_SSH_USUARIO`, `PAF_SSH_CHAVE`, `PAF_SSH_KNOWN_HOSTS` e `PAF_DIR_HOST`;
- [ ] `docker login ghcr.io` no host com um token de leitura de pacotes;
- [ ] tornar o job `PAF CI` verificação obrigatória na proteção das branches.

### Walks locais

Um walk sozinho roda localmente por `walk-local.sh`, sobre a mesma receita de pilha do CI (`walk-pilha.sh`, a única cópia), e o walk recebe `PAF_API`, `PAF_WEB`, `PAF_S3`, `PAF_S3_BUCKET`, `PAF_SENHA_DEMO`, `PAF_PG` e `PAF_PG_CONTAINER`. O que cada walk pede da pilha sai do nome do arquivo, em `walk-pilha.sh`: o build de produção com `vite preview` para `paf-navegacao`, `paf-fichas-do-dia` e `paf-selecionar-ficha`, e as URLs curtas de mídia (`PAF__Midia__MargemLeitura=00:00:40`, `PAF__Midia__PisoValidadeUrlLeitura=00:00:01`) para `paf-midia-painel`. Recusa senha ausente ou igual à do kit e `PAF_WALK` ausente ou inexistente com saída 2, sem subir nada; não deixa processo nem contêiner ao sair.

```bash
PAF_SENHA_DEMO=<senha> PAF_WALK=tests/e2e/paf-vip.walk.mjs deploy/paf/walk-local.sh
```

`PAF_PULAR_BUILD=1` pula o build do .NET quando os binários Release já são desta árvore.

## Massa demo da homologação (O09, D19, RNF17)

A homologação usa os tenants de demonstração `acme` e `globex`, com a unidade, a fila, a prioridade, as estações e o contrato do `PafDemoSeeder`, criados pelo verbo `seed-demo` do DbMigrator do kit. O guarda do kit (`src/Host/FSH.Starter.DbMigrator/Program.cs`) recusa o verbo fora de `Development` e continua intocado: pela decisão D19, quem roda o seed é `seed-homolog.sh`, que passa `DOTNET_ENVIRONMENT=Development` só para o contêiner do migrador daquela execução. A API e o migrador do `up` continuam em `Production`.

```bash
docker compose -f deploy/paf/compose.yml --env-file deploy/paf/ambientes/homologacao.env up -d --wait postgres valkey
PAF_SENHA_DEMO='<senha da massa demo>' deploy/paf/seed-homolog.sh deploy/paf/ambientes/homologacao.env
```

As guardas rodam antes de qualquer chamada ao `docker`, então um pedido recusado sai com código 2 sem abrir conexão nenhuma:

- o projeto do arquivo tem de ser `paf-homologacao` (ou `paf-ensaio-*`, só no ensaio local): o arquivo de produção, e qualquer arquivo desconhecido, é recusado;
- a string de conexão tem de apontar para `Host=postgres`, o PostgreSQL do próprio projeto, alcançável só dentro da rede dele: um arquivo editado para chegar ao banco de produção pela porta publicada ou por outro host é recusado;
- a senha dos usuários demo vem de `PAF_SENHA_DEMO`, nunca de arquivo: obrigatória, com 12 caracteres ou mais, e diferente das senhas padrão do kit (`Password123!` e `123Pa$$word!`, que estão no `appsettings.Development.json` e em todo teste). Ela vai ao contêiner por `-e Seed__DemoPassword` sem valor, então não aparece na linha de comando.

O seed é idempotente: rodar de novo não duplica nada e realinha a senha dos usuários demo para a de `PAF_SENHA_DEMO`. Efeito conhecido de `Development` nesse contêiner: o migrador carrega o `appsettings.Development.json` da API, e o que o arquivo do ambiente não sobrescreve vale só durante o seed (nível de log, `OpenApiOptions`, `Seed:Demo`). Ressalva do kit, fora do escopo de O09: o guarda de `Production` só dispara depois de esperar o banco, tomar a trava e aplicar as migrações do catálogo, então um `seed-demo` recusado ainda migra o catálogo.

```bash
deploy/paf/seed-homolog.test.sh paf-dbmigrator:<tag>
```

O teste prova cada guarda com um `docker` falso no `PATH`, que deixa um arquivo marcador se for chamado (código 2 e marcador ausente; a homologação válida é o controle e chega ao `docker`). Depois sobe só PostgreSQL e Valkey no projeto descartável `paf-ensaio-o09` (porta 29432 do PostgreSQL, 29080 a 29083 reservadas, subnet `172.31.29.0/24`), roda o wrapper duas vezes e confere que a contagem exata de linhas de toda tabela é igual nas duas, que o hash do administrador demo confere com `PAF_SENHA_DEMO` e não com a senha padrão, e que o `seed-demo` sem o wrapper, com o `Production` do arquivo, continua recusado pelo kit. Remove o projeto ao sair.

## Backup diário e teste de restauração (O11, RNF14)

O serviço `backup` do `compose.yml` roda `paf-backup agendar` (imagem `deploy/paf/pg-backup`, sobre a mesma `postgres:18-alpine` do banco, porque o `pg_dump` não lê servidor de versão maior que a dele). Uma vez por dia, às 03:00 de Brasília (`TZ=America/Sao_Paulo`, o agendador recusa outro fuso):

- `flock` no volume de trabalho: uma segunda execução simultânea sai com código 75 sem enviar nada;
- recusa o backup se algum tenant tiver banco próprio (`ConnectionString` preenchida), porque esse banco ficaria fora da cópia;
- abre uma transação `REPEATABLE READ`, exporta o snapshot, conta as linhas de toda tabela dentro dele e roda `pg_dump -Fc --snapshot` no mesmo snapshot, de modo que as contagens batem exatamente com a cópia;
- confere o dump com `pg_restore --list`, calcula o sha256 do dump e das contagens e envia, nesta ordem, `.dump`, `.contagens` e `.sha256` para `<projeto>/copias/<AAAA-MM-DD>/fsh-<AAAA-MM-DDTHHMMSS>` no bucket de backup do ambiente. Todo envio é condicional (`If-None-Match: *`): o script nunca sobrescreve objeto. O `.sha256` vai por último; cópia sem ele é envio interrompido, e a restauração a recusa;
- qualquer falha: linha JSON no log, POST `{"text": "..."}` em `PAF_BACKUP_ALERTA_URL`, código diferente de zero e healthcheck do contêiner vermelho até o próximo sucesso. O healthcheck também fica vermelho sem sucesso há mais de 26 horas.

Execução manual (depois de todo deploy e sempre que quiser uma cópia agora):

```bash
docker compose -f deploy/paf/compose.yml --env-file deploy/paf/ambientes/producao.env exec backup paf-backup agora
```

### Passo humano: bucket, token, bloqueio e ciclo de vida (uma vez por ambiente)

A garantia contra um host comprometido é o bloqueio do bucket (ARQ l.388), que só o dono da conta Cloudflare configura. Para cada ambiente, com `wrangler login` na conta da Codefi:

- [ ] Criar o bucket de backup: `npx wrangler r2 bucket create paf-backup-producao` (e `paf-backup-homologacao`), separado do bucket da aplicação.
- [ ] Bloqueio de 7 dias no bucket inteiro: `npx wrangler r2 bucket lock add paf-backup-producao retencao-7-dias "" --retention-days 7`.
- [ ] Ciclo de vida só nas cópias, que apaga o que passou do prazo e uploads incompletos: `npx wrangler r2 bucket lifecycle add paf-backup-producao expira-copias "paf-producao/copias/" --expire-days 7 --abort-multipart-days 1` (o prefixo é o `COMPOSE_PROJECT_NAME` do ambiente).
- [ ] Ciclo de vida dos objetos de prova do bloqueio: `npx wrangler r2 bucket lifecycle add paf-backup-producao expira-provas "paf-producao/verificacao-bloqueio/" --expire-days 7`.
- [ ] Conferir: `npx wrangler r2 bucket lock list paf-backup-producao` mostra a regra de bloqueio e `npx wrangler r2 bucket lifecycle list paf-backup-producao` as duas de ciclo de vida. Os relatórios de restauração (`paf-producao/restauracoes/`) ficam fora do ciclo de vida de propósito: são o registro do RNF14 e não expiram.
- [ ] Token de API do R2 com permissão de objeto, leitura e escrita, só nesse bucket; access key e secret em `PAF_BACKUP_ACCESS_KEY` e `PAF_BACKUP_SECRET_KEY` do arquivo do ambiente. Nunca a credencial do Storage da aplicação (o `verificar-ambiente.sh` reprova).
- [ ] Provar o bloqueio com a credencial do backup: `docker compose -f deploy/paf/compose.yml --env-file <arquivo.env> run --rm backup verificar-bloqueio`. Envia um objeto de prova, tenta sobrescrever e apagar, e só sai com 0 se as duas tentativas forem recusadas. O objeto fica retido 7 dias e sai pelo ciclo de vida `expira-provas`.
  - VERIFICADO em homologação em 19/10/2026 (`paf-backup-homologacao`, imagem desta pasta com `docker run --rm ... verificar-bloqueio` e `PAF_AMBIENTE=paf-homologacao`): `PASS sobrescrita recusada`, `PASS exclusão recusada`, `VERIFICAR-BLOQUEIO OK`, código 0. As respostas cruas do R2 confirmam o motivo: sobrescrita e exclusão recusadas com `ObjectLockedByBucketPolicy`, e o conteúdo lido depois continua o original. Produção SKIPPED.

Comandos conferidos na documentação do Wrangler em 19/10/2026 (`r2 bucket lock add <BUCKET> [NAME] [PREFIX] --retention-days`, `r2 bucket lifecycle add <BUCKET> [NAME] [PREFIX] --expire-days --abort-multipart-days`). O bloqueio tem precedência sobre o ciclo de vida, então nada sai antes dos 7 dias.

### Teste de restauração

```bash
deploy/paf/restaurar-backup.sh deploy/paf/ambientes/producao.env ultima      # ou a chave sem extensão
```

Sobe um projeto descartável (`deploy/paf/pg-backup/restauracao.yml`, rede própria, onde o `postgres` do ambiente nem resolve), e em sequência, cada etapa com o seu código de saída: baixa a cópia e confere o sha256; `pg_restore` num PostgreSQL vazio; compara a contagem de cada tabela com a do momento do dump; roda o `apply` da imagem do migrator do ambiente sobre a cópia, e qualquer linha nova em `__EFMigrationsHistory` reprova (migração pendente); faz HEAD de cada `paf."Midias"."ChaveObjeto"` no bucket da aplicação, com a mesma regra de prefixo do kit, e uma mídia ausente reprova. Com tudo verde, grava o relatório em `<projeto>/restauracoes/<instante>.json` no bucket de backup, que é o registro do RNF14. Remove o projeto descartável ao sair.

### Testes

```bash
docker build -t paf-backup:teste deploy/paf/pg-backup
deploy/paf/publicar-imagem-migrator.sh teste
deploy/paf/pg-backup/backup.test.sh paf-backup:teste paf-dbmigrator:teste
```

PostgreSQL migrado e com o `seed-demo`, MinIO no lugar dos dois buckets e um receptor de webhook. Casos: cópia completa e restauração aprovada e registrada; execução simultânea no `flock`; credencial errada com alerta e healthcheck vermelho; armazenamento fora do ar no meio da execução sem `.sha256`; cópia parcial recusada pelo sha256; cópia sem `.sha256` recusada; mídia ausente; migração pendente; tenant com banco próprio; `verificar-bloqueio` reprovando bucket sem bloqueio; agendador mirando 03:00 de Brasília e recusando outro fuso. A recusa de sobrescrita e exclusão dentro dos 7 dias não tem como ser provada no MinIO (ele cria versão nova em vez de recusar) e fica SKIPPED aqui: a prova é o `verificar-bloqueio` no R2 real, no passo humano acima.

## Limite de taxa (O12, ARQ l.380 e l.382, D20)

O limitador do kit (janela fixa de 60 s, sem fila, em memória por instância) fica ligado em produção com os limites do arquivo de ambiente, medidos e não chutados. `RateLimitingOptions__Ip__PermitLimit=800`, `__Tenant__PermitLimit=16000` e `__Auth__PermitLimit=30` estão nos dois `.env.exemplo`; `User` fica no valor do kit (200). Sem `TrustedProxyOptions__KnownProxies__0` igual ao IP fixo do Caddy, toda loja cai na partição do IP do Caddy e o limite vira um teto para a base inteira; o teste de integração prova isso com a rede confiável errada (429 já nos logins da primeira loja).

Medição de 19/10/2026, com `clients/dashboard/tests/e2e/paf-abertura-loja.medicao.mjs` contra a API real (`PAF_WALK=tests/e2e/paf-abertura-loja.medicao.mjs deploy/paf/walk-local.sh`), requisições por estação no minuto da abertura, contando toda rota da tela (shell do kit, upgrades de WebSocket):

| Estação | Abertura (60 s) | Minuto seguinte | Política `auth` |
|---|---|---|---|
| Guichê (login, escolha do guichê) | 30 | 0 | 1 login |
| Totem | 5 | 1 (configuração a cada 60 s) | 0 (resgate do vínculo só na instalação) |
| Painel | 6 | 1 | 0 |

A medição roda o dashboard no servidor de desenvolvimento, com o `StrictMode` do React duplicando efeitos (3 leituras de permissões, 4 conexões do hub no guichê), então os números são teto, não média.

Com o volume D20 (por loja 8 guichês, 3 totens e 2 painéis; 20 lojas) e o pico de 10 fichas por totem no minuto, cada ficha custando 3 operações de guichê, o pior minuto de uma loja atrás do seu NAT é 8×30 + 3×5 + 2×6 + 30×(1+3) = 387 requisições, e o do tenant, com as 20 lojas abrindo no mesmo minuto, 7740. Os limites são o dobro, arredondado. A política `auth` precisa de 8 logins e 5 renovações de credencial de estação por loja; 30 cobre o critério de 15 logins em 60 s com folga para nova tentativa. Com duas instâncias o teto efetivo dobra, porque cada uma conta sozinha.

A partição própria para estação (ADR 0008, linha "Partição de limite de taxa por estação") não entra: com os limites acima, os 60 totens das 20 lojas emitem no mesmo minuto sem recusa.

Barreiras: `verificar-ambiente.sh` reprova `Auth` abaixo de 15, `Ip` abaixo de 387 e `Tenant` abaixo de 7740 (ausente conta como o valor do kit); `src/Tests/Integration.Tests/Tests/PAF/PafLimiteTaxaTests.cs` sobe a API com `appsettings.Production.json` e as linhas deste arquivo de ambiente e prova os 15 logins pelo proxy, o `X-Forwarded-For` forjado, o pico da loja e do tenant e o 429 com `Retry-After` e sem ficha. O teste apresenta o proxy como um endereço da rede do Docker, que é o caso do Caddy; o caminho real pelo Caddy, com `CF-Connecting-IP` e `X-Forwarded-For` forjado, é o CA7 do `ensaio-deploy.sh`.

## Deploy em sequência (O06, RNF16, ARQ seção 11)

```bash
PAF_REGISTRO=ghcr.io/<dono> deploy/paf/deploy.sh <homologacao|producao> <tag>   # no host, em $PAF_DIR_HOST
```

O CI chama pelo `implantar-remoto.sh`; à mão, roda no host, nunca de fora. Antes de cada deploy, o rollback é nomeado e confirmado por Marcelo: é o mesmo script com a tag anterior, que o deploy imprime (`rollback: deploy/paf/deploy.sh <ambiente> <tag>`) e grava em `ambientes/<ambiente>.tag-anterior` antes de tocar em qualquer coisa.

A tag anterior é a tag em execução que não é a do deploy. Depois de um deploy parcial (código 7), `api-a` e `api-b` rodam tags diferentes: repetir a tag nova mantém a antiga como rollback, voltar para a antiga grava a nova, e uma terceira tag é recusada com 4, sem tocar em nada. Repetir a tag que já roda nas duas instâncias mantém o rollback que estava gravado.

Sequência, cada passo com código próprio:

1. `flock -n` em `ambientes/<ambiente>.deploy.lock`: o segundo deploy simultâneo do mesmo ambiente sai na hora com 3.
2. Conferências sem mutação: os dois arquivos de ambiente, `verificar-ambiente.sh` (inclui `QuotaOptions__Enabled=false`, RNF09, e o túnel e o Caddy), as quatro imagens presentes ou baixadas.
3. PostgreSQL, Valkey e Caddy sobem se faltarem, nunca são recriados; o migrator da tag nova roda antes de qualquer instância. Só expandir: quem garante é a barreira da O07 no CI, o deploy não sabe distinguir.
4. Por instância, `api-a` e `api-b`, com a que não tem contêiner em execução primeiro (subir uma instância nova não para nada, e a existente ganha uma parceira saudável): a outra precisa responder `GET api/v1/paf/saude` 200 na sua porta, senão o deploy sai com 11 sem parar esta (o migrator já rodou); para esta instância com `SIGTERM` e `stop_grace_period` 30 s, em que o Kestrel termina as requisições em andamento e fecha os WebSockets, cujos clientes reconectam pelo Caddy na outra; sobe a tag nova; espera saúde 200 na porta da instância em até 90 s; espera 6 s para a verificação ativa do Caddy (a cada 2 s) e a passiva (2 s) a devolverem ao rodízio antes de parar a seguinte. Não há dreno explícito (Q4): a nova conexão que chega à instância parada falha na abertura e o Caddy tenta a outra. Falha: a instância volta para a imagem anterior e o deploy sai sem tocar na seguinte. Interrompido entre a parada e a subida, imprime `RESIDUO <instância> ficou parada`; o Caddy atende pela outra e o deploy seguinte a sobe primeiro.
5. Dashboard e admin na tag nova, depois das APIs.
6. `ambientes/<ambiente>.imagens.env` registra as quatro imagens em execução; um `docker compose` à mão passa `--env-file ambientes/<ambiente>.env --env-file ambientes/<ambiente>.imagens.env`.

| Código | Significado |
|---|---|
| 0 | implantado |
| 2 | uso (ambiente, tag ou `PAF_REGISTRO` inválidos) |
| 3 | outro deploy do ambiente segura a trava |
| 4 | conferência reprovou; nada foi tocado |
| 5 | imagem indisponível; nada foi tocado |
| 6 | infraestrutura ou migrator falhou; nenhuma instância foi tocada |
| 7 | uma instância falhou e voltou à imagem anterior, saudável; a seguinte ficou intocada |
| 8 | uma instância falhou e não voltou (sem imagem anterior, ou a anterior também sem saúde): ação manual |
| 9 | APIs na tag nova; dashboard ou admin falharam e voltaram às imagens anteriores |
| 10 | dashboard ou admin falharam e não voltaram |
| 11 | a outra instância parou de responder saúde no meio do deploy; esta não foi tocada, mas o migrator já rodou e as instâncias anteriores desta execução estão na tag nova: repetir o deploy ou fazer o rollback |

Homologação e produção têm as duas instâncias, então nenhum deploy deixa a API fora do ar. Interrompido (SIGTERM do `timeout 1800`) entre a parada e a subida de uma instância, ela fica parada e o Caddy atende pela outra; o deploy seguinte a encontra sem contêiner e a sobe primeiro.

Passo humano antes do primeiro deploy com este script: o túnel do ambiente criado e o `cloudflared` no ar (seção "Túnel e Caddy"). Num host que rodava só `api-a`, o primeiro deploy sobe `api-b` antes de parar `api-a`. Um `COMPOSE_PROFILES=duas-instancias` que tenha ficado no arquivo de ambiente não tem mais efeito e pode sair.

### ensaio-deploy.sh

```bash
deploy/paf/ensaio-deploy.sh paf-api:<tag> paf-dbmigrator:<tag> paf-dashboard:<tag>   # local, nunca na VPS
deploy/paf/deploy.test.sh                                                           # caminhos de falha, sem Docker
```

Um contêiner Ubuntu 22.04 faz o papel do host (`docker compose`, `flock`, `curl`) na rede da VM do Docker (`--network host`), com a mesma árvore que o `implantar-remoto.sh` envia. O `cloudflared` não roda no ensaio, porque precisa de uma credencial de túnel real e conectaria na Cloudflare; o `deploy.sh` também nunca o sobe. No lugar dele, os clientes do ensaio entram na rede do projeto e chegam ao Caddy com o host público, e o CA7 manda `CF-Connecting-IP` como o `cloudflared` faz. Confere: primeira implantação com o Caddy roteando por host (API 200, dashboard 200, host desconhecido 404) e nenhum contêiner do `cloudflared`; deploy sob o walk em laço pelo Caddy (totem com retry pela mesma chave, guichê com chamar, iniciar e encerrar, painel e guichê no hub com reconexão), com o upgrade direto do hub em 101 sem negociação (CA1); troca registrada das duas instâncias, uma de cada vez; segundo deploy simultâneo com 3 pelo `flock`; imagem sem saúde com 7, a instância de volta e a outra intocada (mesmo contêiner); migrator que falha com 6 sem tocar instância; nenhum 5xx na emissão, fichas novas iguais às chaves com 200, toda senha chamada no painel, todo `EventoId` de `SenhaChamada` da caixa de saída recebido pelo hub ou recuperado pela reconsulta do painel ao reconectar (o evento enviado na janela entre o WebSocket fechado e o novo não chega pelo hub; no ensaio de 19/10/2026, 1 de 495), `AtividadesSessao` e `SessoesEstacao` inalteradas (CA8); `docker kill` de `api-a`, `api-b` e `api-a` com o walk no ar e todo cliente do hub reconectado na outra instância em até 30 s (CA3, três quedas; os erros dessas quedas propositais ficam fora do critério de deploy e são impressos à parte); zero `access_token=` e zero JWT no log do Caddy, com as linhas do hub presentes como controle, e, por último, com as duas APIs paradas, o hub com um token marcado respondendo 502 ou 503 sem o marcador em nenhuma linha de log (CA6); IP auditado no login igual ao `CF-Connecting-IP` com `X-Forwarded-For` forjado, o forjado ignorado também sem `CF-Connecting-IP` e pela porta de loopback da API, chamada do host (CA7, antes do walk). Se o walk reprovar, imprime as linhas de erro dos logs de todos os contêineres da API do ensaio, inclusive dos já recriados. Não cobre: a Cloudflare na frente (borda, túnel real, réplicas do `cloudflared`, CA2, CA4, CA5, CA9 e CA10), TLS real, imagens puxadas do GHCR.
