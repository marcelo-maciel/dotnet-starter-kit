using System.Text.Json;
using System.Text.Json.Serialization;
using FSH.Modules.Billing.Contracts.Dtos;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// The plans the kit seeds are what the Codefi operator sees in the admin console: names in pt-BR and prices in BRL
/// (ADR 0008), with the kit's keys kept, since quota and the trial fallback resolve by key.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafPlanosSemeadosTests(FshWebApplicationFactory factory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task PlanosSemeados_NomesEmPortugues_EmReais_ComAsChavesDoKit()
    {
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        using var resposta = await root.GetAsync("/api/v1/billing/plans?includeInactive=true");
        string corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, corpo);
        var planos = JsonSerializer.Deserialize<List<BillingPlanDto>>(corpo, Json)!;

        var semeados = planos.Where(p => p.Key is "free" or "pro" or "pro-annual")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}|{p.Name}|{p.Currency}|{p.MonthlyBasePrice:0.##}|{p.AnnualPrice:0.##}")
            .ToList();

        semeados.ShouldBe(["free|Gratuito|BRL|0|", "pro|Pro|BRL|29|", "pro-annual|Pro anual|BRL|29|290"]);
    }
}
