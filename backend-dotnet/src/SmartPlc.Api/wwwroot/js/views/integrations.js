// Integrações: o que está CONFIGURADO (vem do servidor) separado do que está VERIFICADO em tempo real.
import { state, on, comunicacao, idadeAtualSeg } from '../store.js';
import { casasDe } from '../prefs.js';
import { sessao, ehAdmin } from '../sessao.js';
import * as usuarios from './users.js';
import { abrirTrocaDeSenha, sairAgora } from '../conta.js';
import { num, hora, idade, esc } from '../format.js';

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
  ${sessao.autenticacaoAtiva && sessao.usuario ? `<section aria-labelledby="i-conta">
    <div class="sec-head"><h2 id="i-conta" class="eyebrow">Minha conta</h2></div>
    <p class="small"><strong>${esc(sessao.usuario.nome)}</strong> · ${esc(sessao.usuario.papelRotulo)} · <span class="mono">${esc(sessao.usuario.email)}</span></p>
    <div class="lab-acoes"><button type="button" class="btn" id="b-conta-senha">Trocar senha</button><button type="button" class="btn" id="b-conta-sair">Sair</button></div>
  </section>` : ''}
  <section aria-labelledby="i-res">
    <div class="sec-head"><h2 id="i-res" class="eyebrow">Como funciona</h2></div>
    <p class="small">O Node-RED lê o CLP e envia os valores a este servidor. O servidor transmite ao painel em tempo real e grava o histórico no banco.</p>
    <ol class="cadeia" aria-label="Caminho dos dados">
      <li><strong>CLP (PLCSIM)</strong><span>executa o controle</span></li>
      <li><strong>Node-RED</strong><span>lê via S7 e envia por HTTP</span></li>
      <li><strong>Servidor</strong><span>valida, transmite e grava</span></li>
      <li><strong>PostgreSQL</strong><span>histórico</span></li>
      <li><strong>Painel</strong><span>supervisão e laboratório</span></li>
    </ol>
  </section>

  <section aria-labelledby="i-con">
    <div class="sec-head"><h2 id="i-con" class="eyebrow">Estado da conexão</h2></div>
    <div class="table-wrap"><table>
      <thead><tr><th scope="col">Item</th><th scope="col">Valor</th></tr></thead>
      <tbody id="tb-con"></tbody></table></div>
  </section>

  <section aria-labelledby="i-var">
    <div class="sec-head"><h2 id="i-var" class="eyebrow">Variáveis · TQ-101</h2><span class="badge" id="n-var"></span></div>
    <div class="table-wrap"><table class="tb-var">
      <caption class="sr-only">Variáveis do SmartPLC, com valor atual</caption>
      <thead><tr><th scope="col">Variável</th><th scope="col">Endereço S7</th><th scope="col">Tipo</th><th scope="col" class="num">Valor atual</th><th scope="col">Un.</th><th scope="col">Leitura</th></tr></thead>
      <tbody id="tb-var"></tbody></table></div>
  </section>
  ${sessao.autenticacaoAtiva && ehAdmin() ? usuarios.html() : ''}
