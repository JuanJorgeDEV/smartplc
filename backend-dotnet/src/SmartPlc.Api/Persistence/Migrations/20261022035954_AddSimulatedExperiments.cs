using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartPlc.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulatedExperiments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fonte",
                table: "experiment_sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "clp");

            migrationBuilder.AddColumn<string>(
                name: "SerieSimulada",
                table: "experiment_sessions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Fonte",
                table: "experiment_sessions");

            migrationBuilder.DropColumn(
                name: "SerieSimulada",
                table: "experiment_sessions");
        }
    }
}
