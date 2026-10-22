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

Há dois caminhos equivalentes; o esquema é **idempotente** (rodar duas vezes não quebra nada).

**Caminho 1 (recomendado, o mais visível):** abra `backend-dotnet/db/001_esquema_inicial.sql`, copie o conteúdo e cole no **SQL Editor** do Supabase. Cria `telemetry_samples`, `process_events`, `control_commands` e `__EFMigrationsHistory`.

**Caminho 2 (pela ferramenta do EF, usando a connection string do user-secrets):**

```bash
cd backend-dotnet
dotnet tool restore                                           # instala o dotnet-ef LOCAL do repositório (dotnet-tools.json)
dotnet tool run dotnet-ef database update --project src/SmartPlc.Api
```

O comando lê `ConnectionStrings:SmartPlc` do user-secrets (ou de `ConnectionStrings__SmartPlc`) e **falha** se não houver, em vez de apontar para outro lugar.
Para regerar o SQL: `dotnet tool run dotnet-ef migrations script --idempotent --project src/SmartPlc.Api -o db/001_esquema_inicial.sql`.

A migration inicial foi escrita à mão (o Smart App Control impediu o `dotnet ef` naquele dia). Depois, o `dotnet ef migrations has-pending-model-changes` oficial confirmou **"No changes have been made to the model since the last migration"**, ela foi aplicada do zero num PostgreSQL 17 real pelos testes, e `MigrationSnapshotTests` guarda essa sincronia.


### Retenção agendada

Depois do esquema, rode `backend-dotnet/db/retencao-pg_cron.sql` no SQL Editor (Supabase Cron). Consulta de histórico de execuções: `cron.job_run_details`.

### Segurança: ative o RLS (sim) — `db/002_seguranca_rls.sql`

O Supabase expõe o schema `public` pela Data API (REST) a quem tiver a URL do projeto e a chave anon/publishable, que é pública por natureza. Tabelas criadas pelo SQL Editor **nascem sem RLS**. O SmartPLC não usa a Data API (o backend conecta direto no Postgres como `postgres`, que tem `BYPASSRLS`), então:

- Rode `002_seguranca_rls.sql` logo depois do esquema: liga o RLS **sem políticas** (ninguém pela API vê ou altera nada) e retira os grants de `anon`/`authenticated`.
- O backend e o pg_cron seguem funcionando normalmente (ignoram o RLS).
- Não crie políticas "liberando" nada. Se um dia o navegador precisar ler o histórico, ele deve passar pelo backend (`/api/historico`), não pela Data API.
- O mesmo vale para a `service_role`/secret key: não é necessária em nenhum ponto deste projeto e nunca deve ir ao front-end nem ao GitHub.

### Backups e acesso

Backups e limites dependem do plano (conferir no painel). O navegador **nunca** fala com o banco: só o backend, com a connection string no servidor.

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
| `Auth__AdminEmail`, `Auth__AdminPassword` | e-mail e senha do **primeiro administrador** (criado só se ainda não houver nenhum usuário). Preencha no painel do Render, nunca no repositório. Depois do primeiro acesso, troque a senha e apague `Auth__AdminPassword`. Ver [autenticacao.md](autenticacao.md) |
| `Auth__Enabled` | não precisa definir: fora de Development o login vem **ligado** (e exige o banco) |
| `Control__Enabled` | `false` (mantenha: controle remoto desligado na nuvem) |
| `Cors__AllowedOrigins__0` | só se o dashboard for servido de outra origem |
| `History__SampleIntervalSeconds`, `History__MinimumLevelChange` | opcionais |
| `Lab__WriteEnabled` | `true`: com login, só operadores e administradores criam/encerram sessões (grava no banco; o CLP não é tocado) |
| `Integration__ShowAddresses` | `false` se não quiser divulgar os endereços S7 na aba Integrações |
| `Command__ConfirmationSeconds` | opcional (padrão 10) |

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


## Checklist para a publicação (V2) — nada disto foi executado

**Supabase** (nesta ordem, no SQL Editor): `db/001_esquema_inicial.sql` (agora inclui `experiment_sessions`), `db/002_seguranca_rls.sql` (RLS nas 5 tabelas), `db/retencao-pg_cron.sql`. Conferir o `rls_ativo` e o teste da chave anon descritos no próprio script.
**Render:** Web Service Docker (`backend-dotnet/Dockerfile`), health check `/health`, variáveis da tabela acima, `Control__Enabled=false`, `Lab__WriteEnabled=false`, `Ingest__ApiKey` forte. O frontend vai junto (é `wwwroot`).
**Node-RED local:** `SMARTPLC_URL=https://<app>.onrender.com/api/telemetria` e `SMARTPLC_API_KEY` (fluxo `node-red/flows.smartplc-dotnet.json`).
**Login:** todas as rotas, exceto `/health`, a tela de entrada e `POST /api/telemetria` (que usa a chave de ingestão), exigem sessão. Sem `Auth__AdminEmail`/`Auth__AdminPassword` no primeiro deploy, ninguém consegue entrar: defina-os antes.
