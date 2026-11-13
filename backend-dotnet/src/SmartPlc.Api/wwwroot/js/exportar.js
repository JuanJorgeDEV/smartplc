// Exportação para planilha (.xlsx). Os montadores de abas são puros (sem navegador), para poderem ser testados.
import { baixarXlsx } from './xlsx.js';
import { carimboArquivo } from './format.js';

const quando = (t) => ({ data: new Date(t).getTime() });
const modoTexto = (m) => (m === null || m === undefined ? '' : m ? 'Manual' : 'Automático');

const informacoes = (pares) => ({
    nome: 'Informações',
    colunas: [{ titulo: 'Item', largura: 26 }, { titulo: 'Valor', largura: 90 }],
    linhas: pares
});

const COL_SESSAO = [
    { titulo: 'Data e hora (local)', formato: 'data', largura: 22 },
    { titulo: 'Nível PV (%)', formato: '0.0', largura: 14 },
    { titulo: 'Setpoint SP (%)', formato: '0.0', largura: 16 },
    { titulo: 'Saída da válvula MV (%)', formato: '0.0', largura: 24 },
    { titulo: 'Modo', largura: 14 }
];

/** Leituras recebidas por esta aba (memória do navegador). */
export function abasSessao(amostras, geradoEm = Date.now()) {
    return [
        { nome: 'Sessão', colunas: COL_SESSAO, filtro: true, linhas: amostras.map((s) => [quando(s.t), s.pv, s.sp, s.cv, modoTexto(s.modo)]) },
        informacoes([
            ['Malha', 'TQ-101 · controle de nível'],
            ['Origem', 'Leituras recebidas por esta aba (memória do navegador, até 60 min). Não é o histórico permanente do banco.'],
            ['Amostras', amostras.length],
            ['Gerado em', quando(geradoEm)]
        ])
    ];
}

/**
 * Período consultado no banco. `rotuloEvento` e `motivo` vêm da tela (nomes legíveis), para não repetir aqui.
 * temMais = existem registros além dos carregados (o arquivo só tem o que está na tela).
 */
export function abasPeriodo({ amostras, eventos, de, ate, temMais, rotuloEvento = (t) => t, motivo = (m) => m }, geradoEm = Date.now()) {
    return [
        {
            nome: 'Amostras', filtro: true,
            colunas: [
                { titulo: 'Data e hora (local)', formato: 'data', largura: 22 },
                { titulo: 'Adquirida em (local)', formato: 'data', largura: 22 },
                { titulo: 'Nível PV (%)', formato: '0.0', largura: 14 },
                { titulo: 'Setpoint SP (%)', formato: '0.0', largura: 16 },
                { titulo: 'Saída da válvula MV (%)', formato: '0.0', largura: 24 },
                { titulo: 'Modo', largura: 14 },
                { titulo: 'Motivo da gravação', largura: 20 }
            ],
            linhas: amostras.map((s) => [quando(s.t), s.adquiridoEm ? quando(s.adquiridoEm) : null, s.nivel, s.setpoint, s.valvula, modoTexto(s.modoManual), motivo(s.motivo)])
        },
        {
            nome: 'Eventos', filtro: true,
            colunas: [
                { titulo: 'Data e hora (local)', formato: 'data', largura: 22 },
                { titulo: 'Evento', largura: 40 },
                { titulo: 'Valor anterior', formato: '0.00', largura: 16 },
                { titulo: 'Valor novo', formato: '0.00', largura: 16 },
                { titulo: 'Detalhe', largura: 50 }
            ],
            linhas: eventos.map((e) => [quando(e.t), rotuloEvento(e.tipo), e.valorAnterior, e.valorNovo, e.detalhe])
        },
        informacoes([
            ['Malha', 'TQ-101 · controle de nível'],
            ['Período consultado', `${new Date(de).toLocaleString('pt-BR')} até ${new Date(ate).toLocaleString('pt-BR')}`],
            ['Amostras no arquivo', amostras.length],
            ['Eventos no arquivo', eventos.length],
            ['Está completo?', temMais ? 'Não. Há mais registros no período; o arquivo traz só o que estava carregado na tela.' : 'Sim, todo o período consultado.'],
            ['Origem', 'Banco de dados (PostgreSQL). As amostras são gravadas por seleção (periódica, por variação e por evento), não a cada leitura.'],
            ['Gerado em', quando(geradoEm)]
        ])
    ];
}

export const exportarSessao = (amostras) => baixarXlsx(`tq101_sessao_${carimboArquivo()}.xlsx`, abasSessao(amostras));
export const exportarPeriodo = (dados) => baixarXlsx(`tq101_historico_${carimboArquivo()}.xlsx`, abasPeriodo(dados));
