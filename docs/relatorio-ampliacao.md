# SmartPLC: como conecta hoje e como crescer

Relatório em linguagem simples, de 22/10/2026. Parte do que **já funciona** (com o simulador) e mostra o que mudaria para um **CLP real** e para **várias malhas**, com exemplos. No fim, o que mais dá para ampliar, em ordem de prioridade.

---

## 1. Como a conexão funciona hoje (com o PLCSIM)

```
 PLCSIM / TIA Portal        Node-RED                 Servidor (Render)            Banco (Supabase)     Navegador
 ┌──────────────────┐  S7   ┌────────────┐  HTTPS    ┌─────────────────┐  SQL   ┌────────────┐      ┌──────────┐
 │ programa da malha │ ───▶ │ lê 8 tags  │ ───────▶ │ valida, guarda  │ ─────▶ │ histórico  │      │ painel   │
 │ (nível, válvula,  │      │ a cada ~1 s│  POST     │ o último estado │        └────────────┘      │ login    │
 │ PID)              │ ◀─── │ escreve    │ ◀─────── │ e transmite     │ ─────────────────────────▶ │ tempo    │
 └──────────────────┘ S7    │ comandos   │ resposta  │ (SignalR)       │                            │ real     │
                            └────────────┘ c/ comandos└─────────────────┘                            └──────────┘
```

**O ponto-chave:** a conexão **sempre parte do Node-RED para a nuvem** (de saída). Nada precisa ser aberto na rede do CLP. Os comandos voltam dentro da **resposta** da telemetria.

### O que o Node-RED lê (já configurado)

| Variável | Endereço no CLP | O que é |
|---|---|---|
| `nivel` | `DB3,REAL0` | nível do tanque, 0–100 % |
| `valvula` | `DB4,REAL0` | saída do controlador (MV), 0–100 % |
| `setpoint` | `DB5,REAL0` | nível desejado (SP) |
| `modoManual` | `DB5,X8.0` | 1 = manual, 0 = automático |
| `valvulaManual` | `DB5,REAL22` | abertura manual |
| `kp`, `ti`, `td` | `DB5,REAL10 / 14 / 18` | parâmetros do PID |

### A mensagem que o Node-RED envia (a cada ~1 s)

```json
POST https://smartplc.onrender.com/api/telemetria
X-Api-Key: <chave de ingestão>

{ "nivel": 55.6, "valvula": 38.5, "setpoint": 60, "modoManual": false,
  "valvulaManual": 12, "kp": 2.5, "ti": 15, "td": 0.5 }
```

E o servidor responde `{"status":"ok"}`, ou, se alguém pediu um comando no painel:

```json
{ "status": "ok", "comandos": [ { "id": "…", "campos": { "setpoint": 70 } } ] }
```

O Node-RED escreve `70` em `DB5,REAL0`. Na leitura seguinte, o CLP reporta `setpoint: 70` e **só então** o painel marca o comando como confirmado.

---

## 2. Como seria com um CLP real (S7-1200 / S7-1500)

O **caminho de dados é o mesmo**. O que muda está no CLP e na rede:

| Tema | PLCSIM (hoje) | CLP real |
|---|---|---|
| Endereço | IP virtual do PLCSIM | IP do CLP (ex.: `192.168.0.10`), rack 0, slot 1 |
| Permissão S7 | já liberada | no TIA: *Proteção → "Permitir acesso via PUT/GET"* e **DBs sem otimização** (acesso absoluto) |
| Sinais | simulados no programa | sensor 4–20 mA → entrada analógica → **escalonamento** para 0–100 % |
| Válvula | variável do programa | saída analógica 4–20 mA para o posicionador |
| Rede | tudo no mesmo PC | CLP em **rede industrial separada**; o Node-RED roda num PC/gateway ligado a ela |
| Falha | nada acontece | alarme, perda de comunicação e **modo seguro** precisam estar no CLP |

### Exemplo: nível de um tanque de 2 m

- Sensor ultrassônico LT-101, faixa 0–2 m, saída 4–20 mA.
- O módulo analógico entrega um número de 0 a 27648. No programa, um bloco de escala converte para **%**: `nivel = (valor / 27648) × 100`.
- Com 1,0 m de água: 12 mA → 50 %. É esse `50.0` que o Node-RED lê em `DB3,REAL0`.
- O **PID (`PID_Compact`) fica no CLP**. O SmartPLC só supervisiona e pede mudanças de setpoint/modo/PID. Se a internet cair, o controle continua funcionando.

### Cuidados que o simulador não ensina

