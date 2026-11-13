# SmartPLC: documentação do projeto

Documento de trabalho, em linguagem simples, para a equipe editar. Atualizado em 29/10/2026.
Marcações usadas: **[feito]** existe e foi testado · **[no ar]** está publicado · **[hipótese]** ainda precisa ser confirmado com pessoas da indústria · **[a decidir]** depende de uma escolha da equipe.

---

## 1. Em um minuto

O **SmartPLC** é um painel web que mostra, em tempo real e de qualquer lugar, o que um CLP (o "computador" que controla uma máquina) está fazendo, guarda o histórico, deixa pessoas autorizadas dar comandos e permite **fazer experimentos de controle** sem risco. O caso usado é um **tanque com malha de nível**: um sensor mede o nível, o controlador (PID) decide quanto abrir a válvula, e o painel acompanha tudo.

Serve a dois mundos: **o chão de fábrica** (supervisionar e agir) e **a sala de aula** (entender como um controle responde, com simulação e sem quebrar nada).

---

## 2. A dor que originou o projeto

> Estas são as dores **conhecidas e plausíveis** na pequena e média indústria. Antes de usá-las no vídeo, vale **confirmar com 3 a 5 conversas** com quem trabalha com isso (técnico de manutenção, operador, supervisor). **[hipótese]**

**O dia a dia, sem o painel:**

1. **Para saber como a máquina está, é preciso ir até ela.** O operador caminha até o painel local ou olha um display no equipamento.
2. **O histórico não existe ou fica preso.** Quando algo dá errado ("o nível subiu de madrugada"), ninguém sabe exatamente quando nem por quê. Anotações em papel, planilhas, memória.
3. **Sistemas de supervisão tradicionais (SCADA) são caros e pesados.** Licença por ponto, computador dedicado, integrador especializado. Muita fábrica pequena fica sem.
4. **Quem aprende controle não tem onde testar.** Sintonizar um PID numa planta real é arriscado; em sala de aula costuma ser só teoria e gráfico de livro.
5. **Mudança de parâmetro sem rastro.** Alguém altera o setpoint, ninguém registra quem nem quando.
6. **Acesso inseguro.** Para ver o painel de fora, muita gente abre a rede industrial para a internet, o que é perigoso.

**O que o SmartPLC propõe como resposta:**

| Dor | Resposta no projeto |
|---|---|
| Ir até a máquina | painel web em tempo real, em qualquer navegador |
| Sem histórico | todo o processo gravado e consultável por período |
| Sistema caro | tecnologia aberta e hospedagem gratuita para começar |
| Sem onde praticar | Laboratório com simulação e análise automática da resposta |
| Mudança sem rastro | comandos só por login, com estados e confirmação pelo próprio CLP |
| Rede exposta | a conexão sai da fábrica para a nuvem; nada é aberto na rede do CLP |

---

## 3. O que o produto faz hoje

**Cinco telas [feito] [no ar]**

| Tela | Para quê |
|---|---|
| Visão Geral | o tanque animado com nível (PV), setpoint (SP) e válvula (MV), alarmes e tendência |
| Controle | enviar setpoint, modo manual/automático, válvula e PID, com confirmação e etapas |
| Histórico | consultar o que aconteceu, por período, com os eventos |
| Laboratório | experimentos guiados, com **simulação** ou com o CLP, e resumo automático |
| Integrações | estado da conexão, variáveis e **usuários** (administrador) |

**Segurança [feito]:** login com três papéis (administrador, operador, visualizador), bloqueio por tentativas, sessão que expira, senha guardada só como hash, banco com acesso fechado a quem não é o servidor.

**Regra de ouro dos comandos [feito]:** o painel **nunca finge**. Um comando só é dado como aplicado depois que o **CLP reporta** o novo valor. Sem resposta, ele expira e avisa.

---

## 4. Como funciona (visão simples)

```
 CLP / PLCSIM ──S7──▶ Node-RED ──HTTPS──▶ Servidor ──SQL──▶ Banco (histórico)
 (a máquina)         (ponte, lê e        (Render,        (Supabase)
                      escreve no CLP)     ASP.NET Core)
                           ▲                  │
                           └── comandos ──────┘  (na resposta da telemetria)
                                              │
                                              └──tempo real──▶ Painel no navegador
```

- **A conexão sempre sai da fábrica.** O Node-RED envia a telemetria a cada ~1 s; os comandos voltam na **resposta**. Nada é aberto na rede do CLP.
- **Tecnologias:** ASP.NET Core 10 (servidor), SignalR (tempo real), PostgreSQL (banco), HTML/CSS/JavaScript puros (painel), Node-RED (ponte), Docker (empacotamento), Render e Supabase (hospedagem).
- **Alternativas ao Node-RED:** OPC UA, Modbus, MQTT. O servidor só espera um JSON; trocar a ponte não muda o resto.

