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

O que cada projeto sobe: PostgreSQL com `timezone=America/Sao_Paulo` na instância, Valkey próprio com senha e AOF (nunca compartilhado entre ambientes, ARQ l.367), o DbMigrator (`apply --seed`), `api-a`, o dashboard e o admin do kit. `api-b` fica no perfil `duas-instancias`, desligado: produção só sobe com duas instâncias depois de S03 e S04 mesclados (riscos do plano da fatia 2); a partir daí, `COMPOSE_PROFILES=duas-instancias` no arquivo de produção. Toda porta é publicada só em `127.0.0.1`, para o Apache do host (O03). A rede tem subnet fixa por ambiente, e é ela que entra em `TrustedProxyOptions:KnownNetworks` (ARQ l.380). As subnets `172.31.10.0/24` e `172.31.20.0/24` dos exemplos precisam ser conferidas contra as redes que já existem na VPS (`docker network inspect`) antes do primeiro `up`.

Prazo de inatividade (D04): `dashboard/config.json.template` é montado por volume sobre o template do kit na imagem do dashboard, com `inactivityIdleMs` vindo de `PAF_INATIVIDADE_MS` (8 horas nos exemplos, o mesmo prazo para todas as telas). A imagem e o entrypoint do kit não mudam.

### verificar-ambiente.sh

Renderiza os dois ambientes com `docker compose config` (todos os perfis ligados) e confere o modelo resolvido. Sai com código diferente de zero se faltar qualquer invariante:

- quotas do kit desligadas explicitamente (`QuotaOptions__Enabled=false`, ARQ l.384);
- `JwtOptions:SigningKey` com 32 caracteres ou mais, só ASCII imprimível, diferente do exemplo e diferente entre os ambientes;
- `AccessTokenMinutes` explícito e igual nos dois ambientes;
- `KnownNetworks` não vazio e igual à subnet da rede do projeto; subnets sem sobreposição;
- Valkey e PostgreSQL do próprio projeto, com as senhas do arquivo; nomes de projeto, portas e buckets do R2 diferentes;
- `x-paf-unidade` no CORS sem perder nenhum cabeçalho de `appsettings.Production.json`;
- prazos explícitos de PostgreSQL (`Timeout`, `Command Timeout`) e Valkey (`connectTimeout`, `syncTimeout`, `asyncTimeout`) até o teto de 30 s do teste de O19;
- `TZ=America/Sao_Paulo` na API, no DbMigrator e no PostgreSQL; `Storage:Provider=s3` (R2, ADR 0007);
- nenhum valor de exemplo (`<...>`), nenhum `container_name`, toda porta em `127.0.0.1`, Valkey sem porta publicada.
- observabilidade (O10, RNF06): `OTEL_EXPORTER_OTLP_ENDPOINT` em https, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, `OTEL_EXPORTER_OTLP_HEADERS` com `api-key=`, `OTEL_EXPORTER_OTLP_TIMEOUT` até 30 s, `OTEL_SERVICE_NAME` diferente entre os ambientes, o sink OTLP de `appsettings.Production.json` desligado pelo índice (`Serilog__WriteTo__1__Name=`, senão cada log vai duas vezes) e o console em JSON compacto.

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

## Vhost do Apache do host (O03, RNF02, RNF16)

`apache/paf-vhost.conf.template` é o vhost de um ambiente no Apache compartilhado da VPS (layout Debian/Ubuntu). Nunca editar a cópia gerada em `sites-available`: o template é a fonte.

- API: `balancer://<COMPOSE_PROJECT_NAME>` com `api-a` e, só quando `COMPOSE_PROFILES` tem `duas-instancias`, `api-b`, os dois em `127.0.0.1` com `upgrade=websocket` (opção B da ARQ seção 11: WebSockets com `SkipNegotiation`, sem cookie de afinidade), `connectiontimeout=3` e `retry=10` (o padrão de 60 s deixava a instância reiniciada fora do grupo, ARQ l.378); `ProxyTimeout 60`, acima do keepalive de 15 s do SignalR; `ProxyPreserveHost On` e `X-Forwarded-Proto https`.
- Dashboard e admin: um vhost 443 cada, proxy simples para a porta do ambiente; porta 80 só redireciona para https no mesmo host, caminho e query, para que HTTP desses hosts nunca caia no vhost padrão de outro cliente.
- Log sem query string em nenhuma rota (`%m %U %H`, nunca `%r`): hub do PAF, hub do kit e `/notifications` levam `?access_token=`.
- TLS: certificados em `/etc/letsencrypt/live/<host>/`. Sem o certificado, o `configtest` falha e nada é aplicado.

