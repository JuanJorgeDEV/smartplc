-- SmartPLC — retenção automática no Supabase (Supabase Cron = pg_cron).
-- EXECUTE MANUALMENTE no SQL Editor do projeto, DEPOIS de aplicar as migrations. Nada aqui roda sozinho.
--
-- Habilite o Cron no Dashboard (Integrations -> Cron). Se preferir SQL: create extension if not exists pg_cron;
-- (a forma exata de habilitar no seu plano deve ser conferida na documentação atual do Supabase.)
--
-- Política inicial (configurável: troque os números e rode de novo):
--   amostras brutas: 14 dias  |  eventos: 365 dias
-- Cada tarefa é um único DELETE por dia. Com a amostragem híbrida (dezenas de milhares de linhas por dia no pior caso)
-- ele é curto. Se um dia o volume crescer muito, reduza o intervalo da tarefa (ex.: de hora em hora) em vez de apagar tudo de uma vez.

select cron.schedule(
  'smartplc-retencao-amostras',
  '17 3 * * *',                                   -- todos os dias, 03:17 UTC
  $$ delete from public.telemetry_samples where "RecebidoEm" < now() - interval '14 days' $$
);

select cron.schedule(
  'smartplc-retencao-eventos',
  '23 3 * * *',                                   -- todos os dias, 03:23 UTC
  $$ delete from public.process_events where "OcorridoEm" < now() - interval '365 days' $$
);

-- Conferir:
--   select jobid, jobname, schedule, active from cron.job where jobname like 'smartplc-%';
--   select jobname, status, return_message, start_time, end_time
--     from cron.job_run_details d join cron.job j using (jobid)
--    where j.jobname like 'smartplc-%' order by start_time desc limit 20;
-- Mudar o prazo: rode cron.schedule de novo com o MESMO nome (atualiza a tarefa) e o novo intervalo.
-- Remover:
--   select cron.unschedule('smartplc-retencao-amostras');
--   select cron.unschedule('smartplc-retencao-eventos');
