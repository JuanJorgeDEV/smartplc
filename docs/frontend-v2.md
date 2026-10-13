# Frontend V2

HTML + CSS + JavaScript nativo (módulos ES) servidos por `backend-dotnet/src/SmartPlc.Api/wwwroot/`. Chart.js 4.4.3 e SignalR 8.0.7 por CDN (como antes). Sem framework e sem etapa de build. O dashboard original (Express) continua em `public/`, intocado.

## Estrutura

```
wwwroot/
  index.html            casca: navegação, faixa de estado, alarme, diálogo, avisos
  css/  tokens.css      tokens da landing, copiados sem alteração (claro e escuro)
        fonts.css       Space Grotesk 500 · IBM Plex Sans 400 · IBM Plex Mono 400/500 (arquivos locais, SIL OFL)
        base.css        reset, tipografia, controles, tabelas, diálogos
        app.css         layout e telas
  js/   main.js         tema, rotas por hash (#/visao-geral …), faixa de estado, alarmes
        store.js        estado central e regras: comunicação, alarmes, buffer de amostras, comandos
        live.js         SignalR com reconexão infinita (e consulta periódica se a biblioteca não carregar)
        api.js          fetch com tempo limite; nunca lança
        prefs.js        preferências de apresentação (localStorage)
        charts.js       Chart.js (uma instância por tela, marcadores verticais, decimação min-max)
        glossary.js, ui.js, format.js
        views/ overview · control · history · lab · integrations     (cada uma exporta montar() e devolve a limpeza)
```

Cada tela é montada ao entrar e **desmontada ao sair** (ouvintes removidos, gráfico destruído). O estado vive no `store`, então voltar a uma aba não perde nada.

## Princípios de comportamento

- **Nada fictício.** Sem leitura, valores viram "—" e o tanque fica vazio; com leitura velha, os números ficam apagados com "valor antigo" e o horário. Os estados da comunicação são quatro e aparecem sempre na faixa superior: *Recebendo do CLP*, *Aguardando telemetria*, *Sem comunicação com o CLP*, *Servidor desconectado*.
- **O CLP é a fonte da verdade.** "Valor no CLP" vem da telemetria. Um comando mostra etapas (solicitado → recebido → confirmado/falhou/expirado) e o novo valor só aparece depois da releitura.
- **Animação só com dado novo.** O nível e o marcador de SP têm transição de 700 ms ao receber um valor; nada anima continuamente. `prefers-reduced-motion` desliga as transições. O fluxo na tubulação é apenas a opacidade proporcional à CV.
- **Sem credenciais no navegador.** O painel não envia nem guarda `X-Operator-Key`/`X-Api-Key`. Se o servidor exigir chave de operador, a tela de Controle fica em modo de consulta e explica por quê. Há um teste que garante que os arquivos estáticos não contêm essas chaves.

## Identidade visual

Segue o design system **que já existia na landing** (`SmartPLC-Portifolio/css/tokens.css`): ângulo reto, réguas hairline no lugar de cartões, o tanque sobre a zona escura `field`, `signal` (#C7FF3A) **só** para dado vivo (PV, menisco, "ao vivo"), SP sempre em `ink` tracejado, `alarm` sempre com rótulo de texto, números em mono tabular. Tema escuro por padrão, com alternância e memória (`?tema=` força o tema, usado nas capturas).

Decisões e divergências do briefing (para você aprovar):
- **Fontes:** o briefing sugeriu Barlow; a landing usa **Space Grotesk**. Mantive Space Grotesk para preservar a identidade já aprovada e evitar uma quarta família. Trocar é uma linha em `fonts.css`/`tokens.css`.
- **Cor "atenção" (âmbar):** não existia nos tokens; acrescentei `--warn` em `base.css` (#8A5A00 no claro, #E3B341 no escuro).
- **Marca:** o logotipo da landing tem cor fixa; usei o mesmo desenho com `currentColor` para valer nos dois temas.
- **"Malha de vazão":** não encontrei essa expressão em nenhum arquivo do repositório (código ou documentação técnica). **Não abri os `.docx` do projeto** (`SmartPLC.docx`, `SmartPLC_corrigido.docx`); se o documento acadêmico ainda diz "vazão", a inconsistência está nele e eu não o alterei.

## Escala tipográfica

O aplicativo usa um degrau acima da landing, para leitura prolongada em tela de trabalho (a landing não foi alterada): corpo 18/28 px, texto pequeno 16/24, rótulos mono 14/18 (caixa alta), microtexto 13/17, leituras grandes inalteradas. Os rótulos do sinótico e dos gráficos acompanham (gráficos 13 px; no celular, os textos do desenho são ampliados para compensar o encolhimento do SVG). As variáveis estão em `css/base.css` (`--text-body`, `--text-small`, `--text-label`, `--text-micro`); ajustar a leitura é mexer nelas.

## Preferências (Integrações)

Nome exibido, casas decimais, quais variáveis ficam em destaque e quais seções aparecem na Visão Geral. **Ficam no `localStorage` do navegador** (chave `smartplc.prefs.v1`): não vão ao servidor, não valem para outras pessoas/aparelhos, não alteram endereços do CLP e **não mudam a política de gravação do histórico**. Foi a opção mais simples e segura para a primeira versão; se um dia precisarem ser compartilhadas, vira um endpoint autenticado.

## Desempenho e acessibilidade

- Uma instância de gráfico por tela; dados em `{x,y}` sem parsing; decimação *min-max* (preserva picos); 60 min de memória (≈ 3600 pontos); atualização a 1 Hz e a cada telemetria.
- Histórico: carrega no máximo `limiteMaximo` (5000) amostras por vez, com "Carregar mais".
- Foco visível (anel de 2 px), link "Ir para o conteúdo", `h1` único por tela, campos com rótulo, diálogo de confirmação com foco inicial em "Cancelar", estados de alarme com texto (nunca só cor), região `aria-live` para avisos.
- Responsivo: navegação lateral ≥ 900 px; abaixo, barra inferior com 5 abas. Verificado sem rolagem horizontal em 360, 390, 768 e 1440 px.

## Executar e capturar

```bash
cd backend-dotnet && dotnet run --project src/SmartPlc.Api            # http://localhost:5000
node tools/capturar-telas.js http://localhost:5000 [filtro] [segundos]   # imagens em docs/evidencias/v2/
```

As imagens em `docs/evidencias/v2/` vieram do **simulador do Node-RED**; os arquivos `semdados-*` mostram um servidor sem telemetria, sem banco e com comandos desabilitados.
