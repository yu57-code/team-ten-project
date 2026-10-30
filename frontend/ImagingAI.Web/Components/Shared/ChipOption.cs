namespace ImagingAI.Web.Components.Shared;

/// <summary>FilterChips 的一個選項；Count 有值時會顯示在文字後面。</summary>
public sealed record ChipOption<TValue>(TValue Value, string Label, int? Count = null);
