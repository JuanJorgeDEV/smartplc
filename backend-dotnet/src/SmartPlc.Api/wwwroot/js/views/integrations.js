// Integrações: o que está CONFIGURADO (vem do servidor/documentação) separado do que está VERIFICADO em tempo real.
import { state, on, comunicacao, idadeAtualSeg } from '../store.js';
import { prefs, salvar, restaurar, aoMudar } from '../prefs.js';
import { num, hora, idade, esc } from '../format.js';
import { termo } from '../glossary.js';
import { aviso } from '../ui.js';

export const titulo = 'Integrações';

const COMUNICACAO = {
    ok: ['live', 'Recebendo telemetria'],
    aguardando: ['warn', 'Aguardando a primeira leitura'],
    sem_clp: ['alarm', 'Telemetria parou'],
    servidor_off: ['alarm', 'Sem conexão com o servidor']
};

export async function montar(raiz) {
    raiz.innerHTML = `
<div class="integ">
  <section aria-labelledby="i-con">
    <div class="sec-head"><h2 id="i-con" class="eyebrow">Conexão industrial</h2><span class="small">Cada linha diz se o dado é <span class="badge">configurado</span> ou <span class="badge verified">verificado</span> agora.</span></div>
    <div class="table-wrap"><table>
      <thead><tr><th scope="col">Item</th><th scope="col">Valor</th><th scope="col">Natureza</th></tr></thead>
      <tbody id="tb-con"></tbody></table></div>
    <p class="note">O backend só observa a <em>chegada</em> de telemetria vinda do Node-RED. Se a conexão S7 entre o Node-RED e o CLP está saudável é uma informação do Node-RED: aqui ela aparece apenas de forma indireta (as leituras chegam ou não chegam).</p>
    <ol class="cadeia" aria-label="Caminho dos dados">
      <li><strong>${termo('clp', 'CLP')} (PLCSIM)</strong><span>executa o controle</span></li>
      <li><strong>Node-RED</strong><span>lê via S7 e envia por HTTP</span></li>
      <li><strong>ASP.NET Core</strong><span>valida, transmite (SignalR) e grava</span></li>
      <li><strong>PostgreSQL</strong><span>${termo('historico', 'histórico')}</span></li>
      <li><strong>Este painel</strong><span>supervisão e laboratório</span></li>
    </ol>
  </section>

  <section aria-labelledby="i-var">
    <div class="sec-head"><h2 id="i-var" class="eyebrow">Catálogo de variáveis · TQ-101</h2><span class="badge" id="n-var"></span></div>
    <div class="table-wrap"><table class="tb-var">
      <caption class="sr-only">Variáveis do SmartPLC, com valor atual e opções de apresentação</caption>
      <thead><tr><th scope="col">Nome exibido</th><th scope="col">Identificador</th><th scope="col">Endereço S7</th><th scope="col">Tipo</th><th scope="col" class="num">Valor atual</th><th scope="col">Un.</th><th scope="col">Leitura</th><th scope="col">Casas</th><th scope="col">Destaque</th></tr></thead>
      <tbody id="tb-var"></tbody></table></div>
    <p class="note" id="n-enderecos"></p>
  </section>

  <section aria-labelledby="i-apr">
    <div class="sec-head"><h2 id="i-apr" class="eyebrow">Apresentação</h2><button type="button" class="btn small" id="b-restaurar">Restaurar apresentação original</button></div>
    <div class="ind-opcoes">
      <label class="check"><input type="checkbox" data-ind="tendencia"> Mostrar a tendência na Visão Geral</label>
      <label class="check"><input type="checkbox" data-ind="alarmes"> Mostrar os alarmes na Visão Geral</label>
      <label class="check"><input type="checkbox" data-ind="resumo"> Mostrar o resumo de operação na Visão Geral</label>
    </div>
    <p class="note"><strong>Onde ficam estas escolhas:</strong> no armazenamento local deste navegador. Não são enviadas ao servidor, não valem para outras pessoas ou aparelhos, não alteram endereços do CLP e <em>não mudam a política de gravação do histórico</em> (o banco continua guardando as oito variáveis conforme a política do servidor).</p>
  </section>
</div>`;

    const $ = (id) => raiz.querySelector(`#${id}`);

    function linhasConexao() {
        const cfg = state.config;
        const com = comunicacao();
        const ult = idadeAtualSeg();
        const [tomC, txtC] = COMUNICACAO[com];
        const hubTxt = { conectado: ['live', 'Conectado (SignalR)'], conectando: ['warn', 'Conectando…'], reconectando: ['warn', 'Reconectando…'], desconectado: ['alarm', 'Desconectado'] }[state.hub];
        const L = (item, valor, natureza) => `<tr><th scope="row" class="item">${item}</th><td>${valor}</td><td><span class="badge ${natureza === 'verificado' ? 'verified' : ''}">${natureza}</span></td></tr>`;
        $('tb-con').innerHTML = [
            L('Tipo de integração', 'Siemens S7 (ISO-on-TCP, via PLCSIM Advanced)', 'configurado'),
            L('Camada de integração', 'Node-RED (nós <span class="mono">s7 in</span> / <span class="mono">s7 out</span>)', 'configurado'),
            L('Variáveis configuradas', cfg ? `${cfg.integracao.variaveisConfiguradas}` : '—', 'configurado'),
            L('Último recebimento de telemetria', ult === null ? '—' : `<span class="num">${hora(Date.now() - ult * 1000)}</span> <span class="muted">(${idade(ult)})</span>`, 'verificado'),
            L('Comunicação observada pelo backend', `<span class="tag ${tomC}">${txtC}</span>`, 'verificado'),
            L('Conexão deste painel com o servidor', `<span class="tag ${hubTxt[0]}">${hubTxt[1]}</span>`, 'verificado'),
            L('Comandos', cfg ? (cfg.controle.habilitado ? (cfg.controle.exigeChaveDeOperador ? 'Habilitados, exigem chave de operador' : 'Habilitados (ambiente local, sem chave)') : 'Desabilitados · somente leitura') : '—', 'configurado'),
            L('Histórico no banco', cfg ? (cfg.historico.habilitado ? `Ligado · consulta de até ${cfg.historico.periodoMaximoHoras} h por vez` : 'Desligado (sem banco configurado)') : '—', 'configurado'),
            L('Laboratório', cfg ? (cfg.laboratorio.habilitado ? (cfg.laboratorio.escrita ? 'Disponível' : 'Disponível somente para consulta') : 'Indisponível (exige o banco)') : '—', 'configurado'),
            L('Limiares de alarme de nível', cfg ? `baixo &lt; ${num(cfg.processo.nivelBaixo, 0)} % · alto &gt; ${num(cfg.processo.nivelAlto, 0)} %` : '—', 'configurado')
        ].join('');
    }

    const valorDe = (campo) => {
        const t = state.status.telemetria, c = state.status.controle;
        return ({ nivel: t.nivel, valvula: t.valvula, setpoint: c.setpoint, modoManual: c.modoManual, kp: c.kp, ti: c.ti, td: c.td, valvulaManual: c.valvulaManual })[campo];
    };

    function linhasVariaveis() {
        const p = prefs();
        const com = comunicacao();
        const tem = state.status.telemetria.timestamp !== null;
        $('n-var').textContent = `${state.variaveis.length} variáveis`;
        if (!state.variaveis.length) { $('tb-var').innerHTML = '<tr><td colspan="9" class="muted">O catálogo não pôde ser carregado do servidor.</td></tr>'; return; }
        // Não recria linhas enquanto alguém digita num campo da tabela.
        const editando = raiz.querySelector('.tb-var :focus');
        if (editando && editando.tagName !== 'BUTTON') { atualizarValores(); return; }

        $('tb-var').innerHTML = state.variaveis.map((v) => {
            const pr = p.variaveis[v.campo];
            const leitura = com === 'ok' ? '<span class="tag live">Lendo</span>' : tem ? '<span class="tag alarm">Antigo</span>' : '<span class="tag off">Sem leitura</span>';
            return `<tr data-campo="${v.campo}">
              <td><label class="sr-only" for="nm-${v.campo}">Nome exibido de ${esc(v.nome)}</label><input class="input mini" id="nm-${v.campo}" data-nome="${v.campo}" maxlength="40" value="${esc(pr.nome)}" placeholder="${esc(v.nome)}"></td>
              <td class="mono">${esc(v.identificador)}</td>
              <td class="mono">${v.endereco ? esc(v.endereco) : '<span class="muted">oculto</span>'}</td>
              <td class="mono">${esc(v.tipo)}</td>
              <td class="num" data-valor="${v.campo}"></td>
              <td>${esc(v.unidade || '—')}</td>
              <td data-leitura>${leitura}</td>
              <td>${v.tipo === 'BOOL' ? '<span class="muted">—</span>' : `<label class="sr-only" for="cs-${v.campo}">Casas decimais de ${esc(v.nome)}</label><select class="input mini" id="cs-${v.campo}" data-casas="${v.campo}">${[0, 1, 2, 3].map((n) => `<option value="${n}" ${pr.casas === n ? 'selected' : ''}>${n}</option>`).join('')}</select>`}</td>
              <td><label class="check"><input type="checkbox" data-destaque="${v.campo}" ${pr.destaque ? 'checked' : ''} aria-label="Destacar ${esc(v.nome)} na Visão Geral"></label></td>
            </tr>`;
        }).join('');
        $('n-enderecos').textContent = state.enderecosVisiveis === false
            ? 'Os endereços S7 estão ocultos neste ambiente por configuração do servidor.'
            : 'Endereços conforme o fluxo do Node-RED da equipe (nó s7 endpoint "PLCSIM_Advanced"). O SmartPLC não altera endereços nem escreve em posições arbitrárias de memória do CLP.';
        atualizarValores();
    }

    function atualizarValores() {
        const p = prefs();
        const com = comunicacao();
        const tem = state.status.telemetria.timestamp !== null;
        state.variaveis.forEach((v) => {
            const td = raiz.querySelector(`[data-valor="${v.campo}"]`);
            if (!td) return;
            const bruto = valorDe(v.campo);
            td.textContent = !tem ? '—' : v.tipo === 'BOOL' ? (bruto ? 'verdadeiro (manual)' : 'falso (automático)') : num(bruto, p.variaveis[v.campo].casas);
            td.classList.toggle('muted', com !== 'ok');
            const l = td.parentElement.querySelector('[data-leitura]');
            if (l) l.innerHTML = com === 'ok' ? '<span class="tag live">Lendo</span>' : tem ? '<span class="tag alarm">Antigo</span>' : '<span class="tag off">Sem leitura</span>';
        });
    }

    function pintarIndicadores() {
        const ind = prefs().indicadores;
        raiz.querySelectorAll('[data-ind]').forEach((c) => { c.checked = !!ind[c.dataset.ind]; });
    }

    // ---------- Edição (valida e salva a cada mudança) ----------
    raiz.addEventListener('change', (e) => {
        const alvo = e.target;
        const p = JSON.parse(JSON.stringify(prefs()));
        if (alvo.dataset.nome) p.variaveis[alvo.dataset.nome].nome = alvo.value.trim().slice(0, 40);
        else if (alvo.dataset.casas) p.variaveis[alvo.dataset.casas].casas = Number(alvo.value);
        else if (alvo.dataset.destaque) p.variaveis[alvo.dataset.destaque].destaque = alvo.checked;
        else if (alvo.dataset.ind) p.indicadores[alvo.dataset.ind] = alvo.checked;
        else return;
        salvar(p);
    });
    $('b-restaurar').addEventListener('click', () => { restaurar(); linhasVariaveis(); pintarIndicadores(); aviso('ok', 'Apresentação restaurada ao padrão.'); });

    const offs = [on('status', () => { linhasConexao(); atualizarValores(); }), on('tick', () => { linhasConexao(); atualizarValores(); }), on('conexao', linhasConexao), on('config', () => { linhasConexao(); linhasVariaveis(); }), aoMudar(() => atualizarValores())];
    linhasConexao(); linhasVariaveis(); pintarIndicadores();
    return () => offs.forEach((f) => f());
}
