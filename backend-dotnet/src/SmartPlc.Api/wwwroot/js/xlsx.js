// Gerador mínimo de planilha .xlsx (Office Open XML), sem dependências e sem rede.
// Escreve só o necessário: abas, cabeçalho em negrito, primeira linha congelada, filtro, largura de coluna,
// datas de verdade, números com formato e textos. Texto vai como texto: nunca é interpretado como fórmula.
//
// abas = [{ nome, colunas: [{ titulo, largura?, formato? }], linhas: [[valor, ...], ...], filtro? }]
// Um valor pode ser número, texto, booleano, Date, null/undefined/'' (célula vazia) ou { data: ms } (instante).
// formato: '0', '0.0', '0.00' (números) ou 'data' (instante, em hora local).

const enc = new TextEncoder();

// ---------- ZIP (sem compressão) ----------
const TABELA_CRC = (() => {
    const t = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
        let c = n;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
        t[n] = c >>> 0;
    }
    return t;
})();

export function crc32(bytes) {
    let c = 0xffffffff;
    for (let i = 0; i < bytes.length; i++) c = TABELA_CRC[(c ^ bytes[i]) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
}

function zip(arquivos) {
    const partes = [], central = [];
    let deslocamento = 0;
    // 1980-01-01 00:00:00 (campos de data/hora do formato ZIP)
    const hora = 0, data = (0 << 9) | (1 << 5) | 1;
    for (const { nome, bytes } of arquivos) {
        const n = enc.encode(nome), crc = crc32(bytes);
        const lh = new DataView(new ArrayBuffer(30));
        lh.setUint32(0, 0x04034b50, true); lh.setUint16(4, 20, true); lh.setUint16(6, 0x0800, true); lh.setUint16(8, 0, true);
        lh.setUint16(10, hora, true); lh.setUint16(12, data, true); lh.setUint32(14, crc, true);
        lh.setUint32(18, bytes.length, true); lh.setUint32(22, bytes.length, true); lh.setUint16(26, n.length, true); lh.setUint16(28, 0, true);
        partes.push(new Uint8Array(lh.buffer), n, bytes);
        const ch = new DataView(new ArrayBuffer(46));
        ch.setUint32(0, 0x02014b50, true); ch.setUint16(4, 20, true); ch.setUint16(6, 20, true); ch.setUint16(8, 0x0800, true); ch.setUint16(10, 0, true);
        ch.setUint16(12, hora, true); ch.setUint16(14, data, true); ch.setUint32(16, crc, true);
        ch.setUint32(20, bytes.length, true); ch.setUint32(24, bytes.length, true); ch.setUint16(28, n.length, true);
        ch.setUint32(42, deslocamento, true);
        central.push(new Uint8Array(ch.buffer), n);
        deslocamento += 30 + n.length + bytes.length;
    }
    const tamCentral = central.reduce((s, p) => s + p.length, 0);
    const fim = new DataView(new ArrayBuffer(22));
    fim.setUint32(0, 0x06054b50, true); fim.setUint16(8, arquivos.length, true); fim.setUint16(10, arquivos.length, true);
    fim.setUint32(12, tamCentral, true); fim.setUint32(16, deslocamento, true);
    const todas = [...partes, ...central, new Uint8Array(fim.buffer)];
    const saida = new Uint8Array(todas.reduce((s, p) => s + p.length, 0));
    let pos = 0;
    for (const p of todas) { saida.set(p, pos); pos += p.length; }
    return saida;
}

// ---------- XML ----------
const xml = (s) => String(s).replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

export function letraColuna(i) {
    let s = '';
    for (let n = i + 1; n > 0; n = Math.floor((n - 1) / 26)) s = String.fromCharCode(65 + ((n - 1) % 26)) + s;
    return s;
}

/** Instante (ms desde 1970) -> número de série do Excel, na hora local (o Excel não guarda fuso). */
export function serialExcel(ms, deslocamentoMin = new Date(ms).getTimezoneOffset()) {
    const dias = (ms - deslocamentoMin * 60000) / 86400000 + 25569;
    return Math.round(dias * 1e9) / 1e9;
}

const FORMATOS_NATIVOS = { '0': 1, '0.00': 2 };

function nomeDeAba(n, usados) {
    let base = String(n).replace(/[\[\]:*?/\\]/g, ' ').trim().slice(0, 31) || 'Planilha';
    let nome = base, i = 2;
    while (usados.has(nome.toLowerCase())) nome = `${base.slice(0, 28)} ${i++}`;
    usados.add(nome.toLowerCase());
    return nome;
}

function estilos(abas) {
    // índice 0: padrão · 1: cabeçalho · 2: data · depois, um por formato numérico
    const personalizados = [];
    const indiceDe = new Map();
    const xfs = ['<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>',
        '<xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1"/>',
        '<xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>'];
    personalizados.push('<numFmt numFmtId="164" formatCode="dd/mm/yyyy\\ hh:mm:ss"/>');
    for (const aba of abas) for (const c of aba.colunas) {
        const f = c.formato;
        if (!f || f === 'data' || indiceDe.has(f)) continue;
        let id = FORMATOS_NATIVOS[f];
        if (id === undefined) { id = 165 + personalizados.length - 1; personalizados.push(`<numFmt numFmtId="${id}" formatCode="${xml(f)}"/>`); }
        indiceDe.set(f, xfs.length);
        xfs.push(`<xf numFmtId="${id}" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>`);
    }
    const texto = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        + '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
        + `<numFmts count="${personalizados.length}">${personalizados.join('')}</numFmts>`
        + '<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>'
        + '<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>'
        + '<fill><patternFill patternType="solid"><fgColor rgb="FFE6E5DF"/><bgColor indexed="64"/></patternFill></fill></fills>'
        + '<borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border>'
        + '<border><left/><right/><top/><bottom style="thin"><color auto="1"/></bottom><diagonal/></border></borders>'
        + '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>'
        + `<cellXfs count="${xfs.length}">${xfs.join('')}</cellXfs>`
        + '<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>';
    return { texto, indiceDe };
}

function celula(ref, valor, formato, indiceDe) {
    if (valor === null || valor === undefined || valor === '') return '';
    if (valor instanceof Date) valor = { data: valor.getTime() };
    if (typeof valor === 'object' && valor !== null && 'data' in valor) {
        return Number.isFinite(valor.data) ? `<c r="${ref}" s="2"><v>${serialExcel(valor.data)}</v></c>` : '';
    }
    if (typeof valor === 'boolean') return `<c r="${ref}" t="b"><v>${valor ? 1 : 0}</v></c>`;
    if (typeof valor === 'number') {
        if (!Number.isFinite(valor)) return '';
        const s = formato && indiceDe.has(formato) ? ` s="${indiceDe.get(formato)}"` : '';
        return `<c r="${ref}"${s}><v>${valor}</v></c>`;
    }
    return `<c r="${ref}" t="inlineStr"><is><t xml:space="preserve">${xml(valor)}</t></is></c>`;
}

function planilha(aba, indiceDe) {
    const nCol = aba.colunas.length;
    const ultima = Math.max(1, aba.linhas.length + 1);
    const cols = aba.colunas.map((c, i) => `<col min="${i + 1}" max="${i + 1}" width="${c.largura ?? 16}" customWidth="1"/>`).join('');
    const cab = aba.colunas.map((c, i) => `<c r="${letraColuna(i)}1" s="1" t="inlineStr"><is><t>${xml(c.titulo)}</t></is></c>`).join('');
    const linhas = aba.linhas.map((l, r) => `<row r="${r + 2}">${aba.colunas.map((c, i) => celula(`${letraColuna(i)}${r + 2}`, l[i], c.formato, indiceDe)).join('')}</row>`).join('');
    return '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        + '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
        + `<dimension ref="A1:${letraColuna(nCol - 1)}${ultima}"/>`
        + '<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/><selection pane="bottomLeft"/></sheetView></sheetViews>'
        + '<sheetFormatPr defaultRowHeight="15"/>'
        + `<cols>${cols}</cols>`
        + `<sheetData><row r="1">${cab}</row>${linhas}</sheetData>`
        + (aba.filtro && aba.linhas.length ? `<autoFilter ref="A1:${letraColuna(nCol - 1)}${ultima}"/>` : '')
        + '</worksheet>';
}

/** Monta o arquivo .xlsx e devolve os bytes. */
export function criarXlsx(abas) {
    if (!abas?.length) throw new Error('Informe ao menos uma aba.');
    const usados = new Set();
    const nomes = abas.map((a) => nomeDeAba(a.nome, usados));
    const { texto: estilosXml, indiceDe } = estilos(abas);
    const arquivos = [
        ['[Content_Types].xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            + '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>'
            + '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
            + '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
            + abas.map((_, i) => `<Override PartName="/xl/worksheets/sheet${i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join('')
            + '</Types>'],
        ['_rels/.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            + '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'],
        ['xl/workbook.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
            + `<sheets>${nomes.map((n, i) => `<sheet name="${xml(n)}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`).join('')}</sheets></workbook>`],
        ['xl/_rels/workbook.xml.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            + abas.map((_, i) => `<Relationship Id="rId${i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet${i + 1}.xml"/>`).join('')
            + `<Relationship Id="rId${abas.length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>`],
        ['xl/styles.xml', estilosXml],
        ...abas.map((a, i) => [`xl/worksheets/sheet${i + 1}.xml`, planilha(a, indiceDe)])
    ].map(([nome, texto]) => ({ nome, bytes: enc.encode(texto) }));
    return zip(arquivos);
}

export const TIPO_XLSX = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

/** Gera a planilha e inicia o download no navegador. */
export function baixarXlsx(nome, abas) {
    const url = URL.createObjectURL(new Blob([criarXlsx(abas)], { type: TIPO_XLSX }));
    const a = document.createElement('a');
    a.href = url;
    a.download = nome;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
