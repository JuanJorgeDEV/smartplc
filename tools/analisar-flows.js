#!/usr/bin/env node
// Analisa um flows.json exportado do Node-RED e imprime um inventário em Markdown.
// Somente leitura; não executa nada do fluxo. Credenciais NUNCA são impressas.
//
// Uso: node tools/analisar-flows.js caminho/flows.json [> docs/inventario-flows.md]

const fs = require('node:fs');

const arquivo = process.argv[2];
if (!arquivo) { console.error('Uso: node tools/analisar-flows.js <flows.json>'); process.exit(1); }

let nos;
try { nos = JSON.parse(fs.readFileSync(arquivo, 'utf8')); }
catch (e) { console.error(`Não consegui ler ${arquivo}: ${e.message}`); process.exit(1); }
if (!Array.isArray(nos)) { console.error('Formato inesperado: esperava um array de nós (Exportar > Todos os fluxos).'); process.exit(1); }

const porId = new Map(nos.map((n) => [n.id, n]));
const abas = nos.filter((n) => n.type === 'tab');
const config = nos.filter((n) => !n.z && n.type !== 'tab' && n.type !== 'subflow');
const fluxo = nos.filter((n) => n.z && !['comment', 'group'].includes(n.type));

const SEGREDOS = /pass|secret|token|key|credential|auth/i;
const nome = (n) => `${n.name ? `"${n.name}" ` : ''}\`${n.type}\` (${n.id})`;
const trecho = (s, max = 600) => (String(s).length > max ? String(s).slice(0, max) + ' …' : String(s));
const seguro = (obj) => Object.fromEntries(Object.entries(obj).map(([k, v]) => [k, SEGREDOS.test(k) ? '«omitido»' : v]));

console.log(`# Inventário do Node-RED\n\nArquivo: \`${arquivo}\` — ${nos.length} nós, ${abas.length} aba(s).\n`);

console.log('## Abas');
abas.forEach((t) => console.log(`- ${t.label || t.id}${t.disabled ? ' **(desabilitada)**' : ''}`));

console.log('\n## Tipos de nó (revela o protocolo usado com o CLP)');
const contagem = {};
fluxo.forEach((n) => { contagem[n.type] = (contagem[n.type] || 0) + 1; });
Object.entries(contagem).sort((a, b) => b[1] - a[1]).forEach(([t, c]) => console.log(`- \`${t}\`: ${c}`));

console.log('\n## Nós de configuração (conexões com o CLP / brokers) — segredos omitidos');
config.forEach((n) => {
    const { id, type, name, x, y, z, wires, ...resto } = n;
    console.log(`- ${nome(n)}: \`${JSON.stringify(seguro(resto))}\``);
});
if (!config.length) console.log('- (nenhum)');

console.log('\n## Gatilhos e intervalos de leitura');
fluxo.filter((n) => n.type === 'inject').forEach((n) => {
    const rep = n.repeat ? `a cada ${n.repeat} s` : (n.crontab ? `cron ${n.crontab}` : 'sem repetição');
    console.log(`- ${nome(n)}: ${rep}${n.once ? ', dispara ao iniciar' : ''}`);
});
fluxo.filter((n) => /^(s7|snap7|opcua|modbus|mqtt in)/i.test(n.type) || /s7|opcua|modbus/i.test(n.type)).forEach((n) => {
    const { id, type, name, x, y, z, wires, ...resto } = n;
    console.log(`- ${nome(n)}: \`${JSON.stringify(seguro(resto))}\``);
});

console.log('\n## Endpoints HTTP');
fluxo.filter((n) => n.type === 'http in').forEach((n) => console.log(`- ENTRADA ${nome(n)}: ${String(n.method).toUpperCase()} \`${n.url}\``));
fluxo.filter((n) => n.type === 'http request').forEach((n) => console.log(`- SAÍDA ${nome(n)}: ${n.method} \`${n.url || '(vem de msg.url)'}\``));
fluxo.filter((n) => n.type === 'http response').forEach((n) => console.log(`- RESPOSTA ${nome(n)}: status \`${n.statusCode || '(msg.statusCode)'}\``));

console.log('\n## Nós function (lógica de conversão/mapeamento)');
fluxo.filter((n) => n.type === 'function').forEach((n) => {
    console.log(`\n### ${nome(n)}\n\`\`\`js\n${trecho(n.func, 1500)}\n\`\`\``);
});

console.log('\n## Nós change/switch (mapeamento de campos)');
fluxo.filter((n) => n.type === 'change').forEach((n) => {
    (n.rules || []).forEach((r) => console.log(`- ${nome(n)}: ${r.t} \`${r.p}\` ← ${r.pt === 'str' || r.pt === 'num' || r.pt === 'bool' ? `\`${r.to}\`` : `${r.pt}:\`${r.to}\``}`));
});

// ---- Verificação de contrato com o backend .NET ----
console.log('\n## Verificação contra o contrato do backend');
const OBRIG = ['nivel', 'valvula'];
const OPC = ['setpoint', 'modoManual', 'valvulaManual', 'kp', 'ti', 'td'];
const textoDeCodigo = fluxo
    .filter((n) => n.type === 'function' || n.type === 'change' || n.type === 'template')
    .map((n) => `${n.func || ''} ${n.template || ''} ${JSON.stringify(n.rules || '')}`)
    .join('\n');
const usa = (campo) => new RegExp(`\\b${campo}\\b`).test(textoDeCodigo);
OBRIG.forEach((c) => console.log(`- obrigatório \`${c}\`: ${usa(c) ? 'mencionado nos nós function/change' : '**NÃO ENCONTRADO**'}`));
OPC.forEach((c) => console.log(`- opcional \`${c}\`: ${usa(c) ? 'mencionado' : 'não encontrado'}`));

const reqTelemetria = fluxo.filter((n) => n.type === 'http request' && /\/api\/telemetria/.test(n.url || ''));
console.log(`- nós que chamam \`/api/telemetria\`: ${reqTelemetria.length ? reqTelemetria.map((n) => n.id).join(', ') : '**nenhum**'}`);
fluxo.filter((n) => n.type === 'http request' && /:3000|:5000/.test(n.url || '')).forEach((n) =>
    console.log(`- destino configurado em ${n.id}: \`${n.url}\` (confirmar para qual backend aponta)`));
fluxo.filter((n) => n.type === 'http request' && n.headers && Object.keys(n.headers).length).forEach((n) =>
    console.log(`- ${n.id} envia cabeçalhos: ${Object.keys(n.headers).join(', ')}`));

// ---- Caminho de cada fluxo de leitura até o POST ----
console.log('\n## Caminhos (origem -> ... -> http request)');
const paraDestino = (n, visitados = new Set(), trilha = []) => {
    if (visitados.has(n.id)) return [];
    visitados.add(n.id);
    const t = [...trilha, n.name || n.type];
    if (n.type === 'http request' || !(n.wires || []).flat().length) return [t.join(' -> ')];
    return (n.wires || []).flat().flatMap((id) => (porId.get(id) ? paraDestino(porId.get(id), visitados, t) : []));
};
const alvos = fluxo.filter((n) => !fluxo.some((o) => (o.wires || []).flat().includes(n.id)) && (n.wires || []).flat().length);
alvos.forEach((n) => paraDestino(n).forEach((c) => console.log(`- ${c}`)));
