using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartPlc.Api.Persistence.Migrations
{
    /// <summary>
    /// Liga o RLS (sem nenhuma política) e retira os privilégios dos papéis da Data API do Supabase.
    /// Motivo: o Supabase expõe o schema "public" por REST a quem tiver a chave anon (pública por natureza). O SmartPLC
    /// não usa a Data API: o backend conecta direto como "postgres" (BYPASSRLS), então nada muda para ele.
    /// Em PostgreSQL comum (testes, desenvolvimento) os papéis anon/authenticated não existem e os REVOKEs são pulados.
    /// Equivale a db/002_seguranca_rls.sql, agora aplicado junto com o esquema.
    /// </summary>
    public partial class EnableRowLevelSecurity : Migration
    {
        private static readonly string[] Tabelas =
        [
            "telemetry_samples", "process_events", "control_commands", "experiment_sessions", "__EFMigrationsHistory"
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var t in Tabelas)
            {
                migrationBuilder.Sql($"ALTER TABLE public.\"{t}\" ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
        REVOKE ALL ON TABLE public.""{t}"" FROM anon;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        REVOKE ALL ON TABLE public.""{t}"" FROM authenticated;
    END IF;
END $$;");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Desliga o RLS. Os privilégios revogados NÃO são devolvidos: reabrir a Data API é uma decisão consciente.
            foreach (var t in Tabelas)
                migrationBuilder.Sql($"ALTER TABLE public.\"{t}\" DISABLE ROW LEVEL SECURITY;");
        }
    }
}
