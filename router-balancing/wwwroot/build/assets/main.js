//#region src/ts/main.ts
function e(e) {
	let t = e === "system" ? window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light" : e;
	document.documentElement.setAttribute("data-theme", t), localStorage.setItem("rb.theme", e);
}
function t(e, t) {
	let n = localStorage.getItem(`rb.panel.${e}`);
	return n === null ? t : n === "1";
}
function n(e, t) {
	localStorage.setItem(`rb.panel.${e}`, t ? "1" : "0");
}
function r(e, t, n) {
	e && (t >= e.scrollLeft + 8 && t <= e.scrollLeft + e.clientWidth - 8 && n >= e.scrollTop + 8 && n <= e.scrollTop + e.clientHeight - 8 || e.scrollTo({
		left: Math.max(0, t - e.clientWidth / 2),
		top: Math.max(0, n - e.clientHeight / 2),
		behavior: "smooth"
	}));
}
var i = { applyTheme: e }, a = {
	get: t,
	set: n
}, o = { scrollTo: r };
window.rbTheme = i, window.rbPanel = a, window.rbTrace = o, e(localStorage.getItem("rb.theme") ?? "system"), window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
	(localStorage.getItem("rb.theme") ?? "system") === "system" && e("system");
});
//#endregion
export { a as rbPanel, i as rbTheme, o as rbTrace };
