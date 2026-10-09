using Microsoft.Extensions.Configuration;
using Npgsql;
using SmartPlc.Api.History;

namespace SmartPlc.Api.Tests;

/// <summary>Leitura da connection string: nada de banco, nada de rede.</summary>
public class DbConnectionTests
{
    private static NpgsqlConnectionStringBuilder N(string raw) => new(DbConnection.Normalize(raw));

    [Fact]
    public void Uri_do_Supabase_pooler_e_convertida_preservando_usuario_com_ponto()
    {
        var b = N("postgresql://postgres.abcdefgh:s3nha@aws-0-sa-east-1.pooler.supabase.com:5432/postgres");

        Assert.Equal("aws-0-sa-east-1.pooler.supabase.com", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("postgres.abcdefgh", b.Username);
        Assert.Equal("s3nha", b.Password);
        Assert.Equal("postgres", b.Database);
    }

    [Fact]
    public void Caracteres_especiais_codificados_na_senha_da_uri_sao_decodificados()
    {
        Assert.Equal("p@ss:w/rd#1", N("postgres://u:p%40ss%3Aw%2Frd%231@db.exemplo.com/postgres").Password);
    }

    [Fact]
    public void Uri_sem_porta_ou_banco_usa_5432_e_postgres()
    {
        var b = N("postgres://u:p@db.exemplo.com");
        Assert.Equal(5432, b.Port);
        Assert.Equal("postgres", b.Database);
    }

    [Fact]
    public void Servidor_remoto_exige_TLS_por_padrao()
    {
        Assert.Equal(SslMode.Require, N("Host=db.exemplo.com;Username=u;Password=p;Database=d").SslMode);
        Assert.Equal(SslMode.Require, N("postgresql://u:p@db.exemplo.com/d").SslMode);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Banco_local_nao_forca_TLS(string host)
    {
        Assert.Equal(SslMode.Prefer, N($"Host={host};Username=u;Password=p;Database=d").SslMode);
    }

    [Fact]
    public void Modo_de_TLS_explicito_e_respeitado()
    {
        Assert.Equal(SslMode.VerifyFull, N("Host=db.exemplo.com;Username=u;Password=p;Database=d;SSL Mode=VerifyFull").SslMode);
        Assert.Equal(SslMode.VerifyFull, N("postgresql://u:p@db.exemplo.com/d?sslmode=verify-full").SslMode);
    }

    [Fact]
    public void Pool_pequeno_por_padrao_e_configuravel()
    {
        Assert.Equal(5, N("Host=h;Username=u;Password=p;Database=d").MaxPoolSize);
        Assert.Equal(20, N("Host=h;Username=u;Password=p;Database=d;Maximum Pool Size=20").MaxPoolSize);
    }

    [Fact]
    public void Resolve_devolve_null_quando_nao_configurada()
    {
        Assert.Null(DbConnection.Resolve(new ConfigurationBuilder().Build()));
        Assert.Null(DbConnection.Resolve(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SmartPlc"] = "  " }).Build()));
    }

    [Fact]
    public void Resolve_le_ConnectionStrings_SmartPlc()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SmartPlc"] = "postgresql://u:p@db.exemplo.com/d"
        }).Build();
        Assert.Contains("db.exemplo.com", DbConnection.Resolve(cfg));
    }

    [Fact]
    public void Descricao_para_log_nunca_contem_a_senha()
    {
        var texto = DbConnection.Describe(DbConnection.Normalize("postgresql://u:SEGREDO-123@db.exemplo.com/d"));
        Assert.DoesNotContain("SEGREDO", texto);
        Assert.Contains("db.exemplo.com", texto);
    }
}
