namespace SmartPlc.Api.Auth;

public static class Papeis
{
    /// <summary>Tudo, inclusive gerenciar usuários.</summary>
    public const string Admin = "admin";
    /// <summary>Envia comandos (quando habilitados) e cria/encerra experimentos.</summary>
    public const string Operador = "operador";
    /// <summary>Somente consulta.</summary>
    public const string Visualizador = "visualizador";

    public static readonly string[] Todos = [Admin, Operador, Visualizador];

    public static string Rotulo(string papel) => papel switch
    {
        Admin => "Administrador",
        Operador => "Operador",
        Visualizador => "Visualizador",
        _ => papel
    };
}

public static class Politicas
{
    public const string Operador = "operador";
    public const string Admin = "admin";
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Liga o login. Vazio = decide pelo ambiente: ligado fora de Development, desligado em Development (desenvolvimento local
    /// sem banco). Desligado, qualquer pessoa que alcance o servidor é tratada como administradora: use só em máquina própria.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Se não existir nenhum usuário, o primeiro administrador é criado com estes dados (a senha só vale para essa criação).</summary>
    public string AdminEmail { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    public string AdminName { get; set; } = "Administrador";

    /// <summary>Duração absoluta da sessão (o cookie não é renovado).</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>Tentativas erradas seguidas antes do bloqueio temporário da conta.</summary>
    public int MaxFailures { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    public int PasswordMinLength { get; set; } = 10;
    public const int PasswordMaxLength = 128;

    /// <summary>Iterações do PBKDF2-HMAC-SHA256 (recomendação OWASP atual: 600 000).</summary>
    public int Pbkdf2Iterations { get; set; } = 600_000;

    /// <summary>Por quanto tempo o servidor confia no que sabe sobre o usuário antes de reconferir no banco (revogação quase imediata).</summary>
    public int UserCacheSeconds { get; set; } = 20;

    /// <summary>Guarda as chaves do cookie no banco (padrão). Desligado, as sessões caem a cada reinício do servidor.</summary>
    public bool PersistKeysInDatabase { get; set; } = true;
}
