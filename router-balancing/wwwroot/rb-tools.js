//#region rbTools — tiện ích trang Tools
// Tải text thành file: Blazor trong WebView2 không có dialog save native — dùng Blob URL
// + thẻ <a download>. UTF-8 giữ nguyên tiếng Việt trong file config.
window.rbTools = {
	download: function (filename, text) {
		var blob = new Blob([text], { type: "application/json;charset=utf-8" });
		var url = URL.createObjectURL(blob);
		var a = document.createElement("a");
		a.href = url;
		a.download = filename;
		document.body.appendChild(a);
		a.click();
		a.remove();
		URL.revokeObjectURL(url);
	}
};
//#endregion
