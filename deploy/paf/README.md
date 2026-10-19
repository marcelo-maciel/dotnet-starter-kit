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

`verificar-ambiente.test.sh` prova cada caso negativo sobre um par gerado dos arquivos `.exemplo` (sobe nenhum contêiner).

### ensaio-ambientes.sh

Ensaio local da topologia, só em projetos `paf-ensaio-*` (roda `down -v`): sobe os dois projetos, confere `SHOW timezone`, o preflight CORS com `X-Paf-Unidade` (204, e a origem do outro ambiente recusada), o `/config.json` do dashboard, que uma emissão na homologação não publica nada no Valkey de produção em 5 s (com a emissão na produção como controle positivo) e que o `down -v` da homologação deixa produção rodando. A massa demo do ensaio entra pelo `seed-demo` em `Development` dentro da rede do projeto; o seed da homologação de verdade é O09 (D19).
