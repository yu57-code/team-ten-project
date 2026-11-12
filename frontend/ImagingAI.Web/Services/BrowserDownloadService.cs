using Microsoft.JSInterop;

namespace ImagingAI.Web.Services;

/// <summary>讓瀏覽器下載文字檔（例如 FHIR JSON）。對應 wwwroot/js/imaging.js 的 imagingDownload。</summary>
public sealed class BrowserDownloadService(IJSRuntime js)
{
    public ValueTask DownloadTextAsync(string fileName, string content, string mimeType = "application/json") =>
        js.InvokeVoidAsync("imagingDownload", fileName, content, mimeType);
}
