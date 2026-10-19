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

## Prazo de inatividade do dashboard (O15, D04)

O guarda de inatividade do kit desloga quem fica 20 minutos sem interagir, o que soltaria a licença e o guichê do atendente parado no posto. Pela decisão D04 (Marcelo Maciel, 17/10/2026), o prazo passa a ser um só para todas as telas do dashboard, da ordem de um turno: 8 horas de inatividade (`inactivityIdleMs: 28800000`) mais 1 minuto de aviso (`inactivityWarningMs: 60000`). Risco aceito: uma tela de cadastro esquecida aberta continua logada durante esse prazo.

O valor vem de `deploy/paf/dashboard/config.json.template`, que substitui o template do kit sem editá-lo. Contrato com o compose do PAF (O02): o contêiner do dashboard, em todos os ambientes, monta esse arquivo por volume em `/etc/fsh/config.json.template:ro` e recebe `FSH_API_URL` e `FSH_DEFAULT_TENANT`, que o entrypoint do kit substitui por `envsubst` ao subir.

Os prazos ficam como número literal, nunca como variável de ambiente: uma variável esquecida vira texto vazio ou string, e o `env.ts` do kit cai em silêncio nos 20 minutos para qualquer valor que não seja número positivo. O template também não liga `demoMode`; se a homologação precisar do seletor de conta de demonstração, ela monta um template próprio com a mesma regra.

```bash
docker build -t paf-dashboard:smoke clients/dashboard
deploy/paf/smoke-inatividade.sh paf-dashboard:smoke
```

O smoke sobe a imagem duas vezes, com e sem a montagem, e confere no `/config.json` servido que o prazo é número igual a 8 horas com o template do PAF e que ele não existe sem a montagem. Código de saída 0 só com tudo verde; remove os contêineres ao sair. Prova negativa: com o valor entre aspas no template, o smoke sai com código 1.
