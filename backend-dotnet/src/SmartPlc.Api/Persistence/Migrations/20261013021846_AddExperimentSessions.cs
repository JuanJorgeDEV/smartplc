using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartPlc.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExperimentSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "experiment_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InicioEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FimEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PlcOnlineInicial = table.Column<bool>(type: "boolean", nullable: false),
                    NivelInicial = table.Column<double>(type: "double precision", nullable: true),
                    SetpointInicial = table.Column<double>(type: "double precision", nullable: true),
                    ValvulaInicial = table.Column<double>(type: "double precision", nullable: true),
                    ModoManualInicial = table.Column<bool>(type: "boolean", nullable: true),
                    NivelFinal = table.Column<double>(type: "double precision", nullable: true),
                    SetpointFinal = table.Column<double>(type: "double precision", nullable: true),
                    ValvulaFinal = table.Column<double>(type: "double precision", nullable: true),
                    ModoManualFinal = table.Column<bool>(type: "boolean", nullable: true),
                    Observacoes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_experiment_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_experiment_sessions_InicioEm",
                table: "experiment_sessions",
                column: "InicioEm");

            migrationBuilder.CreateIndex(
                name: "IX_experiment_sessions_Status",
                table: "experiment_sessions",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 'ativa'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "experiment_sessions");
        }
    }
}
