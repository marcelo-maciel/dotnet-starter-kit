using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.PAF
{
    /// <inheritdoc />
    public partial class InitialPaf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "paf");

            migrationBuilder.CreateTable(
                name: "Unidades",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    DocumentoFiscal = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Ativa = table.Column<bool>(type: "boolean", nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Unidades", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AtividadesSessao",
                schema: "paf",
                columns: table => new
                {
                    SessaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UltimaAtividadeEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AtividadesSessao", x => x.SessaoId);
                    table.ForeignKey(
                        name: "FK_AtividadesSessao_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContratosUnidade",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    InicioVigencia = table.Column<DateOnly>(type: "date", nullable: false),
                    FimVigencia = table.Column<DateOnly>(type: "date", nullable: true),
                    Plano = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    LimiteSessoes = table.Column<int>(type: "integer", nullable: false),
                    Politica = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContratosUnidade", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContratosUnidade_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Estacoes",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Configuracao = table.Column<string>(type: "jsonb", nullable: true),
                    Ativa = table.Column<bool>(type: "boolean", nullable: false),
                    UltimaConexaoEmissaoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UltimaQuedaEmissaoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UltimaConexaoPainelEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UltimaQuedaPainelEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Estacoes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Estacoes_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Parametros",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nivel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Chave = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Valor = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parametros", x => x.Id);
                    table.CheckConstraint("CK_Parametros_NivelUnidade", "(\"Nivel\" = 'Empresa') = (\"UnidadeId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Parametros_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Prioridades",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Valor = table.Column<int>(type: "integer", nullable: false),
                    Ativa = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prioridades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Prioridades_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Setores",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Setores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Setores_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UsuariosUnidade",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordem = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsuariosUnidade", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsuariosUnidade_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SessoesEstacao",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EstacaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessaoId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcessaoId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcessaoExpiraEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SelecionadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LiberadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessoesEstacao", x => x.Id);
                    table.CheckConstraint("CK_SessoesEstacao_DonoUnico", "(\"SessaoId\" IS NULL) <> (\"ConcessaoId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_SessoesEstacao_Estacoes_EstacaoId",
                        column: x => x.EstacaoId,
                        principalSchema: "paf",
                        principalTable: "Estacoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VinculosEstacao",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstacaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ResgatadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevogadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CredencialEmissaoHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CredencialPainelHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VinculosEstacao", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VinculosEstacao_Estacoes_EstacaoId",
                        column: x => x.EstacaoId,
                        principalSchema: "paf",
                        principalTable: "Estacoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VinculosEstacao_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EstacaoPrioridades",
                schema: "paf",
                columns: table => new
                {
                    EstacaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrioridadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordem = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstacaoPrioridades", x => new { x.EstacaoId, x.PrioridadeId });
                    table.ForeignKey(
                        name: "FK_EstacaoPrioridades_Estacoes_EstacaoId",
                        column: x => x.EstacaoId,
                        principalSchema: "paf",
                        principalTable: "Estacoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EstacaoPrioridades_Prioridades_PrioridadeId",
                        column: x => x.PrioridadeId,
                        principalSchema: "paf",
                        principalTable: "Prioridades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EstacaoPrioridades_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Filas",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SetorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NomeExibicao = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    HoraInicial = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    HoraFinal = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Fonte = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CorFonte = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CorBotaoSelecionado = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Ativa = table.Column<bool>(type: "boolean", nullable: false),
                    UltimaChamadaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Filas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Filas_Setores_SetorId",
                        column: x => x.SetorId,
                        principalSchema: "paf",
                        principalTable: "Setores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Filas_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EstacaoFilas",
                schema: "paf",
                columns: table => new
                {
                    EstacaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Papel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FilaId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordem = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstacaoFilas", x => new { x.EstacaoId, x.Papel, x.FilaId });
                    table.ForeignKey(
                        name: "FK_EstacaoFilas_Estacoes_EstacaoId",
                        column: x => x.EstacaoId,
                        principalSchema: "paf",
                        principalTable: "Estacoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EstacaoFilas_Filas_FilaId",
                        column: x => x.FilaId,
                        principalSchema: "paf",
                        principalTable: "Filas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EstacaoFilas_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Fichas",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilaId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrioridadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Letra = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Numero = table.Column<int>(type: "integer", nullable: false),
                    IdentificadorPublico = table.Column<Guid>(type: "uuid", nullable: false),
                    Canal = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EstacaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChaveIdempotencia = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DataOperacional = table.Column<DateOnly>(type: "date", nullable: false, computedColumnSql: "(\"CriadoEm\" AT TIME ZONE 'America/Sao_Paulo')::date", stored: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fichas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Fichas_Estacoes_EstacaoId",
                        column: x => x.EstacaoId,
                        principalSchema: "paf",
                        principalTable: "Estacoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Fichas_Filas_FilaId",
                        column: x => x.FilaId,
                        principalSchema: "paf",
                        principalTable: "Filas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Fichas_Prioridades_PrioridadeId",
                        column: x => x.PrioridadeId,
                        principalSchema: "paf",
                        principalTable: "Prioridades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Fichas_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FilaPrioridades",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilaId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrioridadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Letra = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    NumeroInicial = table.Column<int>(type: "integer", nullable: false),
                    CorBotao = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Ordem = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilaPrioridades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilaPrioridades_Filas_FilaId",
                        column: x => x.FilaId,
                        principalSchema: "paf",
                        principalTable: "Filas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FilaPrioridades_Prioridades_PrioridadeId",
                        column: x => x.PrioridadeId,
                        principalSchema: "paf",
                        principalTable: "Prioridades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FilaPrioridades_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Atendimentos",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    FichaId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilaId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrioridadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstacaoId = table.Column<Guid>(type: "uuid", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    Estado = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DataOperacional = table.Column<DateOnly>(type: "date", nullable: false, computedColumnSql: "(\"CriadoEm\" AT TIME ZONE 'America/Sao_Paulo')::date", stored: true),
                    DataElegibilidade = table.Column<DateOnly>(type: "date", nullable: false, computedColumnSql: "(\"ElegivelDesde\" AT TIME ZONE 'America/Sao_Paulo')::date", stored: true),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ElegivelDesde = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ChamadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UltimaRechamadaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IniciadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EncerradoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CanceladoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AtendimentoOrigemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Versao = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Atendimentos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Atendimentos_Atendimentos_AtendimentoOrigemId",
                        column: x => x.AtendimentoOrigemId,
                        principalSchema: "paf",
                        principalTable: "Atendimentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Atendimentos_Estacoes_EstacaoId",
                        column: x => x.EstacaoId,
                        principalSchema: "paf",
                        principalTable: "Estacoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Atendimentos_Fichas_FichaId",
                        column: x => x.FichaId,
                        principalSchema: "paf",
                        principalTable: "Fichas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Atendimentos_Filas_FilaId",
                        column: x => x.FilaId,
                        principalSchema: "paf",
                        principalTable: "Filas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Atendimentos_Prioridades_PrioridadeId",
                        column: x => x.PrioridadeId,
                        principalSchema: "paf",
                        principalTable: "Prioridades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Atendimentos_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AtosImpersonacao",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcessaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtendimentoId = table.Column<Guid>(type: "uuid", nullable: true),
                    RegistradoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AtosImpersonacao", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AtosImpersonacao_Atendimentos_AtendimentoId",
                        column: x => x.AtendimentoId,
                        principalSchema: "paf",
                        principalTable: "Atendimentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AtosImpersonacao_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Rechamadas",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtendimentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstacaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RechamadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rechamadas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rechamadas_Atendimentos_AtendimentoId",
                        column: x => x.AtendimentoId,
                        principalSchema: "paf",
                        principalTable: "Atendimentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rechamadas_Estacoes_EstacaoId",
                        column: x => x.EstacaoId,
                        principalSchema: "paf",
                        principalTable: "Estacoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rechamadas_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Atendimentos_AtendimentoOrigemId",
                schema: "paf",
                table: "Atendimentos",
                column: "AtendimentoOrigemId");

            migrationBuilder.CreateIndex(
                name: "IX_Atendimentos_FilaId",
                schema: "paf",
                table: "Atendimentos",
                column: "FilaId");

            migrationBuilder.CreateIndex(
                name: "IX_Atendimentos_PrioridadeId",
                schema: "paf",
                table: "Atendimentos",
                column: "PrioridadeId");

            migrationBuilder.CreateIndex(
                name: "IX_Atendimentos_UnidadeData",
                schema: "paf",
                table: "Atendimentos",
                columns: new[] { "UnidadeId", "DataOperacional" });

            migrationBuilder.CreateIndex(
                name: "IX_Atendimentos_UnidadeFilaData",
                schema: "paf",
                table: "Atendimentos",
                columns: new[] { "UnidadeId", "FilaId", "DataOperacional" });

            migrationBuilder.CreateIndex(
                name: "UX_Atendimentos_EstacaoEmCurso",
                schema: "paf",
                table: "Atendimentos",
                columns: new[] { "EstacaoId", "TenantId" },
                unique: true,
                filter: "\"Estado\" IN ('Chamado', 'EmAtendimento')");

            migrationBuilder.CreateIndex(
                name: "UX_Atendimentos_FichaEmCurso",
                schema: "paf",
                table: "Atendimentos",
                columns: new[] { "FichaId", "TenantId" },
                unique: true,
                filter: "\"Estado\" IN ('Chamado', 'EmAtendimento')");

            migrationBuilder.CreateIndex(
                name: "IX_AtividadesSessao_UnidadeAtividade",
                schema: "paf",
                table: "AtividadesSessao",
                columns: new[] { "UnidadeId", "UltimaAtividadeEm" });

            migrationBuilder.CreateIndex(
                name: "IX_AtividadesSessao_UsuarioId",
                schema: "paf",
                table: "AtividadesSessao",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_AtosImpersonacao_AtendimentoId",
                schema: "paf",
                table: "AtosImpersonacao",
                column: "AtendimentoId");

            migrationBuilder.CreateIndex(
                name: "IX_AtosImpersonacao_ConcessaoId",
                schema: "paf",
                table: "AtosImpersonacao",
                column: "ConcessaoId");

            migrationBuilder.CreateIndex(
                name: "IX_AtosImpersonacao_UnidadeId",
                schema: "paf",
                table: "AtosImpersonacao",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_ContratosUnidade_Vigencia",
                schema: "paf",
                table: "ContratosUnidade",
                columns: new[] { "UnidadeId", "InicioVigencia" });

            migrationBuilder.CreateIndex(
                name: "IX_EstacaoFilas_FilaId",
                schema: "paf",
                table: "EstacaoFilas",
                column: "FilaId");

            migrationBuilder.CreateIndex(
                name: "IX_EstacaoFilas_UnidadeId",
                schema: "paf",
                table: "EstacaoFilas",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_EstacaoPrioridades_PrioridadeId",
                schema: "paf",
                table: "EstacaoPrioridades",
                column: "PrioridadeId");

            migrationBuilder.CreateIndex(
                name: "IX_EstacaoPrioridades_UnidadeId",
                schema: "paf",
                table: "EstacaoPrioridades",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_Estacoes_UnidadeId",
                schema: "paf",
                table: "Estacoes",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_Fichas_FilaId",
                schema: "paf",
                table: "Fichas",
                column: "FilaId");

            migrationBuilder.CreateIndex(
                name: "IX_Fichas_PrioridadeId",
                schema: "paf",
                table: "Fichas",
                column: "PrioridadeId");

            migrationBuilder.CreateIndex(
                name: "IX_Fichas_UnidadeData",
                schema: "paf",
                table: "Fichas",
                columns: new[] { "UnidadeId", "DataOperacional" });

            migrationBuilder.CreateIndex(
                name: "UX_Fichas_ChaveIdempotencia",
                schema: "paf",
                table: "Fichas",
                columns: new[] { "EstacaoId", "ChaveIdempotencia", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Fichas_IdentificadorPublico",
                schema: "paf",
                table: "Fichas",
                columns: new[] { "IdentificadorPublico", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Fichas_Numeracao",
                schema: "paf",
                table: "Fichas",
                columns: new[] { "UnidadeId", "FilaId", "PrioridadeId", "DataOperacional", "Numero", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilaPrioridades_PrioridadeId",
                schema: "paf",
                table: "FilaPrioridades",
                column: "PrioridadeId");

            migrationBuilder.CreateIndex(
                name: "UX_FilaPrioridades_FilaPrioridade",
                schema: "paf",
                table: "FilaPrioridades",
                columns: new[] { "FilaId", "PrioridadeId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_FilaPrioridades_Letra",
                schema: "paf",
                table: "FilaPrioridades",
                columns: new[] { "UnidadeId", "Letra", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Filas_SetorId",
                schema: "paf",
                table: "Filas",
                column: "SetorId");

            migrationBuilder.CreateIndex(
                name: "IX_Filas_UnidadeId",
                schema: "paf",
                table: "Filas",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_Parametros_UnidadeId",
                schema: "paf",
                table: "Parametros",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "UX_Parametros_Chave",
                schema: "paf",
                table: "Parametros",
                columns: new[] { "Chave", "UnidadeId", "TenantId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_Prioridades_UnidadeId",
                schema: "paf",
                table: "Prioridades",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_Rechamadas_AtendimentoId",
                schema: "paf",
                table: "Rechamadas",
                column: "AtendimentoId");

            migrationBuilder.CreateIndex(
                name: "IX_Rechamadas_EstacaoId",
                schema: "paf",
                table: "Rechamadas",
                column: "EstacaoId");

            migrationBuilder.CreateIndex(
                name: "IX_Rechamadas_UnidadeId",
                schema: "paf",
                table: "Rechamadas",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "UX_SessoesEstacao_ConcessaoAtiva",
                schema: "paf",
                table: "SessoesEstacao",
                columns: new[] { "ConcessaoId", "TenantId" },
                unique: true,
                filter: "\"LiberadoEm\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_SessoesEstacao_EstacaoAtiva",
                schema: "paf",
                table: "SessoesEstacao",
                columns: new[] { "EstacaoId", "TenantId" },
                unique: true,
                filter: "\"LiberadoEm\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_SessoesEstacao_SessaoAtiva",
                schema: "paf",
                table: "SessoesEstacao",
                columns: new[] { "SessaoId", "TenantId" },
                unique: true,
                filter: "\"LiberadoEm\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Setores_UnidadeId",
                schema: "paf",
                table: "Setores",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_UsuariosUnidade_UnidadeId",
                schema: "paf",
                table: "UsuariosUnidade",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "UX_UsuariosUnidade_UsuarioUnidade",
                schema: "paf",
                table: "UsuariosUnidade",
                columns: new[] { "UsuarioId", "UnidadeId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VinculosEstacao_EstacaoId",
                schema: "paf",
                table: "VinculosEstacao",
                column: "EstacaoId");

            migrationBuilder.CreateIndex(
                name: "IX_VinculosEstacao_UnidadeId",
                schema: "paf",
                table: "VinculosEstacao",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "UX_VinculosEstacao_LinkHash",
                schema: "paf",
                table: "VinculosEstacao",
                columns: new[] { "LinkHash", "TenantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AtividadesSessao",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "AtosImpersonacao",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "ContratosUnidade",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "EstacaoFilas",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "EstacaoPrioridades",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "FilaPrioridades",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Parametros",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Rechamadas",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "SessoesEstacao",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "UsuariosUnidade",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "VinculosEstacao",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Atendimentos",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Fichas",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Estacoes",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Filas",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Prioridades",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Setores",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Unidades",
                schema: "paf");
        }
    }
}
