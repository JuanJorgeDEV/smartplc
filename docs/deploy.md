# Supabase + Render — preparação (NADA foi publicado)

Fatos conferidos na documentação em 08/10/2026; reconfira antes de implantar (planos mudam).

## 1. Banco no Supabase

1. Crie um projeto no Supabase. Guarde a **senha do banco** (você a define; não a coloque em arquivos do repositório nem no chat).
2. Dashboard → **Connect** → copie a string do **Session pooler** (porta 5432, host `aws-…pooler.supabase.com`, usuário `postgres.<ref-do-projeto>`).
   - Por quê: a conexão direta (`db.<ref>.supabase.co`) usa IPv6 (IPv4 só com add-on pago) e o Render é IPv4. O *session mode* é IPv4, aceita *prepared statements* e serve a um servidor persistente. O *transaction mode* (6543) é para serverless e **não** deve ser usado aqui.
3. Use TLS obrigatório (`SSL Mode=Require`; o código já aplica isto fora de localhost). `Require` criptografa mas não valida o servidor; `VerifyFull` com o certificado raiz do projeto é a opção mais rígida.
4. Pool pequeno: o código usa `Maximum Pool Size=5` por padrão (o gravador usa uma conexão por vez).

### Credenciais — como preencher localmente (sem expor a senha)

Preferido, fora do repositório (user-secrets, já inicializado no projeto):

```bash
cd backend-dotnet
dotnet user-secrets set "ConnectionStrings:SmartPlc" "postgresql://postgres.<ref>:<SENHA>@aws-0-<regiao>.pooler.supabase.com:5432/postgres" --project src/SmartPlc.Api
```

Se a senha tiver caracteres especiais, codifique-os na URI (`@`→`%40`, `:`→`%3A`, `/`→`%2F`, `#`→`%23`) ou use o formato Npgsql: `Host=…;Port=5432;Username=postgres.<ref>;Password=…;Database=postgres`.

Alternativa por variável de ambiente (PowerShell): `$env:ConnectionStrings__SmartPlc = "…"`.

### Aplicar o esquema (só com sua autorização)

```bash
# 1) gerar o SQL e REVISAR antes de rodar
dotnet ef migrations script --idempotent --project src/SmartPlc.Api --output esquema.sql
# 2) aplicar: cole esquema.sql no SQL Editor do Supabase, ou
dotnet ef database update --project src/SmartPlc.Api          # usa a connection string acima
# (ou History__MigrateOnStartup=true uma única vez — desligado por padrão)
```

`dotnet-ef` ainda não está instalado como ferramenta do projeto: `dotnet new tool-manifest && dotnet tool install dotnet-ef` (local ao repositório, não global).
**A migration inicial foi escrita à mão** (o Smart App Control impediu o `dotnet ef` nesta máquina). Ela foi aplicada do zero num PostgreSQL 17 real pelos testes, e `MigrationSnapshotTests` confirma que modelo e snapshot estão em sincronia. Se quiser trocá-la por uma gerada pela ferramenta: apague `Persistence/Migrations/` e rode `dotnet ef migrations add InitialCreate --output-dir Persistence/Migrations`.

### Retenção agendada

Depois do esquema, rode `backend-dotnet/db/retencao-pg_cron.sql` no SQL Editor (Supabase Cron). Consulta de histórico de execuções: `cron.job_run_details`.

### Backups e acesso

Backups e limites dependem do plano (conferir no painel). O navegador **nunca** fala com o banco: só o backend, com a connection string no servidor. Não use a `service_role` key em lugar nenhum deste projeto — ela não é necessária (a conexão é Postgres direto). Se um dia habilitar a Data API do Supabase, ative RLS nas tabelas `telemetry_samples`, `process_events` e `control_commands`.

## 2. Backend no Render

- **Tipo:** Web Service, runtime **Docker**, `Dockerfile` em `backend-dotnet/Dockerfile`, *root directory* `backend-dotnet`.
- **Porta:** o Render define `PORT` (padrão 10000) e exige escuta em `0.0.0.0`. `Program.cs` já faz isso quando `PORT` existe e nenhuma URL foi configurada; localmente continua em `http://localhost:5000`.
- **Health check:** caminho `/health` (responde 200 sem consultar o banco — um health check dependente do banco faria o Render reiniciar o serviço, derrubando o tempo real, quando só o histórico estivesse fora). `/health/ready` mostra os contadores do histórico.
- **WebSocket/SignalR:** suportado pelo Render; o frontend reconecta sozinho.
- **Variáveis de ambiente** (painel do Render → Environment; nunca no GitHub):

| Variável | Valor |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (já no Dockerfile) |
| `ConnectionStrings__SmartPlc` | string do session pooler |
| `Ingest__ApiKey` | chave longa e aleatória (obrigatória: a aplicação **não sobe** sem ela em Production) |
| `Control__Enabled` | `false` (mantenha: controle remoto desligado na nuvem) |
| `Cors__AllowedOrigins__0` | só se o dashboard for servido de outra origem |
| `History__SampleIntervalSeconds`, `History__MinimumLevelChange` | opcionais |

- **Plano gratuito (conferido):** hiberna após 15 min sem tráfego de entrada (requisições HTTP e mensagens em WebSocket já abertas contam como atividade); retoma em ~1 min no próximo acesso; 750 h/mês por workspace; pode ser reiniciado a qualquer momento; sem disco persistente (por isso o histórico fica no Supabase). Telemetria do Node-RED a cada poucos segundos mantém o serviço acordado, mas não garante ausência de reinícios.
- **Rollback:** Render → Deploys → *Rollback* para um deploy anterior; o esquema do banco só avança por migrations (ao reverter o código, confirme que a versão antiga é compatível com o esquema).
- **Logs:** painel do Render (stdout). A connection string nunca é logada (só `usuario@host:porta/banco`).

## 3. Node-RED local → nuvem (só leitura)

O Node-RED local envia `POST https://<app>.onrender.com/api/telemetria` com o cabeçalho `X-Api-Key: <Ingest__ApiKey>`. A conexão é **de saída**; o PLCSIM e o editor do Node-RED não são expostos. Comandos ficam desligados (`Control__Enabled=false`) até existir autenticação, entrega e confirmação (Marcos 5+).

## Estado

| Item | Situação |
|---|---|
| Dockerfile | escrito, **não construído** (Docker parado nesta máquina) |
| Porta/`PORT`, health, chave obrigatória | implementados, verificados só por compilação |
| Conexão com o Supabase | **configurável; não testada** (sem credenciais) |
| Migrations em PostgreSQL 17 local (contêiner) | **aplicadas e testadas** |
| Migrations no Supabase | **não aplicadas** (aguardam sua autorização) |
| Deploy no Render | **não realizado** |