---

## 5. Onde estamos

| Item | Situação |
|---|---|
| Servidor e painel publicados (Render + Supabase, São Paulo) | **[no ar]**, com login |
| Telemetria real chegando do Node-RED do parceiro | **[feito]** (confirmado em 19/10) |
| Histórico no banco, com política de gravação inteligente | **[feito]** |
| Laboratório com simulação (sem CLP) | **[feito]** |
| Login, papéis e administração de usuários | **[feito]** |
| Comandos pela nuvem (fila) | **[feito]** no servidor e no fluxo; **falta o teste com o PLCSIM do parceiro** |
| Teste com um **CLP físico** | **não feito** |
| Mais de uma malha | **não feito** (hoje é só o TQ-101) |
| Sintonia assistida, relatórios exportáveis, auditoria de comandos | **não feito** |

**Qualidade:** cerca de **500 testes automáticos** (438 no servidor com banco real, 22 + 16 + 8 de interface em Chrome real, 22 do fluxo do Node-RED). O código do aplicativo tem cerca de 7,7 mil linhas, mais 6,6 mil de testes e ferramentas.

**Limites que precisam ficar claros:**
- Plano gratuito: o servidor **dorme** após 15 min sem uso e o banco **pausa** após ~7 dias sem atividade.
- Nada disto foi validado como **sistema de segurança**. Comandar uma planta real exige intertravamentos no CLP e botão de emergência independente do software.
- O simulador é um modelo didático simples, não um gêmeo digital calibrado.

---

## 6. Como chegamos aqui (o processo)

Em 3 semanas e 23 commits, em etapas curtas, cada uma com testes e um ponto de volta:

| Data | Etapa | O que foi feito |
|---|---|---|
| 08/10 | **Auditoria e migração** | o backend original (Express) foi preservado e testado; o novo (ASP.NET Core 10) foi construído para se comportar igual; histórico no PostgreSQL |
| 12/10 | **Integração com o Node-RED** | análise do fluxo real do parceiro, cópia preparada e roteiro de teste com o PLCSIM |
| 13/10 | **Painel V2, Laboratório e nuvem** | nova interface de cinco telas, comandos com confirmação por releitura, Laboratório, banco com segurança (RLS), publicação |
| 22/10 | **Simulação, login e fila** | Laboratório sem CLP, login com papéis, banco que não derruba o servidor, comandos pela nuvem |
| 29/10 | **Documentação** | este documento e o banco de conteúdos |

**Como a equipe trabalha (boas práticas que valem citar):**
- **Testes antes de confiar:** nenhuma entrega sem rodar a bateria; testes em banco real, não só simulado.
- **Honestidade nos estados:** o que foi testado, o que não foi, e o que só foi simulado ficam escritos.
- **Segredos fora do código:** senhas e chaves só em variáveis de ambiente, digitadas pela própria equipe.
- **Mudanças pequenas e reversíveis:** commits atômicos, marcos com tag (`marco-2-estavel`, `marco-4`).
- **Autorização antes de publicar:** nada vai ao ar sem aprovação.

Documentos técnicos de apoio (em `docs/`): `deploy`, `modelo-dados`, `contratos-api`, `autenticacao`, `laboratorio`, `comandos-estados`, `integracao-node-red`, `roteiro-teste-node-red`, `testes`, `frontend-v2`, `relatorio-ampliacao`.

---

## 7. O que podemos melhorar

**Prioridade alta (curto prazo, pouco esforço):**

1. **Testar a cadeia completa com o PLCSIM do parceiro**, incluindo um comando. É a prova que falta.
2. **Auditoria de comandos:** registrar quem pediu o quê e quando (a tabela já está reservada).
3. **Relatório do experimento** em CSV e PDF, para anexar a trabalhos.
4. **Testes automáticos no GitHub** a cada alteração (integração contínua).
5. **Manter o banco acordado:** um monitor de disponibilidade e um aviso antes de pausar.

**Prioridade média:**

6. **Sintonia assistida:** a partir do degrau do Laboratório, sugerir Kp/Ti/Td (Ziegler-Nichols, Cohen-Coon) e comparar a curva prevista com a real. É o que mais valoriza o lado acadêmico.
7. **Alarmes com notificação** (e-mail ou Telegram) e "quem reconheceu".
8. **Indicadores de desempenho** (IAE, ISE) e comparação de sessões lado a lado.
9. **Segurança extra:** recuperação de senha por e-mail, segundo fator, chave de ingestão por dispositivo.
10. **Modo professor:** ver as sessões dos alunos e dar retorno.