```bash
sudo deploy/paf/apache/conferir-apache.sh                                  # só leitura
sudo deploy/paf/apache/aplicar-vhost.sh deploy/paf/ambientes/producao.env  # um ambiente por vez
```

`conferir-apache.sh` confere versão 2.4.47 ou mais nova, MPM `event` e os módulos `ssl`, `proxy`, `proxy_http`, `proxy_balancer`, `slotmem_shm`, `lbmethod_byrequests` e `headers`, e imprime `Timeout`, `MaxRequestWorkers` e afins. Nunca habilita módulo: o que faltar sai com a linha `a2enmod` a rodar, um módulo por vez, com confirmação de Marcelo, `configtest` e `graceful`, e o p95 dos outros sites medido antes e depois. Rollback de um módulo: `a2dismod`, `configtest`, `graceful`.

`aplicar-vhost.sh` roda o conferir, gera o vhost, guarda o anterior em `<projeto>.conf.anterior`, instala, `a2ensite`, `configtest` e só então `graceful`. `configtest` falho restaura o anterior (ou desabilita o site novo), confere o estado restaurado e sai com código diferente de zero, sem `graceful`. Rollback do vhost: copiar o `.anterior` de volta (ou `a2dissite <projeto>`), `configtest` e `graceful`.

### Limite de conexões: bloqueia produção

No Apache 2.4.x, cada WebSocket aberto pelo túnel do `mod_proxy_http` prende uma thread do worker enquanto durar (a documentação 2.4 não tem diretiva de túnel assíncrono). Medido no ensaio abaixo, Apache 2.4.52 do Ubuntu 22.04 com o `mpm_event.conf` padrão (`MaxRequestWorkers 150`, o mesmo valor da Contabo): 150 WebSockets abriram, os outros 150 foram recusados, o log registrou `AH00484` e o próprio `GET /health` pelo Apache deixou de responder em 10 s, ou seja, os demais sites do host também param. Com `MaxRequestWorkers 600` e `ServerLimit 24` os mesmos 300 WebSockets ficaram 180 s abertos, com `graceful` no meio, sem perda e sem `AH00484`. O volume de D20 (20 unidades com 3 totens, 2 painéis e 8 guichês) dá 260 conexões do `PafHub`, fora as abas de cadastro. Aumentar `MaxRequestWorkers`/`ServerLimit` no Apache compartilhado ou pôr um proxy dedicado ao PAF é decisão de Marcelo antes da produção; o vhost não resolve isso sozinho. Conexões abertas também atravessam um `graceful` presas ao processo antigo, que só sai quando elas fecham; cada `graceful` com o PAF no ar ocupa slots de `ServerLimit` até lá.

### ensaio-apache.sh

```bash
deploy/paf/apache/ensaio-apache.sh spike-ws-app <conexoes> <segundos>
```

Ensaio local, nunca na VPS: Apache 2.4.52 do `ubuntu:22.04` em contêiner (`paf-ensaio-apache*`, removidos ao sair), o `aplicar-vhost.sh` rodando dentro dele sobre o vhost gerado do exemplo de produção com duas instâncias, certificados autoassinados e o hub do spike de WebSocket (`PAFV2/spikes/websocket/app`, imagem `spike-ws-app`) nas portas do ambiente. Confere redirecionamento, roteamento por host, handshake 101 em WebSockets, `docker kill` da instância conectada com reconexão na outra e retorno dela ao grupo, N conexões por S segundos com broadcast por minuto e `graceful` no meio, zero `access_token=` e zero marcador do token nos logs (com as linhas 101 do hub presentes como controle), zero `AH00484`/`AH00485`, `configtest` falho sem troca de arquivo nem `graceful`, e módulo ausente abortando antes de tocar no arquivo. Código 0 só com tudo verde. Não cobre: TLS real, Cloudflare ou outro proxy à frente, o Apache no host chegando pela rede do Docker, e o p95 dos outros sites da VPS.

## CI e promoção (O08, RNF16, RNF17)

`.github/workflows/paf.yml` é arquivo novo, registrado no ADR 0008 ("Workflow de CI do PAF"); os workflows do kit não mudam e continuam só em `main`. Roda em push para `main` e `feat/**` e em todo pull request, sem filtro de branch base, porque os PRs do PAF apontam para branches de feature.

