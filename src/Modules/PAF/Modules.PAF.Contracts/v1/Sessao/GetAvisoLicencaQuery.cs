using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Sessao;

/// <summary>Aviso da ultrapassagem do limite de licenças (RF22, D03) para a sessão que entrou acima dele.</summary>
public sealed record GetAvisoLicencaQuery : IQuery<AvisoLicencaDto?>;

/// <summary>
/// A ocupação mais recente da sessão acima do limite contratado da unidade. <c>RegistroId</c> identifica a ocupação,
/// para a tela não repetir o aviso que o usuário já fechou.
/// </summary>
public sealed record AvisoLicencaDto(Guid RegistroId, Guid UnidadeId, string Unidade, int Ocupadas, int Limite, DateTimeOffset RegistradoEm);
