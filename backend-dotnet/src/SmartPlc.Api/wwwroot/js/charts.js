// Gráficos de série temporal (Chart.js, já usado no projeto). Eixo X linear em milissegundos (sem adaptador de datas).
import { hora, dataHora, num } from './format.js';

/** Cores lidas dos tokens da landing para o tema atual (claro/escuro). */
export function cores() {
    const cs = getComputedStyle(document.documentElement);
    const v = (n) => cs.getPropertyValue(n).trim();
    return { pv: v('--signal-ink'), sp: v('--setpoint'), cv: v('--ink-muted'), grade: v('--hairline'), texto: v('--ink-muted'), alarme: v('--alarm'), aviso: v('--warn'), linha: v('--line') };
}

// Marcadores verticais (ex.: instante em que o CLP reportou a mudança de setpoint). Registrado uma vez.
const marcadores = {
    id: 'marcadores',
    afterDatasetsDraw(chart, _args, opcoes) {
        const itens = opcoes?.itens ?? [];
        if (!itens.length) return;
        const { ctx, chartArea, scales } = chart;
        const c = cores();
        ctx.save();
        ctx.font = '12px "IBM Plex Mono", monospace';
        for (const it of itens) {
            const x = scales.x.getPixelForValue(it.x);
            if (x < chartArea.left || x > chartArea.right) continue;
            ctx.strokeStyle = it.cor === 'alarme' ? c.alarme : it.cor === 'aviso' ? c.aviso : c.texto;
            ctx.lineWidth = 1;
            ctx.setLineDash(it.tracejado === false ? [] : [3, 3]);
            ctx.beginPath(); ctx.moveTo(x, chartArea.top); ctx.lineTo(x, chartArea.bottom); ctx.stroke();
            if (it.rotulo) {
                ctx.setLineDash([]);
                ctx.fillStyle = ctx.strokeStyle;
                const largura = ctx.measureText(it.rotulo).width;
                const xt = x + 4 + largura > chartArea.right ? x - 4 - largura : x + 4;
                ctx.fillText(it.rotulo, xt, chartArea.top + 13 + (it.linha ?? 0) * 14);
            }
        }
        ctx.restore();
    }
};
let registrado = false;
function registrar() { if (!registrado && typeof Chart !== 'undefined') { Chart.register(marcadores); registrado = true; } }

/**
 * Gráfico PV/SP (e MV opcional). Dados: [{x: ms, y}]. Mantém uma única instância; atualize com atualizar().
 */
export function criarTendencia(canvas, { cv = false, ariaLabel = 'Gráfico de tendência' } = {}) {
    if (typeof Chart === 'undefined') return null;
    registrar();
    Chart.defaults.font.family = '"IBM Plex Mono", ui-monospace, monospace';
    Chart.defaults.font.size = 13;
    const c = cores();
    canvas.setAttribute('role', 'img');
    canvas.setAttribute('aria-label', ariaLabel);

    const chart = new Chart(canvas.getContext('2d'), {
        type: 'line',
        data: {
            datasets: [
                { label: 'PV', data: [], borderColor: c.pv, borderWidth: 2, pointRadius: 0, tension: 0, parsing: false, normalized: true, order: 1 },
                { label: 'SP', data: [], borderColor: c.sp, borderWidth: 1.5, borderDash: [6, 4], pointRadius: 0, stepped: 'before', parsing: false, normalized: true, order: 2 },
                { label: 'MV', data: [], borderColor: c.cv, borderWidth: 1.25, pointRadius: 0, tension: 0, parsing: false, normalized: true, hidden: !cv, order: 3 }
            ]
        },
        options: {
            responsive: true, maintainAspectRatio: false, animation: false,
            interaction: { mode: 'nearest', axis: 'x', intersect: false },
            scales: {
                x: { type: 'linear', ticks: { callback: (v) => hora(v), maxTicksLimit: 7, color: c.texto, maxRotation: 0 }, grid: { color: c.grade }, border: { color: c.linha } },
                y: { min: 0, max: 100, ticks: { callback: (v) => `${v}%`, color: c.texto, stepSize: 20 }, grid: { color: c.grade }, border: { color: c.linha } }
            },
            plugins: {
                legend: { display: false },
                decimation: { enabled: true, algorithm: 'min-max' },
                tooltip: {
                    backgroundColor: '#0a0d0c', titleColor: '#ecebe6', bodyColor: '#ecebe6', borderColor: '#2a302e', borderWidth: 1, cornerRadius: 0, displayColors: false,
                    callbacks: {
                        title: (items) => dataHora(items[0].parsed.x),
                        label: (ctx) => `${ctx.dataset.label}: ${num(ctx.parsed.y, 1)} %`
                    }
                },
                marcadores: { itens: [] }
            }
        }
    });
    return chart;
}

export function atualizar(chart, { pv, sp, cv, xmin, xmax, marcadores: m }) {
    if (!chart) return;
    if (pv) chart.data.datasets[0].data = pv;
    if (sp) chart.data.datasets[1].data = sp;
    if (cv) chart.data.datasets[2].data = cv;
    if (xmin !== undefined) chart.options.scales.x.min = xmin;
    if (xmax !== undefined) chart.options.scales.x.max = xmax;
    if (m) chart.options.plugins.marcadores.itens = m;
    chart.update('none');
}

export function aplicarTema(chart) {
    if (!chart) return;
    const c = cores();
    chart.data.datasets[0].borderColor = c.pv;
    chart.data.datasets[1].borderColor = c.sp;
    chart.data.datasets[2].borderColor = c.cv;
    for (const eixo of ['x', 'y']) {
        chart.options.scales[eixo].ticks.color = c.texto;
        chart.options.scales[eixo].grid.color = c.grade;
        chart.options.scales[eixo].border.color = c.linha;
    }
    chart.update('none');
}

export function destruir(chart) { try { chart?.destroy(); } catch { /* já destruído */ } }
