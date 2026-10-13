// Formatação para a interface (pt-BR). Valor ausente nunca vira "0": vira "—".

export const clamp = (v, min, max) => Math.min(max, Math.max(min, v));

export function num(v, casas = 1) {
    if (v === null || v === undefined || Number.isNaN(Number(v))) return '—';
    return Number(v).toLocaleString('pt-BR', { minimumFractionDigits: casas, maximumFractionDigits: casas });
}

export function hora(t) {
    return new Date(t).toLocaleTimeString('pt-BR', { hour12: false });
}

export function dataHora(t) {
    const d = new Date(t);
    return `${d.toLocaleDateString('pt-BR')} ${d.toLocaleTimeString('pt-BR', { hour12: false })}`;
}

/** 125 -> "2 min 05 s" */
export function duracao(seg) {
    if (seg === null || seg === undefined || Number.isNaN(seg)) return '—';
    const s = Math.round(seg);
    if (s < 60) return `${s} s`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m} min ${String(s % 60).padStart(2, '0')} s`;
    return `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min`;
}

/** "há 3 s" */
export function idade(seg) {
    if (seg === null || seg === undefined) return '—';
    if (seg < 1) return 'agora';
    return `há ${duracao(seg)}`;
}

export function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/** Valor de <input type="datetime-local"> (hora local) a partir de um instante. */
export function paraInputLocal(t) {
    const d = new Date(t);
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}

export const iso = (t) => new Date(t).toISOString();

export function baixarArquivo(nome, texto, tipo = 'text/csv;charset=utf-8;') {
    const url = URL.createObjectURL(new Blob([texto], { type: tipo }));
    const a = document.createElement('a');
    a.href = url;
    a.download = nome;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export const carimboArquivo = () => new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');

/** CSV com ponto decimal (padrão de intercâmbio), independente do idioma do navegador. */
export const csvNum = (v) => (v === null || v === undefined ? '' : String(v));
