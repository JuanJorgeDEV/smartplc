-- SmartPLC — segurança das tabelas no Supabase. Rode no SQL Editor DEPOIS de 001_esquema_inicial.sql.
-- Idempotente: pode rodar mais de uma vez.
--
-- Por quê: o Supabase expõe o schema "public" pela Data API (REST) a quem tiver a URL do projeto e a chave anon/publishable,
-- que é pública por natureza (vai embutida em qualquer front-end). Tabelas criadas pelo SQL Editor nascem SEM RLS.
-- O SmartPLC não usa a Data API: o backend conecta direto no Postgres (role "postgres", que tem BYPASSRLS).
-- Então ligamos o RLS SEM nenhuma política = ninguém pela API vê ou altera nada; o backend segue funcionando normalmente.

alter table public.telemetry_samples enable row level security;
alter table public.process_events    enable row level security;
alter table public.control_commands  enable row level security;

-- Defesa em profundidade: ligar o RLS não revoga os privilégios; tirar também os grants dos papéis da API.
revoke all on table public.telemetry_samples from anon, authenticated;
revoke all on table public.process_events    from anon, authenticated;
revoke all on table public.control_commands  from anon, authenticated;
revoke all on table public."__EFMigrationsHistory" from anon, authenticated;
alter table public."__EFMigrationsHistory" enable row level security;

-- Conferir (as 4 linhas devem mostrar rls_ativo = true):
--   select relname as tabela, relrowsecurity as rls_ativo
--     from pg_class c join pg_namespace n on n.oid = c.relnamespace
--    where n.nspname = 'public' and relname in ('telemetry_samples','process_events','control_commands','__EFMigrationsHistory');
--
-- Teste de fumaça (de qualquer terminal; use a chave anon do SEU projeto; deve devolver [] ou erro de permissão, nunca dados):
--   curl "https://<ref>.supabase.co/rest/v1/telemetry_samples?select=*" -H "apikey: <chave-anon>" -H "Authorization: Bearer <chave-anon>"
--
-- Observação: o pg_cron (retencao-pg_cron.sql) roda como "postgres" e também ignora o RLS, então a limpeza continua funcionando.
