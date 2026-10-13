using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using SmartPlc.Api.History;

namespace SmartPlc.Api.Persistence;

/// <summary>
/// Usada só pelas ferramentas do EF (<c>dotnet ef migrations ...</c>, <c>dotnet ef database update</c>).
/// Lê ConnectionStrings:SmartPlc das variáveis de ambiente (ConnectionStrings__SmartPlc) ou do user-secrets,
/// sem subir a aplicação. Sem nenhuma das duas, usa um destino fictício: serve para gerar scripts SQL,
/// mas <c>database update</c> falhará (o que é o desejado: nunca se aplica nada por engano).
/// </summary>
public sealed class SmartPlcDbContextFactory : IDesignTimeDbContextFactory<SmartPlcDbContext>
{
    public SmartPlcDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(typeof(SmartPlcDbContextFactory).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var conexao = DbConnection.Resolve(config)
                      ?? "Host=localhost;Port=1;Database=smartplc_somente_script;Username=design;Password=design";

        return new SmartPlcDbContext(new DbContextOptionsBuilder<SmartPlcDbContext>().UseNpgsql(conexao).Options);
    }
}