**Prioridade futura (esforço grande):**

11. **Várias malhas** (vazão, temperatura, pressão) com cadastro no banco.
12. **Agente próprio no lugar do Node-RED**, na rede do CLP, falando S7/OPC UA.
13. **Aplicativo no celular** (PWA) com alarmes.

**Dívidas técnicas conhecidas:** a tela usa uma única malha fixa; o banco gratuito não tem backup automático; os comandos expiram em 10 s sem prioridade ou fila de aprovação; não há política de LGPD escrita.

---

## 8. Até onde podemos ir

| Nível | O que é | O que falta além de hoje |
|---|---|---|
| **1. Didático (hoje)** | demonstração e sala de aula, com simulador ou PLCSIM | publicar a prova com o PLCSIM do parceiro |
| **2. Piloto em uma pequena indústria** | 1 a 3 malhas reais, usuários reais, só supervisão e comandos de baixo risco | CLP físico, várias malhas, auditoria, notificações, backup, hospedagem sem dormir, termo de uso |
| **3. Produto** | vários clientes, suporte, SLA | multi-empresa, monitoramento 24x7, segurança no padrão IEC 62443, cobrança, suporte, parceria com integrador |

O nível 2 é realista para o próximo semestre se a equipe conseguir **um local de teste**. O nível 3 é um negócio, não só um projeto: exige gente, dinheiro e certificações.

---

## 9. O vídeo (a pensar depois)

**Objetivo:** mostrar o dia a dia de uma indústria, de onde veio a necessidade, e como o SmartPLC resolve.

**Narrativa sugerida (6 atos, cerca de 3 a 4 minutos):**

1. **A rotina:** o operador anda até a máquina, olha o display, anota no papel.
2. **O problema:** um alarme de madrugada e ninguém sabe o que aconteceu.
3. **A virada:** o mesmo tanque, agora no painel, com o histórico.
4. **Como funciona:** o caminho dos dados (CLP, Node-RED, nuvem, painel) em animação simples.
5. **Demonstração:** setpoint, confirmação, Laboratório com simulação e a curva respondendo.
6. **Próximos passos:** o que vem (várias malhas, CLP real) e o convite.

**Como o fluxo do Node-RED entra:** é o melhor "diagrama vivo" do projeto. Duas opções: mostrar a tela do editor com os nós acendendo (autêntico, mas poluído), ou redesenhar o fluxo em animação limpa (o que recomendo, usando o editor real só num plano curto de prova).

**Como o Remotion entra:** o Remotion cria vídeo **com código** (React): cenas, textos, gráficos e transições são componentes, e o resultado é renderizado em MP4. Combina com este projeto porque podemos:
- animar o **diagrama de arquitetura** e as **curvas reais** do Laboratório com os mesmos dados;
- reaproveitar as **capturas de tela** já feitas (`docs/evidencias/v2/`);
- manter tudo em texto, no Git, e refazer o vídeo quando o painel mudar.
O que o Remotion não substitui: a **gravação de voz**, as **imagens de fábrica** (banco de imagens ou filmagem própria) e a edição fina de ritmo.

**Decisões da equipe antes de começar [a decidir]:**
1. Público principal: banca/professor, empresas ou investidores?
2. Duração e onde será exibido (apresentação, redes sociais)?
3. Voz própria, locução gerada ou só legendas?
4. Conseguimos **imagens de uma fábrica real** (ou só banco de imagens)?
5. Mostramos o **CLP físico/PLCSIM** do parceiro filmado, ou só tela gravada?

O material para começar a editar está em [`docs/video/banco-de-conteudos.md`](video/banco-de-conteudos.md).

---

## 10. Glossário rápido

- **CLP (PLC):** computador industrial que liga e desliga máquinas e lê sensores.
- **Malha de controle:** o ciclo "medir, decidir, agir" (sensor, controlador, válvula).
- **PV, SP, MV:** o valor medido (nível), o valor desejado e a variável manipulada (o sinal que o controlador envia à válvula, ou seja, a abertura).
- **PID:** o cálculo mais comum do controlador; Kp, Ti e Td ajustam o quão forte e rápido ele reage.
- **SCADA / supervisório:** sistema que mostra e comanda o processo a distância.
- **PLCSIM:** simulador de CLP da Siemens, para testar sem a máquina.
- **Node-RED:** ferramenta de blocos que liga o CLP a outros sistemas.
- **Telemetria:** os valores que o CLP envia continuamente.
- **Setpoint:** o valor que queremos atingir.
- **Histórico:** os valores guardados ao longo do tempo.
- **RLS:** trava do banco de dados que impede acesso por quem não é o servidor.