1. **Nunca expor o CLP à internet.** O Node-RED na rede local é a ponte; o CLP não fala com a nuvem.
2. **Limites no CLP.** O painel e o fluxo já recusam valores fora de faixa, mas a defesa final é o programa do CLP (limitar SP e válvula, intertravamentos).
3. **Comando não é garantia.** Por isso o painel só confirma pela **releitura** do CLP.
4. **Quem pode comandar:** só operador/administrador, e cada comando deveria ficar registrado com o nome de quem pediu (ver ampliação 3).

### Alternativas ao S7 do Node-RED

| Protocolo | Quando usar |
|---|---|
| **OPC UA** | S7-1500 (servidor embutido) ou qualquer fabricante; tipos e segurança melhores que o S7 puro |
| **Modbus TCP** | CLPs e instrumentos de outras marcas (Schneider, Delta, inversores) |
| **MQTT** | vários dispositivos enviando para um broker; padrão de IIoT |

Para o servidor, **nada muda**: ele só espera o JSON acima. Trocar o protocolo é trabalhar só no Node-RED.

---

## 3. Como seria com várias malhas

Hoje existe **uma** malha (TQ-101) com 8 variáveis fixas. Para várias, a ideia é identificar cada uma:

```json
{ "malha": "FT-201", "pv": 42.0, "cv": 55.0, "setpoint": 45, "modoManual": false,
  "kp": 1.2, "ti": 8, "td": 0 }
```

| Malha | Variável controlada | Atuador | Resposta típica |
|---|---|---|---|
| TQ-101 (existe) | nível | válvula de entrada | lenta (dezenas de segundos) |
| FT-201 | vazão | válvula de controle | rápida (segundos) |
| TT-301 | temperatura | resistência / válvula de vapor | muito lenta, com atraso |
| PT-401 | pressão | válvula de alívio | rápida |

**O que precisaria mudar:** um cadastro de malhas no banco (nome, unidade, limites, endereços), a tela com seletor de malha, histórico e laboratório por malha, e o simulador com um modelo por tipo de processo. É um trabalho **médio a grande**, mas a base (login, fila de comandos, histórico) já serve para todas.

---

## 4. O que mais dá para ampliar

Ordem sugerida para o Projeto Integrador (impacto alto e custo baixo primeiro).

| # | Ampliação | O que entrega | Esforço |
|---|---|---|---|
| 1 | **Publicar e testar com o CLP/PLCSIM do colega** | provar a cadeia completa de verdade | pequeno |
| 2 | **Sintonia assistida** | a partir do degrau do Laboratório, sugere Kp/Ti/Td (Ziegler-Nichols, Cohen-Coon) e mostra a curva prevista × real | médio |
| 3 | **Auditoria de comandos** | quem pediu cada comando, quando e se foi confirmado (a tabela já está reservada) | pequeno |
| 4 | **Relatório do experimento** | exportar a sessão em CSV e PDF (gráfico, métricas, observações) para anexar ao trabalho | pequeno |
| 5 | **Alarmes com notificação** | histórico de alarmes, "quem reconheceu" e aviso por e-mail ou Telegram | médio |
| 6 | **Indicadores de desempenho** | IAE/ISE, tempo de acomodação e comparação lado a lado de várias sessões | pequeno |
| 7 | **Teste automático na nuvem (CI)** | os 500 testes rodam a cada alteração no GitHub | pequeno |
| 8 | **Manter o banco acordado** | aviso/rotina para o Supabase gratuito não pausar; monitor de disponibilidade | pequeno |
| 9 | **Segurança extra** | recuperação de senha por e-mail, segundo fator, chave de ingestão por dispositivo | médio |
| 10 | **Modo professor** | ver as sessões dos alunos, turmas e correção guiada | médio |
| 11 | **Agente próprio no lugar do Node-RED** | programa em .NET na rede do CLP, com OPC UA/S7 e envio de dados | grande |
| 12 | **Várias malhas** | seção 3 acima | grande |
| 13 | **App no celular (PWA)** | instalar o painel e receber alarmes | médio |

### Três ideias que mais valorizam o trabalho acadêmico

1. **Real × simulação na mesma tela.** O Laboratório já simula; sobrepor a curva do PLCSIM mostra o quanto o modelo representa o processo (e permite identificar o modelo a partir do degrau real).
2. **Sintonia assistida (item 2).** Transforma o painel de "supervisório" em ferramenta de ensino de controle.
3. **Auditoria + relatório (itens 3 e 4).** Dá rastreabilidade, algo que todo sistema industrial exige.

### Riscos para ter em mente

- **Plano gratuito:** o Render dorme após 15 min sem uso e o Supabase pausa após ~7 dias sem atividade. Para demonstração, abra antes; para uso contínuo, plano pago ou rotina de manutenção.
- **Comando em planta real** exige análise de risco, intertravamentos no CLP e botão de emergência independente do software.
- **Dados reais** pedem política de retenção, cópia de segurança e conformidade (LGPD se houver dados de pessoas além do login).
