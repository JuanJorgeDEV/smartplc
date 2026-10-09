-- SmartPLC — quanto o histórico está ocupando e crescendo. Somente leitura.

-- Tamanho por tabela (dados + índices)
select relname as tabela,
       pg_size_pretty(pg_total_relation_size(c.oid)) as total,
       pg_size_pretty(pg_relation_size(c.oid))       as dados,
       pg_size_pretty(pg_indexes_size(c.oid))        as indices,
       c.reltuples::bigint                           as linhas_estimadas
  from pg_class c join pg_namespace n on n.oid = c.relnamespace
 where n.nspname = 'public' and c.relkind = 'r' and relname in ('telemetry_samples','process_events','control_commands');

-- Gravações por dia e por motivo (0=Initial 1=Periodic 2=Change 3=Event)
select date_trunc('day', "RecebidoEm") as dia, "MotivoGravacao" as motivo, count(*) as amostras
  from public.telemetry_samples
 group by 1, 2 order by 1 desc, 2;

-- Média de bytes por linha (para projetar crescimento)
select pg_total_relation_size('public.telemetry_samples')::numeric / nullif(count(*), 0) as bytes_por_amostra
  from public.telemetry_samples;

-- Tamanho do banco inteiro (limite do plano gratuito: confira o valor atual na documentação do Supabase)
select pg_size_pretty(pg_database_size(current_database())) as banco;
