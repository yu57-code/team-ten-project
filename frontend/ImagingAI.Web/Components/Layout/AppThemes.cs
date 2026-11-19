using MudBlazor;

namespace ImagingAI.Web.Components.Layout;

/// <summary>
/// 兩種角色的 MudBlazor 主題，色票取自已確認的設計稿：
/// 審核人員 = 深藍（#1E3A8A / 側欄 #14244D），管理員 = 藍綠（#0F766E / 側欄 #1B2430）。
/// </summary>
public static class AppThemes
{
    private static readonly string[] FontStack =
        ["Noto Sans TC", "system-ui", "-apple-system", "Segoe UI", "Microsoft JhengHei", "sans-serif"];

    public static MudTheme Reviewer { get; } = Create(
        primary: "#1E3A8A",
        background: "#F4F6F9",
        lines: "#E2E6EC");

    public static MudTheme Admin { get; } = Create(
        primary: "#0F766E",
        background: "#F3F5F5",
        lines: "#E1E6E8");

    private static MudTheme Create(string primary, string background, string lines) => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = primary,
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#5A6577",
            Tertiary = "#5B2BB5",
            Info = "#1E40AF",
            Success = "#14653A",
            Warning = "#D97706",
            Error = "#DC2626",
            TextPrimary = "#16202E",
            TextSecondary = "#5A6577",
            ActionDefault = "#5A6577",
            Background = background,
            Surface = "#FFFFFF",
            LinesDefault = lines,
            LinesInputs = "#CBD2DC",
            TableLines = "#EDF0F4",
            Divider = lines,
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = FontStack,
                FontSize = "0.875rem",
                LineHeight = "1.6",
                LetterSpacing = "0",
            },
            Button = new ButtonTypography
            {
                FontFamily = FontStack,
                FontSize = "0.875rem",
                FontWeight = "500",
                LetterSpacing = "0",
                TextTransform = "none",
            },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };
}
