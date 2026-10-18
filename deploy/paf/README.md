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
