// Apresentação fixa das variáveis (casas decimais e o que aparece em destaque). Não há mais personalização por usuário:
// a tela mostra sempre o mesmo padrão, definido aqui, e nada é guardado no navegador.

export const PADRAO = Object.freeze({
    variaveis: {
        nivel: { nome: '', casas: 1, destaque: true },
        valvula: { nome: '', casas: 1, destaque: true },
        setpoint: { nome: '', casas: 1, destaque: true },
        modoManual: { nome: '', casas: 0, destaque: true },
        kp: { nome: '', casas: 2, destaque: false },
        ti: { nome: '', casas: 1, destaque: false },
        td: { nome: '', casas: 1, destaque: false },
        valvulaManual: { nome: '', casas: 1, destaque: false }
    },
    indicadores: { tendencia: true, alarmes: true, resumo: true }
});

export const prefs = () => PADRAO;
export const nomeDe = (_campo, padrao) => padrao;
export const casasDe = (campo) => PADRAO.variaveis[campo]?.casas ?? 1;
/** Mantida por compatibilidade com as telas: não há mais o que observar. */
export const aoMudar = () => () => {};
