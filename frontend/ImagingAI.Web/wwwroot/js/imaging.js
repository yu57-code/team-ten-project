// 影像病例結構化平台：前端小工具（由 Blazor 透過 JS Interop 呼叫）

/** 讓瀏覽器下載一個文字檔。 */
window.imagingDownload = (fileName, content, mimeType) => {
    const blob = new Blob([content], { type: mimeType || 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
};
