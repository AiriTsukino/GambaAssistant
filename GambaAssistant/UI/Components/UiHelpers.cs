using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace GambaAssistant.UI.Components;

internal static class UiHelpers
{
    public static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(GambaTheme.Gold, title);
        ImGui.Separator();
    }

    public static void Panel(string title, Action draw) => Panel(title, draw, new Vector2(0, 0));

    public static void Panel(string title, Action draw, Vector2 size)
    {
        var available = ImGui.GetContentRegionAvail();
        if (size.X <= 0) size.X = available.X;
        if (size.Y <= 0) size.Y = available.Y;
        size.X = MathF.Max(120f, size.X);
        size.Y = MathF.Max(120f, size.Y);

        const float rounding = 7f;
        const float padding = 8f;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.BeginChild($"{title}##panel_outer", size, false, ImGuiWindowFlags.NoScrollbar);

        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(GambaTheme.PanelBg), rounding);
        drawList.AddRect(min + new Vector2(0.5f, 0.5f), max - new Vector2(0.5f, 0.5f), ImGui.GetColorU32(GambaTheme.Border), rounding, ImDrawFlags.None, 1.0f);

        ImGui.SetCursorPos(new Vector2(padding, padding));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.BeginChild($"{title}##panel_inner", new Vector2(MathF.Max(40f, size.X - padding * 2f), MathF.Max(40f, size.Y - padding * 2f)), false, ImGuiWindowFlags.None);
        Section(title);
        draw();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    public static void Card(string title, Action draw)
    {
        ImGui.Spacing();
        ImGui.TextColored(GambaTheme.Gold, title);
        ImGui.Separator();
        ImGui.Indent(8f);
        draw();
        ImGui.Unindent(8f);
        ImGui.Spacing();
    }

    public static void InfoBox(string title, string body)
    {
        ImGui.Spacing();
        ImGui.TextColored(GambaTheme.Gold, title);
        ImGui.Separator();
        ImGui.Indent(8f);
        ImGui.TextWrapped(body);
        ImGui.Unindent(8f);
        ImGui.Spacing();
    }

    public static bool VerticalNavItem(string label, bool selected, Vector2 size)
    {
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, GambaTheme.PurpleActive);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, GambaTheme.PurpleHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, GambaTheme.PurpleActive);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.10f, 0.17f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.27f, 0.16f, 0.43f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, GambaTheme.PurpleActive);
        }

        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    public static void Help(string text) { ImGui.TextDisabled(WrapTooltip(text, 95)); }
    public static void Tooltip(string text, int maxLineLength = 95) { if (ImGui.IsItemHovered()) ImGui.SetTooltip(WrapTooltip(text, maxLineLength)); }
    public static string Money(long gil) => $"{gil:N0} gil";
    public static bool InputGil(string label, ref long value)
    {
        var text = value == 0 ? string.Empty : value.ToString();
        if (!ImGui.InputText(label, ref text, 32)) return false;
        text = text.Replace(",", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) { value = 0; return true; }
        if (!long.TryParse(text, out var parsed)) return false;
        value = Math.Max(0, parsed);
        return true;
    }
    public static void Badge(string label, Vector4 color) { ImGui.TextColored(color, label); }
    public static bool ConfirmingButton(string label, string popup, string body)
    {
        if (ImGui.Button(label)) ImGui.OpenPopup(popup);
        var confirmed = false;
        if (ImGui.BeginPopupModal(popup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(body);
            ImGui.Spacing();
            if (ImGui.Button("Confirm")) { confirmed = true; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        return confirmed;
    }
    public static bool DisabledAwareButton(string label, bool enabled, string disabledReason)
    {
        if (!enabled) ImGui.BeginDisabled();
        var clicked = ImGui.Button(label);
        if (!enabled) { ImGui.EndDisabled(); Tooltip(disabledReason); return false; }
        return clicked;
    }
    public static string WrapTooltip(string text, int maxLineLength = 95)
    {
        var builder = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var len = 0;
            foreach (var word in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (len > 0 && len + 1 + word.Length > maxLineLength) { builder.AppendLine(); len = 0; }
                if (len > 0) { builder.Append(' '); len++; }
                builder.Append(word); len += word.Length;
            }
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }
}
