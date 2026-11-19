using Microsoft.AspNetCore.Components.Routing;

namespace ImagingAI.Web.Components.Layout;

/// <summary>側邊選單數字徽章的顏色：紫 = 待覆核，紅 = 異常／失敗。</summary>
public enum NavBadgeTone
{
    Purple,
    Red
}

/// <summary>側邊選單的一個項目。</summary>
public sealed record NavItem(
    string Text,
    string Href,
    string Icon,
    NavLinkMatch Match = NavLinkMatch.Prefix,
    int? Badge = null,
    NavBadgeTone BadgeTone = NavBadgeTone.Red);
