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

// Cuộn vùng sơ đồ Live Trace đưa điểm (x, y) về giữa — điểm đã nằm trong viewport
// (kèm margin) thì không làm gì, để không giật khi người dùng cuộn tay hoặc khi nhiều
// event trong cùng tick đã thấy hết thì không cần nhảy lần nữa
function scrollTo(el: Element | null, x: number, y: number): void {
    if (!el)
        return;
    const m = 8;
    if (x >= el.scrollLeft + m && x <= el.scrollLeft + el.clientWidth - m
        && y >= el.scrollTop + m && y <= el.scrollTop + el.clientHeight - m) {
        return;
    }
    // scrollTo tự clamp về biên scroll — chỉ cần chặn tọa độ âm (điểm ngoài mép trái/đầu)
    el.scrollTo({
        left: Math.max(0, x - el.clientWidth / 2),
        top: Math.max(0, y - el.clientHeight / 2),
        behavior: 'smooth',
    });
}

const rbTheme = { applyTheme };
// Bản viết tắt `{ get, set }` trong brief không biên dịch được (không có identifier get/set) — ánh xạ tường minh, giữ nguyên contract rbPanel.get/rbPanel.set
const rbPanel = { get: getPanel, set: setPanel };
const rbTrace = { scrollTo };

declare global {
    interface Window {
        rbTheme: typeof rbTheme;
        rbPanel: typeof rbPanel;
        rbTrace: typeof rbTrace;
    }
}

window.rbTheme = rbTheme;
window.rbPanel = rbPanel;
window.rbTrace = rbTrace;

applyTheme(localStorage.getItem('rb.theme') ?? 'system');

window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if ((localStorage.getItem('rb.theme') ?? 'system') === 'system') {
        applyTheme('system');
    }
});

export { rbTheme, rbPanel, rbTrace };