Jobs obrigatórios, todos agregados no job `PAF CI`, que fica vermelho se qualquer um falhar, for cancelado ou pulado:

- `Backend PAF`: build da solução, `Architecture.Tests`, `PAF.Tests`, `PAF.Kestrel.Tests` e `Integration.Tests` com o filtro `FullyQualifiedName~Paf` (Testcontainers sobre o Docker do runner);
- `Dashboard PAF`: `npm run lint`, `npm run build` (inclui `tsc -b`) e os testes unitários do PAF por `node --test` (D21);
- `Scripts de implantação`: `exigir-homologacao-verde.test.mjs`, `scripts-ci.test.sh` e `verificar-ambiente.test.sh`;
- `Walk no Chrome`: `walk-ci.sh` sobe PostgreSQL e Valkey efêmeros, aplica migrações e `seed-demo` com a senha do secret, sobe a API e o servidor do Vite em portas livres e roda `paf-primeira-fatia.walk.mjs` no Google Chrome do runner. O código de saída é o do walk: um `FAIL` deixa o job vermelho. Sem o secret `PAF_SENHA_DEMO`, ou com ele igual à senha demo pública do kit, o job falha antes de subir qualquer coisa.
- `Imagem anterior contra esquema novo` (O07, RNF16): `imagem-anterior-esquema-novo.sh <ref-anterior>` compila a API da revisão anterior (base do PR, ou o commit anterior no push), aplica migrações e `seed-demo` com o DbMigrator atual num PostgreSQL efêmero, sobe a API anterior nesse esquema e roda `imagem-anterior-esquema-novo.mjs`: emissão no totem 200, seleção do guichê 204, chamar próxima 200 com a senha emitida e o painel com a chamada. A senha do seed é aleatória por execução, sem secret. Revisão anterior sem o módulo PAF imprime `NAO-SE-APLICA` e sai 0. `imagem-anterior-esquema-novo.test.sh` cobre os erros de uso (saída 2) sem subir nada. A barreira estática fica em `PAF.Tests` (`Esquema/BarreiraExpandirContrairTests`, `Contrato/ContratoEstacaoTests`).

```bash
PAF_SENHA_DEMO=<senha> deploy/paf/walk-ci.sh     # o mesmo walk, local; precisa de npm ci em clients/dashboard
deploy/paf/scripts-ci.test.sh                    # caminhos de falha dos scripts de CI, sem subir nada
node --test deploy/paf/exigir-homologacao-verde.test.mjs
```

Promoção:

- `Deploy homologação`: só em push para `main`, depois do `PAF CI` verde, no environment `homologacao`. Publica `paf-api`, `paf-dbmigrator`, `paf-dashboard` e `paf-admin` no GHCR com a tag igual ao SHA completo e chama `implantar-remoto.sh homologacao <sha>`.
- `Deploy produção`: só manual (`workflow_dispatch` a partir de `main`, com o SHA). O job `Exigir SHA verde em homologação` roda antes do environment `producao` pedir aprovação e falha se a implantação mais recente daquele SHA em homologação não terminou em `success` (`exigir-homologacao-verde.mjs`). A produção usa as mesmas imagens que passaram na homologação, sem rebuild.
- `implantar-remoto.sh <ambiente> <sha>` confere os segredos, copia `deploy/paf` para o host por SSH (os `ambientes/*.env` do host ficam onde estão) e roda lá `deploy/paf/deploy.sh <ambiente> <sha>` com `PAF_REGISTRO` no ambiente. O `deploy.sh` é da tarefa O06; enquanto ele não existir, os dois deploys falham com essa mensagem.

Configuração do repositório, fora do código:

- [ ] secret de repositório `PAF_SENHA_DEMO` (senha do `seed-demo` do walk, diferente de `Password123!`);
- [ ] environments `homologacao` e `producao`, com revisor obrigatório em `producao`, e em cada um os secrets `PAF_SSH_HOST`, `PAF_SSH_USUARIO`, `PAF_SSH_CHAVE`, `PAF_SSH_KNOWN_HOSTS` e `PAF_DIR_HOST`;
- [ ] `docker login ghcr.io` no host com um token de leitura de pacotes;
- [ ] tornar o job `PAF CI` verificação obrigatória na proteção das branches.

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
