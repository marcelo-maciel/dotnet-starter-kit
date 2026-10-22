namespace FSH.Modules.PAF.Contracts.v1;

/// <summary>
/// Comando de escrita de cadastro do PAF cujo tratador persiste pelo <c>SaveChanges</c> do contexto do PAF. Sob
/// impersonação da Codefi, o comportamento de pipeline do módulo anota um <c>AtoImpersonacao</c> antes do tratador,
/// e a linha entra na mesma transação da escrita (seção 9, decisão D28 de 17/10/2026). Cada comando de escrita do PAF
/// leva esta marca ou está na lista de exclusões da matriz de <c>Architecture.Tests</c>, com motivo escrito.
/// Os comandos do guichê (S11) também a levam: escrevem por SQL bruto e gravam o ato pelo auxiliar dentro da própria
/// transação; o comportamento só confere, depois do tratador, que o ato anotado chegou ao banco.
/// </summary>
public interface IEscritaDeCadastro;