</div>`;
    if (sessao.autenticacaoAtiva && ehAdmin()) usuarios.montar(raiz);
    raiz.querySelector('#b-conta-senha')?.addEventListener('click', abrirTrocaDeSenha);
    raiz.querySelector('#b-conta-sair')?.addEventListener('click', sairAgora);

    const $ = (id) => raiz.querySelector(`#${id}`);

    function linhasConexao() {
        const cfg = state.config;
        const com = comunicacao();
        const ult = idadeAtualSeg();
        const [tomC, txtC] = COMUNICACAO[com];
        const hubTxt = { conectado: ['live', 'Conectado'], conectando: ['warn', 'Conectando…'], reconectando: ['warn', 'Reconectando…'], desconectado: ['alarm', 'Desconectado'] }[state.hub];
        const L = (item, valor) => `<tr><th scope="row" class="item">${item}</th><td>${valor}</td></tr>`;
        $('tb-con').innerHTML = [
            L('Telemetria do CLP', `<span class="tag ${tomC}">${txtC}</span>${ult === null ? '' : ` <span class="num muted">${hora(Date.now() - ult * 1000)} (${idade(ult)})</span>`}`),
            L('Painel ↔ servidor', `<span class="tag ${hubTxt[0]}">${hubTxt[1]}</span>`),
            L('Comandos ao CLP', cfg ? (cfg.controle.habilitado ? (cfg.controle.entrega === 'fila' ? 'Habilitados · entregues pela resposta da telemetria' : 'Habilitados') : 'Desabilitados · somente leitura') : '—'),
            L('Histórico no banco', cfg ? (cfg.historico.habilitado ? `Ligado · consulta de até ${cfg.historico.periodoMaximoHoras} h por vez` : 'Desligado (sem banco configurado)') : '—'),
            L('Laboratório', cfg ? (cfg.laboratorio.habilitado ? (cfg.laboratorio.escrita ? 'Disponível' : 'Somente consulta') : 'Indisponível (exige o banco)') : '—'),
            L('Alarmes de nível', cfg ? `baixo &lt; ${num(cfg.processo.nivelBaixo, 0)} % · alto &gt; ${num(cfg.processo.nivelAlto, 0)} %` : '—')
        ].join('');
    }

    const valorDe = (campo) => {
        const t = state.status.telemetria, c = state.status.controle;
        return ({ nivel: t.nivel, valvula: t.valvula, setpoint: c.setpoint, modoManual: c.modoManual, kp: c.kp, ti: c.ti, td: c.td, valvulaManual: c.valvulaManual })[campo];
    };
    const leituraTag = () => {
        const com = comunicacao();
        const tem = state.status.telemetria.timestamp !== null;
        return com === 'ok' ? '<span class="tag live">Lendo</span>' : tem ? '<span class="tag alarm">Antigo</span>' : '<span class="tag off">Sem leitura</span>';
    };

    function linhasVariaveis() {
        $('n-var').textContent = `${state.variaveis.length} variáveis`;
        if (!state.variaveis.length) { $('tb-var').innerHTML = '<tr><td colspan="6" class="muted">O catálogo não pôde ser carregado do servidor.</td></tr>'; return; }
        $('tb-var').innerHTML = state.variaveis.map((v) => `<tr>
              <th scope="row" class="item">${esc(v.nome)}<br><span class="mono muted">${esc(v.identificador)}</span></th>
              <td class="mono">${v.endereco ? esc(v.endereco) : '<span class="muted">oculto</span>'}</td>
              <td class="mono">${esc(v.tipo)}</td>
              <td class="num" data-valor="${v.campo}"></td>
              <td>${esc(v.unidade || '—')}</td>
              <td data-leitura>${leituraTag()}</td>
            </tr>`).join('');
        atualizarValores();
    }

    function atualizarValores() {
        const com = comunicacao();
        const tem = state.status.telemetria.timestamp !== null;
        state.variaveis.forEach((v) => {
            const td = raiz.querySelector(`[data-valor="${v.campo}"]`);
            if (!td) return;
            const bruto = valorDe(v.campo);
            td.textContent = !tem ? '—' : v.tipo === 'BOOL' ? (bruto ? 'manual' : 'automático') : num(bruto, casasDe(v.campo));
            td.classList.toggle('muted', com !== 'ok');
            const l = td.parentElement.querySelector('[data-leitura]');
            if (l) l.innerHTML = leituraTag();
        });
    }

    const offs = [
        on('status', () => { linhasConexao(); atualizarValores(); }),
        on('tick', () => { linhasConexao(); atualizarValores(); }),
        on('conexao', linhasConexao),
        on('config', () => { linhasConexao(); linhasVariaveis(); })
    ];
    linhasConexao(); linhasVariaveis();
    return () => offs.forEach((f) => f());
}
