# Banco de conteúdos para o vídeo e a apresentação

Blocos prontos para **copiar, cortar e reescrever**. Nada aqui é definitivo: é matéria-prima. Os textos de dor da indústria são **[hipótese]** até a equipe conversar com quem vive isso; os números do projeto são reais e conferidos em 29/10/2026.

---

## 1. Frases-base (editáveis)

**Em uma frase:** "O SmartPLC mostra, em qualquer lugar, o que a sua máquina está fazendo e deixa você testar o controle sem risco."

**Pitch de 30 segundos:**
> Na indústria, descobrir o que uma máquina fez de madrugada ainda depende de papel, memória e de alguém ir até o painel. O SmartPLC liga o CLP à nuvem de forma segura, mostra o processo em tempo real, guarda o histórico e deixa pessoas autorizadas comandar, sempre confirmando pelo próprio CLP. E, para quem está aprendendo controle, tem um laboratório que simula o processo e mede a resposta.

**Ganchos de abertura (escolher um):**
1. "Três da manhã. O nível do tanque passou do limite. Quem viu?"
2. "Para saber como a máquina está, você ainda precisa ir até ela?"
3. "Sintonizar um controlador numa fábrica de verdade? Boa sorte."

**Frase de fechamento:** "Do chão de fábrica à sala de aula, o mesmo painel."

---

## 2. Rascunho de narração por ato

| Ato | Texto base (cerca de 20 s cada) |
|---|---|
| 1. A rotina | "Todo dia, o operador percorre a planta. Olha o display, confere a válvula, anota o nível no papel." |
| 2. O problema | "Quando algo sai do normal, o que sobra é a memória de quem estava lá. Sem histórico, não há causa. Sem rastro, não há responsável." |
| 3. A virada | "O SmartPLC coloca o mesmo tanque na tela: nível, setpoint e válvula em tempo real, com alarmes e histórico." |
| 4. Como funciona | "A fábrica envia os dados para a nuvem, a conexão sai de dentro para fora e nada fica exposto. O painel recebe tudo ao vivo." |
| 5. Demonstração | "Pedir um comando é simples, mas o painel só diz 'aplicado' quando o CLP confirma. E no laboratório, qualquer pessoa testa a resposta do controle, sem risco." |
| 6. Próximos passos | "Várias malhas, CLP real, relatórios e notificações. Um piloto numa pequena indústria é o próximo passo." |

---

## 3. Números do projeto (reais)

| Dado | Valor |
|---|---|
| Telas | 5 (Visão Geral, Controle, Histórico, Laboratório, Integrações) |
| Variáveis acompanhadas | 8 por malha |
| Atualização em tempo real | cerca de 1 segundo |
| Experimentos no Laboratório | 2 tipos (resposta ao setpoint e automático × manual), com simulação ou CLP |
| Velocidades da simulação | 1×, 2×, 5× e 10× |
| Papéis de acesso | 3 (administrador, operador, visualizador) |
| Testes automáticos | cerca de 500 |
| Tempo de desenvolvimento | 3 semanas (08/10 a 29/10/2026), 23 commits |
| Hospedagem | Render (servidor) e Supabase (banco, São Paulo) |

---

## 4. Cenas candidatas e imagens que já existem

Todas as imagens estão em `docs/evidencias/v2/`.

| Cena | Imagem pronta | Observação |
|---|---|---|
| Painel principal, tema escuro | `desktop-1440-visao-geral-escuro` | tanque, PV/SP/CV, alarmes |
| Painel principal, tema claro | `desktop-1440-visao-geral-claro` | para contraste com o "papel" |
| Controle com confirmação | `desktop-1440-controle` | mostrar as etapas do comando |
| Histórico | `desktop-1440-historico` | "quando foi?" |
| Laboratório, preparar | `desktop-1440-laboratorio-simulacao-preparar` | escolha da fonte |
| Laboratório, ao vivo | `desktop-1440-laboratorio-simulacao-ativa` | **melhor cena**: degrau de 30 para 65 % |
| Laboratório, resumo | `desktop-1440-laboratorio-simulacao-revisao` | métricas da resposta |
| Integrações | `desktop-1440-integracoes` | caminho dos dados e usuários |
| Login | `desktop-1440-login` | segurança |
| Celular | `mobile-390-visao-geral`, `mobile-390-controle` | "em qualquer lugar" |
| Sem comunicação | `desktop-1440-sem-comunicacao` | honestidade: o painel não inventa dado |

