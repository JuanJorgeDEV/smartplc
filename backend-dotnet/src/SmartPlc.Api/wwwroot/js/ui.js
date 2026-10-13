// Componentes de interface compartilhados: avisos, confirmação e explicações de termos.
import { TERMOS } from './glossary.js';
import { esc } from './format.js';

export function aviso(tipo, texto, ms = 6000) {
    const caixa = document.getElementById('avisos');
    if (!caixa) return;
    const el = document.createElement('div');
    el.className = `aviso ${tipo}`;
    el.textContent = texto;
    caixa.appendChild(el);
    setTimeout(() => el.remove(), ms);
    while (caixa.children.length > 4) caixa.removeChild(caixa.firstChild);
}

/** Pede confirmação explícita. Devolve true/false. */
export function confirmar({ titulo, corpo, confirmarTexto = 'Confirmar', cancelarTexto = 'Cancelar' }) {
    const dlg = document.getElementById('dlg-confirmar');
    if (!dlg || typeof dlg.showModal !== 'function') return Promise.resolve(window.confirm(`${titulo}\n\n${corpo.replace(/<[^>]+>/g, '')}`));
    dlg.querySelector('[data-t]').textContent = titulo;
    dlg.querySelector('[data-c]').innerHTML = corpo;
    const ok = dlg.querySelector('[data-ok]');
    const no = dlg.querySelector('[data-no]');
    ok.textContent = confirmarTexto;
    no.textContent = cancelarTexto;
    return new Promise((resolve) => {
        const fechar = (valor) => { dlg.close(); limpar(); resolve(valor); };
        const aoOk = () => fechar(true);
        const aoNo = () => fechar(false);
        const aoCancelar = (e) => { e.preventDefault(); fechar(false); };
        const limpar = () => { ok.removeEventListener('click', aoOk); no.removeEventListener('click', aoNo); dlg.removeEventListener('cancel', aoCancelar); };
        ok.addEventListener('click', aoOk);
        no.addEventListener('click', aoNo);
        dlg.addEventListener('cancel', aoCancelar);
        dlg.showModal();
        no.focus(); // o foco inicial fica em "Cancelar": nada acontece por um Enter distraído
    });
}

/** Explicações curtas ao clicar (ou focar e pressionar Enter) em um termo. Um único listener delegado. */
export function iniciarTermos() {
    const pop = document.getElementById('termo-pop');
    if (!pop) return;
    let aberto = null;
    const fechar = () => { pop.hidden = true; aberto = null; };
    document.addEventListener('click', (e) => {
        const btn = e.target.closest?.('.term');
        if (!btn) { if (aberto) fechar(); return; }
        if (aberto === btn) { fechar(); return; }
        const t = TERMOS[btn.dataset.termo];
        if (!t) return;
        pop.innerHTML = `<strong>${esc(t[0])}</strong>${esc(t[1])}`;
        pop.hidden = false;
        const r = btn.getBoundingClientRect();
        const largura = Math.min(300, window.innerWidth - 24);
        pop.style.maxWidth = `${largura}px`;
        pop.style.left = `${Math.max(12, Math.min(r.left, window.innerWidth - largura - 12))}px`;
        const abaixo = r.bottom + 8;
        pop.style.top = `${abaixo + pop.offsetHeight > window.innerHeight ? Math.max(8, r.top - pop.offsetHeight - 8) : abaixo}px`;
        aberto = btn;
    });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && aberto) { const b = aberto; fechar(); b.focus(); } });
    window.addEventListener('scroll', () => aberto && fechar(), { passive: true });
}
