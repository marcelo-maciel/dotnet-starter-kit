using System.Net;
using System.Net.Http.Headers;
using Amazon.S3;
using Amazon.S3.Model;
using FSH.Framework.Storage.S3;
using FSH.Framework.Storage.Services;
using FSH.Modules.PAF.Midias;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// M02 against real RustFS: the media key composed by the PAF goes through the kit's storage (presigned PUT, the path
/// M03 uses) and lands where ADR 0007 says, with the kit's <c>Storage:S3:Prefix</c> empty and set to <c>app</c>;
/// never under <c>public/</c>, and an unsigned GET of it is refused even with the <c>public/*</c> read policy every
/// stack applies. The stored key is read with the raw S3 client, never through <see cref="IStorageService"/>, which
/// would apply the prefix a second time.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafMidiaArmazenamentoTests
{
    private const string Bucket = FshWebApplicationFactory.S3Bucket;
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(30);

    private readonly FshWebApplicationFactory _factory;

    public PafMidiaArmazenamentoTests(FshWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PrefixoVazio_ChaveFinalEhAChaveDaMidia_EGetSemAssinaturaDa403()
    {
        var storage = _factory.Services.GetRequiredService<IStorageService>();

        await ConferirEnvioAsync(storage, prefixoDoKit: "");
    }

    [Fact]
    public async Task PrefixoApp_ChaveFinalGanhaOPrefixoNaFrente_EGetSemAssinaturaDa403()
    {
        var storage = new S3StorageService(
            _factory.Services.GetRequiredService<IAmazonS3>(),
            _factory.Services.GetRequiredKeyedService<IAmazonS3>(S3StorageService.PresignClientKey),
            Options.Create(new S3StorageOptions
            {
                Bucket = Bucket,
                ServiceUrl = _factory.S3ServiceUrl,
                ForcePathStyle = true,
                PublicRead = false,
                Region = "us-east-1",
                Prefix = "app",
            }),
            NullLogger<S3StorageService>.Instance);

        await ConferirEnvioAsync(storage, prefixoDoKit: "app/");
    }

    private async Task ConferirEnvioAsync(IStorageService storage, string prefixoDoKit)
    {
        using var prazo = new CancellationTokenSource(Prazo);
        var s3 = _factory.Services.GetRequiredService<IAmazonS3>();
        await AplicarPoliticaDeLeituraPublicaAsync(s3, prazo.Token);

        var formato = FormatosMidia.PorNomeDeArquivo("Abertura da Loja.MP4")!;
        var envio = ChaveObjetoMidia.NovoEnvio("cliente-m02", Guid.CreateVersion7(), formato);
        var bytes = Guid.NewGuid().ToByteArray();
        var chaveFinal = prefixoDoKit + envio.Chave;
        var controlePublico = $"public/paf-m02-controle/{Guid.NewGuid():N}.txt";

        try
        {
            var url = await storage.GenerateUploadUrlAsync(envio.Chave, formato.Mime, bytes.Length, TimeSpan.FromMinutes(5), prazo.Token);
            using var http = new HttpClient { Timeout = Prazo };
            using (var conteudo = new ByteArrayContent(bytes))
            {
                conteudo.Headers.ContentType = MediaTypeHeaderValue.Parse(url.RequiredHeaders["Content-Type"]);
                using var put = await http.PutAsync(url.Url, conteudo, prazo.Token);
                put.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            // The object exists exactly at the expected key, with the type from the RF03 table.
            var metadados = await s3.GetObjectMetadataAsync(Bucket, chaveFinal, prazo.Token);
            metadados.ContentLength.ShouldBe(bytes.Length);
            metadados.Headers.ContentType.ShouldBe("video/mp4");
            chaveFinal.ShouldNotStartWith("public/");
            chaveFinal.ShouldEndWith(".mp4");

            // Signed GET reads it: the 403 below is about the missing signature, not a missing object.
            var leitura = await storage.GenerateDownloadUrlAsync(envio.Chave, TimeSpan.FromMinutes(5), null, prazo.Token);
            (await http.GetByteArrayAsync(leitura, prazo.Token)).ShouldBe(bytes);

            // Control: the public/* policy is live, so anonymous reads work where the bucket allows them.
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = controlePublico, ContentBody = "controle" }, prazo.Token);
            using var anonimo = new HttpClient { Timeout = Prazo };
            using var publico = await anonimo.GetAsync(new Uri($"{_factory.S3ServiceUrl}/{Bucket}/{controlePublico}"), prazo.Token);
            publico.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var semAssinatura = await anonimo.GetAsync(new Uri($"{_factory.S3ServiceUrl}/{Bucket}/{chaveFinal}"), prazo.Token);
            semAssinatura.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            using var limpeza = new CancellationTokenSource(Prazo);
            await s3.DeleteObjectAsync(Bucket, chaveFinal, limpeza.Token);
            await s3.DeleteObjectAsync(Bucket, controlePublico, limpeza.Token);
        }
    }

    // Same policy as the Files visibility tests apply to this shared bucket (anonymous s3:GetObject on public/* only),
    // so the order the collection runs them in does not change either outcome.
    private static Task<PutBucketPolicyResponse> AplicarPoliticaDeLeituraPublicaAsync(IAmazonS3 s3, CancellationToken ct) =>
        s3.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = Bucket,
            Policy = $$"""
                {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":["*"]},"Action":["s3:GetObject"],"Resource":["arn:aws:s3:::{{Bucket}}/public/*"]}]}
                """,
        }, ct);
}