---

## 5. O que ainda precisa ser produzido (lista de trabalho)

**Gravações de tela (podemos fazer agora, em 1080p, 60 fps):**
- [ ] Nível subindo no Laboratório, do degrau até estabilizar (20 s, em velocidade 5×).
- [ ] Um comando no Controle: solicitado, recebido, confirmado.
- [ ] Alarme de nível alto aparecendo e sendo reconhecido.
- [ ] Login, painel, troca de papel (administrador cria usuário).
- [ ] O fluxo do Node-RED com os nós acendendo (plano curto de prova).

**Imagens fora do software (precisam de decisão da equipe):**
- [ ] Imagens de uma planta industrial (banco de imagens livre de direitos, ou filmagem própria).
- [ ] O CLP/PLCSIM do parceiro em funcionamento (tela do TIA Portal).
- [ ] Um operador anotando em papel (encenado ou banco de imagens).

**Entrevistas curtas (para transformar hipótese em fato):**
- [ ] 3 a 5 conversas com técnico, operador ou supervisor: "o que te atrapalha hoje?" e "o que faltou na última parada?".

**Gráficos para animar com dados reais:**
- Curva da resposta ao degrau (PV, SP e CV) em câmera lenta, com a marca de 63 % e o tempo de acomodação.
- Comparação automático × manual (a PV deixa de ser regulada).
- Caminho dos dados: CLP, Node-RED, nuvem, banco, painel, com o pacote "viajando".

---

## 6. Identidade visual (para manter a coerência)

| Elemento | Valor |
|---|---|
| Títulos | Space Grotesk, peso 500 |
| Texto e números | IBM Plex Sans e IBM Plex Mono |
| Fundo escuro de "campo" | `#0A0D0C` (verde quase preto) |
| Cor de dado ao vivo | **verde-sinal `#C7FF3A`**, usada **só** para dado vivo (PV, "ao vivo") |
| Alarme | vermelho, sempre com texto |
| Cantos | retos, sem arredondar |
| Marca | tanque estilizado com linha de setpoint tracejada (`img/marca.svg`) |

---

## 7. Proposta de estrutura para o projeto em Remotion

> Sugestão, a decidir. O Remotion não está instalado e nada foi instalado globalmente.

```
video/
  src/
    Composicao.tsx        a linha do tempo (os 6 atos)
    cenas/                uma cena por arquivo (Rotina, Problema, Virada, Arquitetura, Demo, Futuro)
    componentes/          Tanque animado, Curva, Diagrama, Legenda, Titulo
    dados/                curvas exportadas do Laboratório (JSON)
  public/
    imagens/              capturas de tela e banco de imagens
    audio/                locução e trilha
  package.json
```

**Fluxo de trabalho:**
1. Exportar do Laboratório os dados de uma sessão (**precisa do relatório em JSON/CSV**, melhoria nº 3 do documento principal).
2. Animar a curva e o diagrama como componentes, com os dados reais.
3. Intercalar com gravações de tela e imagens externas.
4. Gravar a locução, ajustar o tempo das cenas e renderizar em MP4 1080p.
5. Gerar uma versão curta (30 s) para redes sociais a partir das mesmas cenas.

---

## 8. Perguntas para a equipe decidir

1. Quem é o público principal do vídeo: banca, empresas ou investidores?
2. Quantos minutos, e em que formato (horizontal para apresentação, vertical para redes)?
3. Locução com a voz de alguém da equipe, voz gerada, ou só legendas e música?
4. Teremos imagens reais de uma fábrica? Alguém conhece um lugar para filmar?
5. Mostramos o PLCSIM/CLP do parceiro em tela ou ao vivo?
6. Qual é a **mensagem única** que a pessoa precisa lembrar ao final?
