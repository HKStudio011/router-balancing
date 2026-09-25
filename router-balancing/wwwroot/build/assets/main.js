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
var r = { applyTheme: e }, i = {
	get: t,
	set: n
};
window.rbTheme = r, window.rbPanel = i, e(localStorage.getItem("rb.theme") ?? "system"), window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
	(localStorage.getItem("rb.theme") ?? "system") === "system" && e("system");
});
//#endregion
export { i as rbPanel, r as rbTheme };
