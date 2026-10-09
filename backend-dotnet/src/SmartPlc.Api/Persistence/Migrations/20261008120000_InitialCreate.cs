using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartPlc.Api.Persistence.Migrations
{
    // ATENÇÃO: escrita à mão no Marco 4 porque o Smart App Control do Windows impediu rodar `dotnet ef`
    // naquela máquina. Para trocar por uma versão gerada pela ferramenta, apague esta pasta e execute:
    //   dotnet ef migrations add InitialCreate --project src/SmartPlc.Api --output-dir Persistence/Migrations
    // Verifique sempre com:  dotnet ef migrations has-pending-model-changes --project src/SmartPlc.Api
    [DbContext(typeof(SmartPlcDbContext))]
    [Migration("20261008120000_InitialCreate")]
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "control_commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SolicitadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Estado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Campos = table.Column<string>(type: "jsonb", nullable: false),
                    Detalhe = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_commands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "process_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OcorridoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Detalhe = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ValorAnterior = table.Column<double>(type: "double precision", nullable: true),
                    ValorNovo = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_process_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_samples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RecebidoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AdquiridoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Nivel = table.Column<double>(type: "double precision", nullable: false),
                    Valvula = table.Column<double>(type: "double precision", nullable: false),
                    Setpoint = table.Column<double>(type: "double precision", nullable: true),
                    ModoManual = table.Column<bool>(type: "boolean", nullable: true),
                    MotivoGravacao = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telemetry_samples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_control_commands_SolicitadoEm",
                table: "control_commands",
                column: "SolicitadoEm");

            migrationBuilder.CreateIndex(
                name: "IX_process_events_OcorridoEm",
                table: "process_events",
                column: "OcorridoEm");

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_samples_AdquiridoEm",
                table: "telemetry_samples",
                column: "AdquiridoEm",
                unique: true,
                filter: "\"AdquiridoEm\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_samples_RecebidoEm",
                table: "telemetry_samples",
                column: "RecebidoEm");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "control_commands");
            migrationBuilder.DropTable(name: "process_events");
            migrationBuilder.DropTable(name: "telemetry_samples");
        }
    }
}
