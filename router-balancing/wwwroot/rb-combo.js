//#region rbCombo — placement cho dropdown ModelCombo
// Menu dùng position:fixed theo tọa độ trigger để không bị overflow-y-auto của dialog
// (Modal.razor) cắt mất khi form dài. Mở dưới trigger nếu đủ chỗ, ngược lại mở lên trên.
// Fallback CSS absolute nếu JS này lỗi (xem ModelCombo.razor).
window.rbCombo = {
	place: function (trigger, menu) {
		if (!trigger || !menu) return;
		var r = trigger.getBoundingClientRect();
		var vh = window.innerHeight;
		menu.style.position = "fixed";
		menu.style.left = r.left + "px";
		menu.style.width = r.width + "px";
		menu.style.right = "auto";
		// 240px: đủ search + ~5 dòng; dưới 160px coi như không đủ chỗ để mở dưới
		var minNeed = 240;
		var below = vh - r.bottom - 8;
		var above = r.top - 8;
		if (below >= minNeed || below >= above) {
			menu.style.top = r.bottom + 4 + "px";
			menu.style.bottom = "auto";
			menu.style.maxHeight = Math.max(140, below - 8) + "px";
		} else {
			menu.style.top = "auto";
			menu.style.bottom = vh - r.top + 4 + "px";
			menu.style.maxHeight = Math.max(140, above - 12) + "px";
		}
	}
};
//#endregion
