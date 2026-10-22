using System.Security.Cryptography;

namespace SmartPlc.Api.Auth;

/// <summary>
/// Hash de senha com PBKDF2-HMAC-SHA256 e sal aleatório por senha. Formato guardado: <c>pbkdf2-sha256$iterações$sal$hash</c> (base64).
/// A comparação é de tempo constante. Hashes com menos iterações que o configurado são reconhecidos para serem refeitos no próximo login.
/// </summary>
public static class PasswordHasher
{
    private const string Prefixo = "pbkdf2-sha256";
    private const int TamanhoSal = 16;
    private const int TamanhoHash = 32;

    public static string Hash(string senha, int iteracoes)
    {
        var sal = RandomNumberGenerator.GetBytes(TamanhoSal);
        var hash = Rfc2898DeriveBytes.Pbkdf2(senha, sal, iteracoes, HashAlgorithmName.SHA256, TamanhoHash);
        return $"{Prefixo}${iteracoes}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Verdadeiro se a senha confere. <paramref name="precisaRefazer"/> indica hash com menos iterações que <paramref name="iteracoesAtuais"/>.</summary>
    public static bool Verificar(string senha, string guardado, int iteracoesAtuais, out bool precisaRefazer)
    {
        precisaRefazer = false;
        var partes = guardado.Split('$');
        if (partes.Length != 4 || partes[0] != Prefixo || !int.TryParse(partes[1], out var iteracoes) || iteracoes is < 1 or > 10_000_000)
            return false;
        try
        {
            var sal = Convert.FromBase64String(partes[2]);
            var esperado = Convert.FromBase64String(partes[3]);
            var calculado = Rfc2898DeriveBytes.Pbkdf2(senha, sal, iteracoes, HashAlgorithmName.SHA256, esperado.Length);
            var ok = CryptographicOperations.FixedTimeEquals(calculado, esperado);
            precisaRefazer = ok && iteracoes < iteracoesAtuais;
            return ok;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Hash de uma senha que ninguém conhece, usado para gastar o mesmo tempo quando o usuário não existe.</summary>
    public static string HashDeFachada(int iteracoes) => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)), iteracoes);

    /// <summary>Regras de senha. Devolve a mensagem do problema, ou nulo se a senha serve.</summary>
    public static string? Validar(string? senha, string email, int minimo)
    {
        if (string.IsNullOrEmpty(senha)) return "informe a senha";
        if (senha.Length < minimo) return $"a senha precisa ter pelo menos {minimo} caracteres";
        if (senha.Length > AuthOptions.PasswordMaxLength) return $"a senha pode ter no máximo {AuthOptions.PasswordMaxLength} caracteres";
        if (senha.Trim().Length == 0) return "a senha não pode ser só espaços";
        if (senha.Distinct().Count() < 4) return "a senha é repetitiva demais";
        if (string.Equals(senha, email, StringComparison.OrdinalIgnoreCase)) return "a senha não pode ser igual ao e-mail";
        var comum = new[] { "1234567890", "0123456789", "password12", "senha12345", "qwertyuiop", "smartplc123", "administrador" };
        if (comum.Any(c => senha.Contains(c, StringComparison.OrdinalIgnoreCase)) && senha.Length < 16) return "essa senha é muito comum; escolha outra";
        return null;
    }
}
