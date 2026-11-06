using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Transportes aceitos pelo PafHub, em <c>PAF:TempoReal</c>. O padrão é só WebSockets, o transporte do front
/// (<c>paf-hub.ts</c>, SkipNegotiation): em long polling cada poll passa de novo pela checagem de sessão, e o aviso
/// SessaoInvalidada de uma revogação pode ficar na fila do poll recusado com 401. Long polling só no host do
/// Integration.Tests, cujo TestServer fecha o WebSocket antes do handshake; verificar-ambiente recusa o ajuste nos
/// arquivos de ambiente.
/// </summary>
public sealed class OpcoesTempoReal
{
    public const string Secao = "PAF:TempoReal";

    public HttpTransportType Transportes { get; set; } = HttpTransportType.WebSockets;

    public static void Registrar(IServiceCollection services)
    {
        services.AddOptions<OpcoesTempoReal>()
            .BindConfiguration(Secao)
            .Validate(o => o.Transportes != HttpTransportType.None, "PAF:TempoReal:Transportes não pode ser vazio.")
            .ValidateOnStart();
    }
}
