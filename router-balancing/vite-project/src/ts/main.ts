import '../css/style.css';

type PanelFallback = boolean;

function applyTheme(theme: string): void {
    const resolved = theme === 'system'
        ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
        : theme;
    document.documentElement.setAttribute('data-theme', resolved);
    // Persist để lần mở app sau (trước khi Blazor init) đọc lại được — tránh flash sai theme
    localStorage.setItem('rb.theme', theme);
}

function getPanel(id: string, fallback: PanelFallback): boolean {
    const raw = localStorage.getItem(`rb.panel.${id}`);
    return raw === null ? fallback : raw === '1';
}

function setPanel(id: string, expanded: boolean): void {
    localStorage.setItem(`rb.panel.${id}`, expanded ? '1' : '0');
}

const rbTheme = { applyTheme };
// Bản viết tắt `{ get, set }` trong brief không biên dịch được (không có identifier get/set) — ánh xạ tường minh, giữ nguyên contract rbPanel.get/rbPanel.set
const rbPanel = { get: getPanel, set: setPanel };

declare global {
    interface Window {
        rbTheme: typeof rbTheme;
        rbPanel: typeof rbPanel;
    }
}

window.rbTheme = rbTheme;
window.rbPanel = rbPanel;

applyTheme(localStorage.getItem('rb.theme') ?? 'system');

window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if ((localStorage.getItem('rb.theme') ?? 'system') === 'system') {
        applyTheme('system');
    }
});

export { rbTheme, rbPanel };
