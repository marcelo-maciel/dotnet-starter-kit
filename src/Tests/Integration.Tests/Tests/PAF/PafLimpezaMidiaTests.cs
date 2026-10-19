using Amazon.S3;
using Amazon.S3.Model;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Storage;
using FSH.Framework.Storage;
using FSH.Framework.Storage.DTOs;
using FSH.Framework.Storage.Services;
using FSH.Modules.PAF.Midias;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// M09 (RF03, ADR 0007): the cleanup job against real PostgreSQL and the collection's RustFS. Each test gets fresh
/// tenants (xUnit builds the class per test), so the counts it asserts are its own. The R2 bucket lock
/// cannot be reproduced on RustFS: "the store refuses the delete" is a decorator whose remove does nothing (SKIPPED here,
/// proven against the real R2 by M11).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafLimpezaMidiaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Bucket = FshWebApplicationFactory.S3Bucket;
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(60);
    private readonly PafTenantCliente _tenant = new(factory, "paf-lm");
    private readonly PafTenantCliente _outro = new(factory, "paf-lm2");
    private readonly List<string> _chaves = [];

    public Task InitializeAsync() => Task.WhenAll(_tenant.InicializarAsync(), _outro.InicializarAsync());

    public async Task DisposeAsync()
    {
        using var prazo = new CancellationTokenSource(Prazo);
        var s3 = factory.Services.GetRequiredService<IAmazonS3>();
        foreach (var chave in _chaves)
        {
            await s3.DeleteObjectAsync(Bucket, chave, prazo.Token);
        }
    }

    [Fact]
    public async Task Vencidos_ApagadosECarimbados_29DiasEReferenciadoEOutroTenantIntocados_SegundaPassadaNaoRefaz()
    {
        var unidade = _tenant.UnidadeInicial;
        var referenciado = await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Confirmado, comObjeto: true);
        await MidiaAsync(_tenant, unidade, referenciado);
        var substituido = await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Confirmado, comObjeto: true);
        var recusado = await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Recusado, comObjeto: true);
        var nuncaConfirmado = await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Aberto, comObjeto: true);
        var nuncaEnviado = await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Aberto, comObjeto: false);
        var recente = await EnvioAsync(_tenant, unidade, dias: 29, Desfecho.Confirmado, comObjeto: true);
        var deOutroTenant = await EnvioAsync(_outro, _outro.UnidadeInicial, dias: 31, Desfecho.Recusado, comObjeto: true);

        var primeira = await RodarAsync(_tenant);

        primeira.ShouldBe(new ResultadoLimpezaMidia(Removidos: 4, Falhas: 0, Indeterminados: 0, SemResposta: false));
        foreach (var apagado in new[] { substituido, recusado, nuncaConfirmado })
        {
            (await ExisteAsync(apagado.Chave)).ShouldBeFalse();
            (await EstadoAsync(_tenant, apagado.EnvioId)).RemovidoEm.ShouldNotBeNull();
        }

        (await EstadoAsync(_tenant, nuncaEnviado.EnvioId)).RemovidoEm.ShouldNotBeNull();
        // The stale open envio is closed before its object goes, so a late confirmation cannot attach it.
        (await EstadoAsync(_tenant, nuncaConfirmado.EnvioId)).RecusadoEm.ShouldNotBeNull();

        foreach (var (tenant, intocado) in new[] { (_tenant, referenciado), (_tenant, recente), (_outro, deOutroTenant) })
        {
            (await ExisteAsync(intocado.Chave)).ShouldBeTrue();
            var estado = await EstadoAsync(tenant, intocado.EnvioId);
            estado.RemovidoEm.ShouldBeNull();
        }

        (await EstadoAsync(_tenant, recente.EnvioId)).RecusadoEm.ShouldBeNull();

        (await RodarAsync(_tenant)).ShouldBe(new ResultadoLimpezaMidia(0, 0, 0, SemResposta: false));
    }

    [Fact]
    public async Task DuasPassadasEmParalelo_SemErro_CadaEnvioRemovidoUmaVez()
    {
        var unidade = _tenant.UnidadeInicial;
        await MidiaAsync(_tenant, unidade, await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Confirmado, comObjeto: true));
        var vencidos = new List<EnvioChave>();
        for (var i = 0; i < 6; i++)
        {
            vencidos.Add(await EnvioAsync(_tenant, unidade, dias: 31 + i, i % 2 == 0 ? Desfecho.Aberto : Desfecho.Recusado, comObjeto: true));
        }

        var passadas = await Task.WhenAll(RodarAsync(_tenant), RodarAsync(_tenant));

        passadas.ShouldAllBe(p => !p.SemResposta && p.Falhas == 0 && p.Indeterminados == 0);
        passadas.Sum(p => p.Removidos).ShouldBe(vencidos.Count);
        foreach (var envio in vencidos)
        {
            (await ExisteAsync(envio.Chave)).ShouldBeFalse();
            (await EstadoAsync(_tenant, envio.EnvioId)).RemovidoEm.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task RemoveQueNaoApaga_DeixaRemovidoEmNulo_EReportaFalha()
    {
        var preso = await EnvioAsync(_tenant, _tenant.UnidadeInicial, dias: 31, Desfecho.Recusado, comObjeto: true);
        var real = factory.Services.GetRequiredService<IStorageService>();

        var resultado = await LimparComAsync(new ArmazenamentoDeTeste(real) { RemoveIgnorado = true }, NovoJob(TimeSpan.FromSeconds(5)));

        resultado.ShouldBe(new ResultadoLimpezaMidia(Removidos: 0, Falhas: 1, Indeterminados: 0, SemResposta: false));
        (await ExisteAsync(preso.Chave)).ShouldBeTrue();
        (await EstadoAsync(_tenant, preso.EnvioId)).RemovidoEm.ShouldBeNull();
    }

    /// <summary>
    /// The kit's HEAD answers null for any store error. With no object seen in the pass, a null is not proof of absence:
    /// a store that is down must not get every envio stamped as removed while the objects stay.
    /// </summary>
    [Fact]
    public async Task HeadSempreNulo_SemArmazenamentoProvado_NadaCarimbado()
    {
        var unidade = _tenant.UnidadeInicial;
        await MidiaAsync(_tenant, unidade, await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Confirmado, comObjeto: true));
        var vencido = await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Recusado, comObjeto: true);
        var semObjeto = await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Recusado, comObjeto: false);
        var real = factory.Services.GetRequiredService<IStorageService>();

        var resultado = await LimparComAsync(new ArmazenamentoDeTeste(real) { HeadSempreNulo = true, RemoveIgnorado = true }, NovoJob(TimeSpan.FromSeconds(5)));

        resultado.ShouldBe(new ResultadoLimpezaMidia(Removidos: 0, Falhas: 0, Indeterminados: 2, SemResposta: false));
        (await EstadoAsync(_tenant, vencido.EnvioId)).RemovidoEm.ShouldBeNull();
        (await EstadoAsync(_tenant, semObjeto.EnvioId)).RemovidoEm.ShouldBeNull();
        (await ExisteAsync(vencido.Chave)).ShouldBeTrue();
    }

    /// <summary>
    /// The kit's provider catches the cancellation and answers null; the job must read that as "no answer" and stop the
    /// pass inside the call deadline, stamping nothing.
    /// </summary>
    [Fact]
    public async Task ArmazenamentoParado_PassadaParaDentroDoPrazo_SemCarimbo()
    {
        var vencido = await EnvioAsync(_tenant, _tenant.UnidadeInicial, dias: 31, Desfecho.Recusado, comObjeto: true);
        var prazoChamada = TimeSpan.FromMilliseconds(500);
        var job = NovoJob(prazoChamada);
        var real = factory.Services.GetRequiredService<IStorageService>();

        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var resultado = await LimparComAsync(new ArmazenamentoDeTeste(real) { HeadParado = true }, job);
        relogio.Stop();

        resultado.SemResposta.ShouldBeTrue();
        resultado.Removidos.ShouldBe(0);
        relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await EstadoAsync(_tenant, vencido.EnvioId)).RemovidoEm.ShouldBeNull();
    }

    /// <summary>
    /// A confirmation that wins between the selection and the job's closing stamp: the envio is now referenced, and the
    /// job leaves its object and row alone. The race is forced inside the probe HEAD, which runs after the selection.
    /// </summary>
    [Fact]
    public async Task ConfirmacaoQueGanhaDoFechamento_EnvioReferenciadoFicaIntocado()
    {
        var unidade = _tenant.UnidadeInicial;
        await MidiaAsync(_tenant, unidade, await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Confirmado, comObjeto: true));
        var atrasado = await EnvioAsync(_tenant, unidade, dias: 31, Desfecho.Aberto, comObjeto: true);
        var real = factory.Services.GetRequiredService<IStorageService>();
        var armazenamento = new ArmazenamentoDeTeste(real)
        {
            AntesDoPrimeiroHead = async () =>
            {
                await _tenant.ExecutarAsync($"""
                    UPDATE paf."EnviosMidia" SET "ConfirmadoEm" = now() WHERE "TenantId" = {_tenant.TenantId} AND "Id" = {atrasado.EnvioId}
                    """);
                await MidiaAsync(_tenant, unidade, atrasado);
            },
        };

        var resultado = await LimparComAsync(armazenamento, NovoJob(TimeSpan.FromSeconds(5)));

        resultado.ShouldBe(new ResultadoLimpezaMidia(0, 0, 0, SemResposta: false));
        (await ExisteAsync(atrasado.Chave)).ShouldBeTrue();
        var estado = await EstadoAsync(_tenant, atrasado.EnvioId);
        estado.RemovidoEm.ShouldBeNull();
        estado.RecusadoEm.ShouldBeNull();
    }

    private enum Desfecho
    {
        Aberto,
        Confirmado,
        Recusado,
    }

    /// <summary>The job as the host builds it, with the given per-call deadline; the store is passed per pass.</summary>
    private LimpezaMidia NovoJob(TimeSpan prazoChamada) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OpcoesMidia { PrazoArmazenamento = prazoChamada }),
            NullLogger<LimpezaMidia>.Instance);

    private async Task<ResultadoLimpezaMidia> RodarAsync(PafTenantCliente tenant)
    {
        using var prazo = new CancellationTokenSource(Prazo);
        using var scope = factory.Services.CreateScope();
        var info = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenant.TenantId);
        return await scope.ServiceProvider.GetRequiredService<LimpezaMidia>().ExecutarNoTenantAsync(info.ShouldNotBeNull(), prazo.Token);
    }

    private async Task<ResultadoLimpezaMidia> LimparComAsync(IStorageService armazenamento, LimpezaMidia job)
    {
        using var prazo = new CancellationTokenSource(Prazo);
        using var scope = factory.Services.CreateScope();
        return await job.LimparAsync(_tenant.Contexto(scope, unidadeCorrente: null), armazenamento, _tenant.TenantId, prazo.Token);
    }

    private async Task<EnvioChave> EnvioAsync(PafTenantCliente tenant, Guid unidadeId, int dias, Desfecho desfecho, bool comObjeto)
    {
        var formato = FormatosMidia.PorNomeDeArquivo("abertura.mp4").ShouldNotBeNull();
        var envio = ChaveObjetoMidia.NovoEnvio(tenant.TenantId, unidadeId, formato);
        DateTimeOffset? confirmado = desfecho == Desfecho.Confirmado ? DateTimeOffset.UtcNow.AddDays(-dias) : null;
        DateTimeOffset? recusado = desfecho == Desfecho.Recusado ? DateTimeOffset.UtcNow.AddDays(-dias) : null;
        await tenant.ExecutarAsync($"""
            INSERT INTO paf."EnviosMidia" ("Id", "UnidadeId", "ChaveObjeto", "Tipo", "ContentType", "CriadoEm", "ConfirmadoEm", "RecusadoEm", "TenantId")
            VALUES ({envio.EnvioId}, {unidadeId}, {envio.Chave}, 'Video', {formato.Mime}, now() - make_interval(days => {dias}),
                    {confirmado}, {recusado}, {tenant.TenantId})
            """);
        if (comObjeto)
        {
            using var prazo = new CancellationTokenSource(Prazo);
            _chaves.Add(envio.Chave);
            await factory.Services.GetRequiredService<IAmazonS3>().PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket,
                Key = envio.Chave,
                ContentBody = "m09",
                ContentType = formato.Mime,
            }, prazo.Token);
        }

        return envio;
    }

    private static Task<int> MidiaAsync(PafTenantCliente tenant, Guid unidadeId, EnvioChave envio) =>
        tenant.ExecutarAsync($"""
            INSERT INTO paf."Midias" ("Id", "UnidadeId", "Descricao", "Tipo", "EnvioId", "ChaveObjeto", "DuracaoSegundos", "Ativa", "TenantId")
            VALUES ({Guid.CreateVersion7()}, {unidadeId}, 'Vídeo', 'Video', {envio.EnvioId}, {envio.Chave}, 30, true, {tenant.TenantId})
            """);

    private async Task<bool> ExisteAsync(string chave)
    {
        using var prazo = new CancellationTokenSource(Prazo);
        try
        {
            await factory.Services.GetRequiredService<IAmazonS3>().GetObjectMetadataAsync(Bucket, chave, prazo.Token);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<EstadoEnvio> EstadoAsync(PafTenantCliente tenant, Guid envioId)
    {
        using var scope = factory.Services.CreateScope();
        return await tenant.Contexto(scope, unidadeCorrente: null).Database.SqlQuery<EstadoEnvio>($"""
            SELECT "RecusadoEm", "RemovidoEm" FROM paf."EnviosMidia" WHERE "TenantId" = {tenant.TenantId} AND "Id" = {envioId}
            """).SingleAsync();
    }

    private sealed record EstadoEnvio(DateTimeOffset? RecusadoEm, DateTimeOffset? RemovidoEm);

    /// <summary>The kit's storage with the failures the real store can show: a delete the lock refuses, a HEAD that hides errors.</summary>
    private sealed class ArmazenamentoDeTeste(IStorageService real) : IStorageService
    {
        public bool RemoveIgnorado { get; init; }
        public bool HeadSempreNulo { get; init; }
        public bool HeadParado { get; init; }
        public Func<Task>? AntesDoPrimeiroHead { get; set; }

        public async Task<StoredObjectMetadata?> HeadObjectAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            if (AntesDoPrimeiroHead is { } gancho)
            {
                AntesDoPrimeiroHead = null;
                await gancho();
            }

            if (HeadParado)
            {
                // Like S3StorageService: the cancellation is caught and the answer is null.
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }

            return HeadSempreNulo ? null : await real.HeadObjectAsync(storageKey, cancellationToken);
        }

        public Task RemoveAsync(string path, CancellationToken cancellationToken = default) =>
            RemoveIgnorado ? Task.CompletedTask : real.RemoveAsync(path, cancellationToken);

        public Task<string> UploadAsync<T>(FileUploadRequest request, FileType fileType, CancellationToken cancellationToken = default)
            where T : class => real.UploadAsync<T>(request, fileType, cancellationToken);

        public Task<FileDownloadResponse?> DownloadAsync(string path, CancellationToken cancellationToken = default) =>
            real.DownloadAsync(path, cancellationToken);

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => real.ExistsAsync(path, cancellationToken);

        public Task<long> GetSizeAsync(string path, CancellationToken cancellationToken = default) => real.GetSizeAsync(path, cancellationToken);

        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) =>
            real.CopyAsync(sourceKey, destinationKey, cancellationToken);

        public Task<PresignedUploadUrl> GenerateUploadUrlAsync(
            string storageKey, string contentType, long maxBytes, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            real.GenerateUploadUrlAsync(storageKey, contentType, maxBytes, ttl, cancellationToken);

        public Task<Uri> GenerateDownloadUrlAsync(
            string storageKey, TimeSpan ttl, string? responseContentDisposition = null, CancellationToken cancellationToken = default) =>
            real.GenerateDownloadUrlAsync(storageKey, ttl, responseContentDisposition, cancellationToken);

        public string BuildPublicUrl(string storageKey) => real.BuildPublicUrl(storageKey);
    }
}
