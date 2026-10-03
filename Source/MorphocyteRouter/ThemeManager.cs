using System.Windows;
using System.Windows.Media;

namespace MorphocyteRouter;

internal static class ThemeManager
{
    internal static readonly string[] ThemeNames =
    {
        "Тёмная морфоцитная", "Светлая морфоцитная", "Аврора", "Космический градиент",
        "Графит", "Океан", "Аметист", "Закат", "Тёплый свет"
    };

    private sealed record Palette(
        string Canvas, string TitleBar, string Panel, string PanelBottom, string Surface, string Input, string Popup,
        string Text, string Muted, string Dim, string Accent, string Secondary, string OnAccent, string Border,
        string BorderStrong, string Hover, string Selected, string SelectedStrong, string Danger, string DangerPanel,
        string LogBackground, string LogText, string Orbit, string OrbitFill, string BackgroundStart,
        string BackgroundMiddle, string BackgroundEnd, string AccentStart, string AccentMiddle, string AccentEnd,
        string PanelStart, string PanelEnd, string Shadow, bool Light);

    private static readonly IReadOnlyDictionary<string, Palette> Palettes = new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase)
    {
        ["Тёмная морфоцитная"] = new(
            "#0B1211", "#59060B0A", "#D0182A26", "#B40E1715", "#101C1A", "#101A19", "#0D1715",
            "#EAF7F2", "#7F9B94", "#6D8B82", "#73FFBF", "#67E8F9", "#07120E", "#293F3C",
            "#42685E", "#142720", "#17372E", "#1A4033", "#E56F7E", "#321B21",
            "#0A1110", "#9AB5AC", "#4EEFAE", "#103F31", "#173B31", "#0B1211", "#080D0C",
            "#91FFD6", "#61F1B4", "#67E8F9", "#D0182A26", "#B40E1715", "#000000", false),
        ["Светлая морфоцитная"] = new(
            "#E9F1ED", "#E3ECE7", "#FAFCFB", "#EAF2EE", "#FFFFFF", "#F8FBF9", "#F3F8F5",
            "#18342B", "#526D63", "#5D776B", "#066C4D", "#147E9A", "#082D20", "#C5D7CE",
            "#91B3A4", "#E4F2EB", "#D3EADF", "#B7DDCD", "#B73B53", "#FBECEF",
            "#F3F8F5", "#526D63", "#48A788", "#DAEEE5", "#FFFFFF", "#EFF6F2", "#E1ECE7",
            "#77DDB5", "#51CBA7", "#67C9D4", "#FAFCFB", "#EAF2EE", "#42695A", true),
        ["Аврора"] = new(
            "#071419", "#102A30", "#14343B", "#0E222C", "#10242A", "#0D2025", "#0C1B21",
            "#E7FBFA", "#91B8BB", "#688F94", "#5CE7D2", "#67C8FF", "#06201D", "#244A51",
            "#3D7A7B", "#15353A", "#19454A", "#21585B", "#FF8299", "#381F2A",
            "#09191E", "#9CC9C8", "#70F1D6", "#12473E", "#15515A", "#0B202B", "#10152B",
            "#79F4DA", "#59D9F4", "#929BFF", "#14343B", "#0E222C", "#000000", false),
        ["Космический градиент"] = new(
            "#120F1F", "#241833", "#30213F", "#211A35", "#1B1730", "#17152A", "#171326",
            "#F5EFFD", "#B1A4C7", "#8E83A8", "#C2A5FF", "#F08CBD", "#20132C", "#403254",
            "#68527D", "#302541", "#3B2B50", "#493158", "#FF8299", "#40202F",
            "#171326", "#B6A9CB", "#C49AFF", "#38204F", "#38264C", "#20172F", "#111322",
            "#C2A5FF", "#F08CBD", "#70DDF4", "#30213F", "#211A35", "#000000", false),
        ["Графит"] = new(
            "#111419", "#171C23", "#1C232C", "#141B23", "#19212A", "#141B23", "#161D26",
            "#ECF2FA", "#A0ADBE", "#8493A8", "#B2C9E8", "#80B6EF", "#101C2B", "#344152",
            "#51677F", "#242F3D", "#2B3B4E", "#344D66", "#FF8A9E", "#3E2330",
            "#10161D", "#AFBDCE", "#92B9E5", "#23354A", "#263445", "#171F2A", "#111419",
            "#DAE6F7", "#ACC6E9", "#87B3E9", "#1C232C", "#141B23", "#000000", false),
        ["Океан"] = new(
            "#081522", "#102536", "#123248", "#0C2234", "#10283C", "#0B2031", "#0B1D2C",
            "#E8F6FF", "#96B8D0", "#7097B2", "#64D2FF", "#80EAD9", "#062435", "#28506C",
            "#3A7897", "#163B52", "#1A4964", "#215C77", "#FF8B9C", "#3C2633",
            "#071927", "#A4C9DF", "#56C5FF", "#123C56", "#134763", "#0B283E", "#10182E",
            "#8AE9F5", "#65CFFF", "#8CA6F5", "#123248", "#0C2234", "#000000", false),
        ["Аметист"] = new(
            "#15111E", "#21192E", "#2C233D", "#1E192C", "#221C30", "#1A1626", "#1C172A",
            "#F6F0FC", "#BAA9CD", "#9786AC", "#D5B2FF", "#9DC6FF", "#26153A", "#4C3A61",
            "#76608E", "#352945", "#433257", "#513D68", "#FF98AE", "#402534",
            "#181320", "#C0AED4", "#C496EF", "#3C2553", "#382A49", "#231A31", "#15111E",
            "#E3C8FF", "#C8A4FA", "#A7BDFC", "#2C233D", "#1E192C", "#000000", false),
        ["Закат"] = new(
            "#21141B", "#321D28", "#422632", "#2B1B29", "#301E2B", "#271824", "#291A25",
            "#FFF2E9", "#D1ACB7", "#AF8694", "#FFC08E", "#FF9EB6", "#34182A", "#64404D",
            "#96616D", "#472B38", "#573242", "#6B3B4C", "#FFA3AF", "#4C2331",
            "#21151E", "#D9B6BD", "#FFAD86", "#5B313A", "#65373A", "#362033", "#1C182B",
            "#FFD19F", "#FFAD9D", "#EBA3D1", "#422632", "#2B1B29", "#000000", false),
        ["Тёплый свет"] = new(
            "#F4EDE5", "#F1E5D7", "#FFFCF7", "#F4EADF", "#FFFDF9", "#FCF6EE", "#FFF8EF",
            "#403024", "#77604D", "#846B57", "#934622", "#327B82", "#402015", "#DDCBB8",
            "#BCA084", "#F4E6D5", "#EFD9BF", "#E6CBA7", "#AF3E50", "#FBEAED",
            "#FBF5ED", "#745C4A", "#BD8052", "#EFDBC2", "#FFFDF9", "#F8EFE3", "#F0E0CF",
            "#F7C994", "#EDB27E", "#D7BF92", "#FFFCF7", "#F4EADF", "#79583C", true)
    };

    internal static string CurrentTheme { get; private set; } = ThemeNames[0];

    internal static string Normalize(string? name) =>
        ThemeNames.FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) ?? ThemeNames[0];

    internal static void Apply(string? name)
    {
        var theme = Normalize(name);
        if (Application.Current is null || !Palettes.TryGetValue(theme, out var p)) return;

        // Themes replace resources rather than mutating them. Frozen brushes avoid
        // change notifications and can be reused safely by all visual elements.
        SolidColorBrush BrushColor(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        SolidColorBrush Brush(string color) => BrushColor(Parse(color));
        LinearGradientBrush Gradient(params (string Color, double Offset)[] stops)
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            foreach (var stop in stops) gradient.GradientStops.Add(new GradientStop(Parse(stop.Color), stop.Offset));
            gradient.Freeze();
            return gradient;
        }
        var accentGradient = Gradient((p.AccentStart, 0), (p.AccentMiddle, 0.58), (p.AccentEnd, 1));
        var panelGradient = Gradient((p.PanelStart, 0), (p.PanelEnd, 1));
        var backgroundGradient = Gradient((p.BackgroundStart, 0), (p.BackgroundMiddle, 0.53), (p.BackgroundEnd, 1));
        // A primary button uses a light gradient; a checked box uses the solid
        // accent. Light palettes need separate foregrounds for these backgrounds.
        var checkMark = p.Light ? "#FFFFFF" : p.OnAccent;
        var resources = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["ThemeCanvas"] = Brush(p.Canvas), ["ThemeAppOutline"] = accentGradient, ["ThemeAppInnerOutline"] = Brush(p.Border), ["ThemeAppGlow"] = Brush(p.Accent),
            ["ThemeAccentGlowColor"] = Parse(p.Accent), ["ThemeStatusNeutral"] = Brush(p.Muted),
            ["ThemeBackgroundGradient"] = backgroundGradient, ["ThemeTitleBar"] = Brush(p.TitleBar),
            ["ThemePanelSolid"] = Brush(p.Panel), ["ThemePanelGradient"] = panelGradient, ["ThemePanelBorder"] = Brush(p.BorderStrong),
            ["ThemeSurface"] = Brush(p.Surface), ["ThemeSurfaceBorder"] = Brush(p.Border), ["ThemeSurfaceHover"] = Brush(p.Hover),
            ["ThemeSurfaceHoverBorder"] = Brush(p.BorderStrong), ["ThemeSurfaceSelected"] = Brush(p.Selected),
            ["ThemeSurfaceSelectedBorder"] = Brush(p.Accent), ["ThemeSurfaceActive"] = Brush(p.SelectedStrong),
            ["ThemeInputBackground"] = Brush(p.Input), ["ThemeInputBorder"] = Brush(p.BorderStrong), ["ThemeInputHint"] = Brush(p.Dim),
            ["ThemePopup"] = Brush(p.Popup), ["ThemePopupBorder"] = Brush(p.BorderStrong), ["ThemeComboDivider"] = Brush(p.Border),
            ["ThemeComboHover"] = Brush(p.Hover), ["ThemeComboSelected"] = Brush(p.Selected),
            ["ThemeText"] = Brush(p.Text), ["ThemeMuted"] = Brush(p.Muted), ["ThemeDim"] = Brush(p.Dim),
            ["ThemeAccent"] = Brush(p.Accent), ["ThemeAccentSecondary"] = Brush(p.Secondary), ["ThemeAccentSoft"] = Brush(p.AccentStart),
            ["ThemeAccentGradient"] = accentGradient, ["ThemeOnAccent"] = Brush(p.OnAccent), ["ThemeDanger"] = Brush(p.Danger),
            ["ThemeButton"] = Brush(p.Surface), ["ThemeButtonBorder"] = Brush(p.BorderStrong), ["ThemeButtonHover"] = Brush(p.Hover),
            ["ThemeButtonPrimaryText"] = Brush(p.OnAccent), ["ThemeTooltipBackground"] = Brush(p.Panel), ["ThemeTooltipBorder"] = Brush(p.BorderStrong),
            ["ThemeSelectionBackground"] = BrushColor(WithAlpha(Blend(Parse(p.Accent), Parse(p.Surface), 0.48), 120)),
            ["ThemeSelectionText"] = Brush(p.Text), ["ThemeCheckboxBackground"] = Brush(p.Input), ["ThemeCheckboxBorder"] = Brush(p.BorderStrong),
            ["ThemeCheckboxMark"] = Brush(checkMark), ["ThemeScrollbarThumb"] = BrushColor(Blend(Parse(p.BorderStrong), Parse(p.Accent), 0.22)),
            ["ThemeScrollbarHover"] = Brush(p.Accent), ["ThemeScrollbarBorder"] = Brush(p.BorderStrong), ["ThemeExpanderBackground"] = Brush(p.Selected),
            ["ThemeExpanderBorder"] = Brush(p.BorderStrong), ["ThemeRuleListBackground"] = BrushColor(WithAlpha(Parse(p.Panel), (byte)(p.Light ? 225 : 178))),
            ["ThemeRuleListBorder"] = Brush(p.Border), ["ThemeRuleBackground"] = Brush(p.Surface), ["ThemeRuleBorder"] = Brush(p.Border),
            ["ThemeRuleHover"] = Brush(p.Hover), ["ThemeRuleHoverBorder"] = Brush(p.Secondary), ["ThemeRuleSelected"] = Brush(p.Selected),
            ["ThemeRuleSelectedBorder"] = Brush(p.Accent), ["ThemeRuleSelectedHover"] = Brush(p.SelectedStrong),
            ["ThemeTagBackground"] = Brush(p.Selected), ["ThemeTagBorder"] = Brush(p.BorderStrong), ["ThemeRouteBackground"] = BrushColor(Blend(Parse(p.Selected), Parse(p.Surface), 0.4)),
            ["ThemeRouteBorder"] = Brush(p.Accent), ["ThemeDirectBackground"] = BrushColor(Blend(Parse(p.Secondary), Parse(p.Surface), 0.82)),
            ["ThemeDirectBorder"] = BrushColor(Blend(Parse(p.Secondary), Parse(p.BorderStrong), 0.48)),
            ["ThemeRejectBackground"] = Brush(p.DangerPanel), ["ThemeRejectBorder"] = BrushColor(Blend(Parse(p.Danger), Parse(p.Border), 0.42)),
            ["ThemeFolderBackground"] = Brush(p.Selected), ["ThemeFolderBorder"] = Brush(p.BorderStrong),
            ["ThemeLogPanel"] = BrushColor(WithAlpha(Parse(p.Panel), (byte)(p.Light ? 239 : 209))), ["ThemeLogBackground"] = Brush(p.LogBackground),
            ["ThemeLogText"] = Brush(p.LogText), ["ThemeLogTrack"] = Brush(p.Input), ["ThemeLogThumb"] = BrushColor(Blend(Parse(p.BorderStrong), Parse(p.Accent), 0.24)),
            ["ThemeLogThumbBorder"] = Brush(p.BorderStrong), ["ThemeDialogShade"] = BrushColor(WithAlpha(Parse(p.Overlay()), 80)),
            ["ThemeRouteHint"] = Brush(p.Dim), ["ThemeEmptyIcon"] = Brush(p.BorderStrong), ["ThemeSplitter"] = Brush(p.BorderStrong),
            ["ThemeCheckMark"] = Brush(checkMark), ["ThemeTitleOutline"] = Brush(p.Orbit), ["ThemeOrbitFill"] = Brush(p.OrbitFill),
            ["ThemeShadowColor"] = Parse(p.Shadow), ["ThemeOrbitLine"] = BrushColor(Blend(Parse(p.Orbit), Parse(p.Canvas), 0.52))
        };

        foreach (var resource in resources) Application.Current.Resources[resource.Key] = resource.Value;
        CurrentTheme = theme;
    }

    internal static Brush GetBrush(string key) => Application.Current?.Resources[key] as Brush ?? Brushes.Transparent;

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value)!;

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Blend(Color from, Color to, double ratio) => Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * ratio),
        (byte)(from.R + (to.R - from.R) * ratio),
        (byte)(from.G + (to.G - from.G) * ratio),
        (byte)(from.B + (to.B - from.B) * ratio));

    private static string Overlay(this Palette palette) => palette.Light ? "#550B2019" : "#59060B0A";
}
