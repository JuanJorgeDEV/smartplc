# Laboratório SmartPLC

Experimentos guiados **sobre a telemetria real da malha TQ-101**. Não existe uma segunda simulação no frontend: a sessão é gravada no servidor com os valores recebidos do CLP e o resumo é calculado com as amostras já persistidas no histórico.

## O que existe

| Experimento | Objetivo | Resultado |
|---|---|---|
| 1 · Resposta ao setpoint | Ver como a PV reage a uma mudança de SP | Gráfico, resumo e métricas do degrau (quando os dados sustentam) |
| 2 · Automático versus manual | Entender a diferença entre os modos | Tempo, PV mín/máx e CV médio por modo; mudanças de modo reportadas |

Fluxo: **preparar → iniciar → alterar (Controle, TIA Portal ou Node-RED) → observar → encerrar → revisar**. O Laboratório **não cria outro caminho de comando** e **não alterna o modo sozinho**. Com comandos desativados, a sessão funciona como observação: a mudança é feita fora e o SmartPLC a identifica quando o **CLP a reporta** (o marcador vertical é o instante da telemetria, não o do pedido).

## Persistência (migration `AddExperimentSessions`)

Tabela `experiment_sessions` (RLS ligado no Supabase por `db/002_seguranca_rls.sql`). Guarda só o enquadramento; a telemetria **não é copiada**: consulta-se `telemetry_samples` e `process_events` pela janela `InicioEm..FimEm` (há um único processo).

| Campo | Observação |
|---|---|
| `Id` (uuid), `Nome` (≤80), `Tipo`, `Status` (`ativa`/`encerrada`/`cancelada`), `InicioEm`, `FimEm` | Índice único parcial: **no máximo uma sessão ativa** (também protege de duas requisições simultâneas) |
| `NivelInicial`, `SetpointInicial`, `NivelFinal`, `SetpointFinal` | Previstos no briefing |
| `ValvulaInicial/Final`, `ModoManualInicial/Final` | **Acrescentados**: o passo "registrar PV, SP e CV iniciais" exige a CV; o modo é o dado central do experimento 2 |
| `PlcOnlineInicial` | **Acrescentado**: evidência de que a sessão começou com comunicação válida (o servidor recusa iniciar sem ela) |
| `Observacoes` (≤500) | Texto livre do usuário |

Os valores de partida e de chegada vêm do **estado que o servidor recebeu do CLP**, nunca do navegador (o corpo da requisição não os aceita). Se o CLP não está respondendo ao encerrar, os valores finais ficam **vazios**: o último valor não é repetido como se fosse atual.

Recuperação: a sessão ativa vive no banco. Atualizar a página ou reiniciar o servidor não a perde (`GET /api/experimentos?status=ativa`); a revisão de sessões antigas é feita pelo banco (`#/laboratorio?sessao=<id>`).

## API

| Rota | Função | Erros principais |
|---|---|---|
| `GET /api/experimentos?status=&limite=` | Lista (mais novas primeiro) | 400 parâmetros; 503 sem banco |
| `POST /api/experimentos` `{tipo, nome?, observacoes?}` | Inicia (201) | 400 campo/tipo inválido ou desconhecido; **409 sem telemetria válida**; **409 já existe sessão ativa** (devolve `sessaoAtiva`); 403 escrita desligada; 503 |
| `GET /api/experimentos/{id}` | Sessão + `resumo` + `serie` (≤ 600 pontos, preserva picos) + `eventos` | 404; 503 |
| `POST /api/experimentos/{id}/encerrar` `{observacoes?}` | Encerra e grava os valores finais | 404; 409 já encerrada |
| `POST /api/experimentos/{id}/cancelar` | Cancela | idem |

Acesso: leitura aberta (como o restante da supervisão). Escrita pode ser desligada com `Lab__WriteEnabled=false` (403). **Em ambiente público deixe `Lab__WriteEnabled=false` até existir autenticação** (Marco 7): a escrita não toca o CLP, mas grava no banco. O Laboratório exige o histórico ligado (`ConnectionStrings:SmartPlc`); sem banco responde 503 e a tela explica, sem inventar dados.

## Métricas do experimento 1 (critérios documentados)

Calculadas por `Lab/ExperimentAnalyzer.cs` sobre as amostras persistidas, com interpolação linear entre amostras vizinhas. **Só são devolvidas quando os critérios abaixo valem**; caso contrário vem o motivo.

| Critério | Valor |
|---|---|
| Degrau de SP mínimo | ≥ 5 pontos |
| Distância mínima da PV ao novo SP no instante do degrau | ≥ 5 pontos |
| Amostras após o degrau | ≥ 6 |
| Resolução (mediana do intervalo entre amostras após o degrau) | ≤ 10 s |
| Tempo até 63 % | ≥ 3 amostras até o cruzamento |
| Subida 10→90 % | ≥ 5 amostras até 90 % |
| Sobre-elevação | só se a PV entrar na banda de ±2 % do degrau (mín. 0,5) |
| Estabilização | após a última amostra fora da banda, com ≥ 3 amostras seguidas dentro dela |

Considera apenas o **primeiro** degrau da sessão. A resolução vem da política de histórico (periódica, por variação ≥ 0,5 e por evento): em transitórios rápidos há uma amostra por variação relevante; em regime, uma a cada 10 s.

**Verificação:** `ExperimentAnalyzerTests` compara contra soluções analíticas (1ª ordem: t63 = τ, 10→90 % = τ·(ln 10 − ln(10/9)), estabilização em 2 % = τ·ln 50; 2ª ordem sub-amortecida: sobre-elevação = exp(−πζ/√(1−ζ²))), e cobre os casos em que **não** calcula. Um teste ponta a ponta usa a política de gravação real e uma resposta de 1ª ordem.

## Conteúdo educativo

Microexplicações estáticas (`wwwroot/js/glossary.js`), sem IA: CLP, processo, PV, SP, CV, malha fechada, PID, Kp, Ti, Td, modo manual/automático, degrau, tempo até 63 %, sobre-elevação e estabilização. São conceituais e dizem "em geral": o efeito real depende do PID implementado no CLP.

## Limitações

- Os dados das demonstrações e capturas vieram do **simulador do Node-RED** (`tools/simulador-nodered.js`), não do PLCSIM. Os números reais dependem do teste da equipe.
- A sobre-elevação em degraus para baixo é a ultrapassagem do novo SP no sentido do movimento (subamortecimento), calculada da mesma forma.
- Sem comunicação durante a sessão, o resumo reflete apenas as amostras que existem; o servidor não preenche lacunas.
