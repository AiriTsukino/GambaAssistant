using System.Collections;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Players;
using GambaAssistant.UI.Components;

namespace GambaAssistant.Services;

public sealed class OverlayService
{
    private readonly Configuration config;
    private readonly BlackjackSession session;
    private readonly LogService log;
    private readonly HashSet<string> unresolvedLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> projectionWarningTimes = new(StringComparer.OrdinalIgnoreCase);

    public OverlayService(Configuration config, BlackjackSession session, LogService log)
    {
        this.config = config;
        this.session = session;
        this.log = log;
    }

    public void Draw()
    {
        if (!config.Overlay.Enabled) return;

        var orderedPlayers = session.SessionPlayers
            .OrderBy(p => p.PartySlot)
            .Take(8)
            .ToList();

        if (!config.Overlay.UseDrawnOverhead)
        {
            DrawCombinedOverlayWindow(orderedPlayers);
            return;
        }

        for (var i = 0; i < orderedPlayers.Count; i++)
        {
            var player = orderedPlayers[i];

            if (IsDemoPlayer(player))
            {
                DrawScreenFallbackOverlay(player, i);
                continue;
            }

            if (TryGetPlayerScreenPosition(player, out var drawnScreenPos, out var failureReason))
                DrawDrawListOverlay(player, drawnScreenPos);
            else
            {
                LogProjectionWarningThrottled(player, failureReason);
                DrawScreenFallbackOverlay(player, i);
            }
        }
    }

    private void DrawCombinedOverlayWindow(IReadOnlyList<PlayerSessionState> players)
    {
        var title = "GambaAssistant Table Overlay##ga_combined_table_overlay";

        // OverlayService draws outside the main Dalamud WindowSystem windows, so apply the
        // same local GambaAssistant theme here as a scoped push/pop. This keeps the overlay
        // visually consistent without touching global Dalamud styling.
        GambaTheme.Push();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WithAlpha(GambaTheme.DarkBg, Math.Clamp(config.Overlay.BackgroundOpacity, 0.05f, 1.0f)));
        ImGui.PushStyleColor(ImGuiCol.Border, GambaTheme.Border);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, GambaTheme.PurpleActive);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.16f, 0.08f, 0.25f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);

        ImGui.SetNextWindowPos(new Vector2(35f, 90f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(320f, 0f), new Vector2(620f, 9999f));

        var flags = ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoDocking;

        if (ImGui.Begin(title, flags))
        {
            ImGui.SetWindowFontScale(Math.Clamp(config.Overlay.TextScale, 0.65f, 2.0f));

            ImGui.TextColored(GambaTheme.Gold, "Table Overlay");
            ImGui.SameLine();
            ImGui.TextDisabled(config.Overlay.Compact ? "Compact" : "Detailed");
            ImGui.Separator();

            if (players.Count == 0)
            {
                ImGui.TextDisabled("No table members.");
            }
            else
            {
                for (var i = 0; i < players.Count; i++)
                    DrawThemedOverlayCard(players[i], i);
            }

            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.End();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(4);
        GambaTheme.Pop();
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);

    private void DrawThemedOverlayCard(PlayerSessionState player, int index)
    {
        if (index > 0)
            ImGui.Spacing();

        var lines = BuildOverlayLines(player);
        if (lines.Count == 0)
            return;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, GambaTheme.PanelBg);
        ImGui.PushStyleColor(ImGuiCol.Border, GambaTheme.Border);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(9f, 7f));

        var childHeight = Math.Max(54f, (lines.Count * ImGui.GetTextLineHeightWithSpacing()) + 18f);
        ImGui.BeginChild($"##ga_overlay_card_{index}_{SafeId(player.Identity.ToString())}", new Vector2(0f, childHeight), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        ImGui.TextColored(player.Status == PlayerStatus.Dealer ? GambaTheme.Gold : GambaTheme.Text, lines[0]);
        for (var i = 1; i < lines.Count; i++)
            ImGui.TextColored(i == 1 ? GambaTheme.Text : GambaTheme.MutedText, lines[i]);

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void DrawScreenFallbackOverlay(PlayerSessionState player, int index)
    {
        // Drawn overhead mode should never fail silently. If the client cannot project a
        // character position this frame, draw a small screen-space fallback without the
        // "not attached" text so the dealer still has visible table information.
        var displaySize = ImGui.GetIO().DisplaySize;
        var rowHeight = config.Overlay.Compact ? 74f : 122f;
        var x = Math.Clamp(24f, 0f, Math.Max(0f, displaySize.X - 260f));
        var y = Math.Clamp(90f + index * rowHeight, 0f, Math.Max(0f, displaySize.Y - 80f));
        DrawDrawListOverlay(player, new Vector2(x + 130f, y + rowHeight));
    }

    private void DrawMovableOverlayWindow(string title, Vector2 defaultPos, PlayerSessionState player)
    {
        ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Math.Clamp(config.Overlay.BackgroundOpacity, 0.05f, 1.0f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(220f, 0f), new Vector2(500f, 9999f));

        var flags = ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoDocking;

        if (ImGui.Begin(title, flags))
        {
            ImGui.SetWindowFontScale(Math.Clamp(config.Overlay.TextScale, 0.65f, 2.0f));
            DrawOverlayContents(player);
            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.End();
    }

    private void DrawDrawListOverlay(PlayerSessionState player, Vector2 anchor)
    {
        var lines = BuildOverlayLines(player);
        if (lines.Count == 0) return;

        var drawList = ImGui.GetForegroundDrawList();
        var textScale = Math.Clamp(config.Overlay.TextScale, 0.65f, 2.0f);
        var padding = 7f * textScale;
        var spacing = 2f * textScale;
        var lineHeight = ImGui.GetTextLineHeight() * textScale;
        var maxWidth = 0f;

        foreach (var line in lines)
            maxWidth = MathF.Max(maxWidth, ImGui.CalcTextSize(line).X * textScale);

        var size = new Vector2(maxWidth + padding * 2f, lines.Count * lineHeight + MathF.Max(0, lines.Count - 1) * spacing + padding * 2f);
        var pos = new Vector2(anchor.X - size.X / 2f, anchor.Y - size.Y);
        var rounding = 5f * textScale;
        var opacity = Math.Clamp(config.Overlay.BackgroundOpacity, 0.05f, 1.0f);
        var bg = ToColor(18, 12, 24, (int)(opacity * 255f));
        var border = ToColor(190, 145, 255, 180);
        var text = ToColor(245, 235, 255, 255);
        var muted = ToColor(190, 175, 205, 255);

        drawList.AddRectFilled(pos, pos + size, bg, rounding);
        drawList.AddRect(pos, pos + size, border, rounding, ImDrawFlags.None, 1.0f);

        var cursor = pos + new Vector2(padding, padding);
        for (var i = 0; i < lines.Count; i++)
        {
            var color = i == 0 ? text : muted;
            AddScaledText(drawList, cursor, color, lines[i], textScale);
            cursor.Y += lineHeight + spacing;
        }
    }

    private static void AddScaledText(ImDrawListPtr drawList, Vector2 pos, uint color, string text, float scale)
    {
        // The Dalamud ImGui binding exposes different AddText overloads across API versions.
        // Use the stable overload and keep the rectangle sizing scaled so drawn overlays remain readable.
        drawList.AddText(pos, color, text);
    }

    private bool TryGetPlayerScreenPosition(PlayerSessionState player, out Vector2 screenPos, out string failureReason)
    {
        screenPos = Vector2.Zero;
        failureReason = string.Empty;

        try
        {
            if (!TryGetPlayerWorldPosition(player, out var worldPos, out var source))
            {
                failureReason = "no matching in-world object or party-list position was found";
                return false;
            }

            var localPos = GetLocalPlayerPosition();
            if (localPos.HasValue && config.Overlay.MaxRenderDistance > 0f)
            {
                var distance = Vector3.Distance(localPos.Value, worldPos);
                if (distance > config.Overlay.MaxRenderDistance)
                {
                    failureReason = $"{source} was {distance:0.0} yalms away, beyond the overlay max render distance";
                    return false;
                }
            }

            var offset = CalculateOverheadOffset(player);
            var anchors = new[]
            {
                worldPos + new Vector3(0f, offset, 0f),
                worldPos + new Vector3(0f, 0f, offset),
            };

            foreach (var anchor in anchors)
            {
                var projected = DalamudServices.GameGui.WorldToScreen(anchor, out var projectedPos);
                if (projected && IsReasonableScreenPosition(projectedPos))
                {
                    screenPos = projectedPos;
                    return true;
                }

                if (IsReasonableScreenPosition(projectedPos))
                {
                    screenPos = projectedPos;
                    return true;
                }
            }

            failureReason = $"{source} position was found, but WorldToScreen did not return a usable screen coordinate";
            return false;
        }
        catch (Exception ex)
        {
            failureReason = $"projection failed: {ex.Message}";
            return false;
        }
    }

    private bool TryGetHeadBoneWorldPosition(PlayerSessionState player, out Vector3 headWorldPos, out string source)
    {
        headWorldPos = Vector3.Zero;
        source = string.Empty;

        var gameObject = ResolveGameObject(player);
        if (gameObject == null)
            return false;

        if (TryReadHeadAnchorFromObjectGraph(gameObject, gameObject.Position, out headWorldPos, new HashSet<object>(ReferenceEqualityComparer.Instance), 0))
        {
            source = "head/bone anchor";
            return true;
        }

        return false;
    }

    private static bool TryReadHeadAnchorFromObjectGraph(object? obj, Vector3 rootPosition, out Vector3 headWorldPos, HashSet<object> visited, int depth)
    {
        headWorldPos = Vector3.Zero;

        if (obj == null || depth > 4)
            return false;

        var type = obj.GetType();
        if (!type.IsValueType && !visited.Add(obj))
            return false;

        // Prefer direct head/nameplate-like properties when a wrapper exposes them.
        foreach (var propertyName in new[] { "HeadWorldPosition", "HeadPosition", "NamePlatePosition", "NameplatePosition" })
        {
            if (TryGetPropertyValue(obj, propertyName, out var value)
                && TryConvertPotentialHeadVector(value, rootPosition, out headWorldPos))
                return true;
        }

        // Try common bone-position method shapes without depending on a specific Dalamud/client-structs version.
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!method.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase)
                || !method.Name.Contains("Position", StringComparison.OrdinalIgnoreCase))
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                continue;

            foreach (var arg in GetHeadBoneMethodArguments(parameters[0].ParameterType))
            {
                try
                {
                    var result = method.Invoke(obj, new[] { arg });
                    if (TryConvertPotentialHeadVector(result, rootPosition, out headWorldPos))
                        return true;
                }
                catch
                {
                    // Different game/API builds expose different method contracts; ignore failed probes.
                }
            }
        }

        // Search named bone collections when they are exposed by wrappers/modding helpers.
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                continue;

            var promisingName = IsPromisingNestedAnchorName(prop.Name);
            if (!promisingName && !prop.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase))
                continue;

            object? value;
            try { value = prop.GetValue(obj); }
            catch { continue; }

            if (value == null || value is string)
                continue;

            if (prop.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase) && value is IEnumerable enumerable)
            {
                if (TryReadHeadFromBoneEnumerable(enumerable, rootPosition, out headWorldPos))
                    return true;
            }

            if (promisingName && IsPromisingNestedAnchorObject(prop.Name, value)
                && TryReadHeadAnchorFromObjectGraph(value, rootPosition, out headWorldPos, visited, depth + 1))
                return true;
        }

        return false;
    }

    private static IEnumerable<object?> GetHeadBoneMethodArguments(Type parameterType)
    {
        if (parameterType == typeof(string))
        {
            foreach (var name in new[] { "Head", "head", "j_kao", "j_head", "n_head", "kao" })
                yield return name;
            yield break;
        }

        if (parameterType == typeof(int))
        {
            foreach (var id in new[] { 10, 11, 12, 13, 14, 15, 16, 17, 18 })
                yield return id;
            yield break;
        }

        if (parameterType == typeof(uint))
        {
            foreach (var id in new uint[] { 10, 11, 12, 13, 14, 15, 16, 17, 18 })
                yield return id;
            yield break;
        }

        if (parameterType.IsEnum)
        {
            foreach (var value in Enum.GetValues(parameterType))
            {
                var name = Enum.GetName(parameterType, value) ?? string.Empty;
                if (name.Contains("Head", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Kao", StringComparison.OrdinalIgnoreCase))
                    yield return value;
            }
        }
    }

    private static bool TryReadHeadFromBoneEnumerable(IEnumerable enumerable, Vector3 rootPosition, out Vector3 headWorldPos)
    {
        headWorldPos = Vector3.Zero;

        foreach (var bone in enumerable)
        {
            if (bone == null)
                continue;

            var name = ReadObjectName(bone);
            if (!IsHeadBoneName(name))
                continue;

            foreach (var propertyName in new[] { "WorldPosition", "Position", "Translation", "Transform", "Matrix" })
            {
                if (TryGetPropertyValue(bone, propertyName, out var value)
                    && TryConvertPotentialHeadVector(value, rootPosition, out headWorldPos))
                    return true;
            }
        }

        return false;
    }

    private static bool IsHeadBoneName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = NormalizeName(name);
        return normalized.Contains("head", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("kao", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadObjectName(object obj)
    {
        foreach (var propertyName in new[] { "Name", "BoneName", "Id", "Identifier" })
        {
            if (TryGetPropertyValue(obj, propertyName, out var value) && value != null)
                return value.ToString();
        }

        return null;
    }

    private static bool IsPromisingNestedAnchorName(string name) =>
        name.Contains("Skeleton", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Draw", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Model", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Bone", StringComparison.OrdinalIgnoreCase);

    private static bool IsPromisingNestedAnchorObject(string propertyName, object value)
    {
        if (value is IEnumerable || value is string)
            return false;

        var typeName = value.GetType().Name;
        return IsPromisingNestedAnchorName(propertyName)
            || typeName.Contains("Skeleton", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Draw", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Model", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Bone", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetPropertyValue(object obj, string propertyName, out object? value)
    {
        value = null;
        try
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || prop.GetIndexParameters().Length != 0 || !prop.CanRead)
                return false;

            value = prop.GetValue(obj);
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertPotentialHeadVector(object? value, Vector3 rootPosition, out Vector3 worldPosition)
    {
        worldPosition = Vector3.Zero;

        if (!TryConvertToVector3(value, out var raw))
            return false;

        if (!IsFinite(raw) || raw == Vector3.Zero)
            return false;

        // Some wrappers return world-space bone positions; others return a local offset from the actor root.
        var asWorldDistance = Vector3.Distance(raw, rootPosition);
        if (asWorldDistance is > 0.15f and < 5.5f)
        {
            worldPosition = raw;
            return true;
        }

        if (raw.Length() < 5.5f)
        {
            var candidate = rootPosition + raw;
            var localDistance = Vector3.Distance(candidate, rootPosition);
            if (localDistance is > 0.15f and < 5.5f)
            {
                worldPosition = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryConvertToVector3(object? value, out Vector3 vector)
    {
        vector = Vector3.Zero;
        if (value == null)
            return false;

        if (value is Vector3 direct)
        {
            vector = direct;
            return true;
        }

        // Matrix-like objects: use translation fields/properties when present.
        foreach (var name in new[] { "Translation", "Position" })
        {
            if (TryGetPropertyValue(value, name, out var nested) && TryConvertToVector3(nested, out vector))
                return true;
        }

        try
        {
            var type = value.GetType();
            var x = ReadFloatMember(value, type, "X");
            var y = ReadFloatMember(value, type, "Y");
            var z = ReadFloatMember(value, type, "Z");
            if (x.HasValue && y.HasValue && z.HasValue)
            {
                vector = new Vector3(x.Value, y.Value, z.Value);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static float? ReadFloatMember(object obj, Type type, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null)
            return ConvertNumberToFloat(prop.GetValue(obj));

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
            return ConvertNumberToFloat(field.GetValue(obj));

        return null;
    }

    private static float? ConvertNumberToFloat(object? value)
    {
        return value switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            uint u => u,
            short s => s,
            ushort us => us,
            byte b => b,
            sbyte sb => sb,
            _ => null,
        };
    }

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.X) && !float.IsInfinity(value.X)
        && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y)
        && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);

    private float GetPlayerSpecificOffset(PlayerSessionState player)
    {
        var key = player.Identity.ToString();
        return config.Overlay.PlayerHeightOffsets.TryGetValue(key, out var playerOffset) ? playerOffset : 0f;
    }

    private float CalculateOverheadOffset(PlayerSessionState player)
    {
        var offset = config.Overlay.VerticalOffset + GetPlayerSpecificOffset(player);
        return Math.Clamp(offset, -1.0f, 8.0f);
    }

    private static float? TryReadObjectFloat(object? obj, string propertyName)
    {
        if (obj == null)
            return null;

        try
        {
            var prop = obj.GetType().GetProperty(propertyName);
            var value = prop?.GetValue(obj);
            return value switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                uint u => u,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsReasonableScreenPosition(Vector2 pos)
    {
        if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsInfinity(pos.X) || float.IsInfinity(pos.Y))
            return false;

        var display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0f || display.Y <= 0f)
            return false;

        // Allow a little padding outside the viewport so labels do not blink off at edges.
        return pos.X >= -100f && pos.Y >= -100f && pos.X <= display.X + 100f && pos.Y <= display.Y + 100f;
    }

    private static Vector3? GetLocalPlayerPosition()
    {
        var local = DalamudServices.ObjectTable.LocalPlayer;
        if (local != null)
            return local.Position;

        foreach (var member in DalamudServices.PartyList)
        {
            var name = member.Name.TextValue.Trim();
            if (string.Equals(NormalizeName(name), NormalizeName(DalamudServices.PlayerState.CharacterName), StringComparison.OrdinalIgnoreCase))
                return member.Position;
        }

        return null;
    }

    private static bool TryGetPlayerWorldPosition(PlayerSessionState player, out Vector3 position, out string source)
    {
        position = Vector3.Zero;
        source = string.Empty;

        var gameObject = ResolveGameObject(player);
        if (gameObject != null)
        {
            position = gameObject.Position;
            source = "object table";
            return true;
        }

        var partyMemberPosition = ResolvePartyMemberPosition(player);
        if (partyMemberPosition.HasValue)
        {
            position = partyMemberPosition.Value;
            source = "party list";
            return true;
        }

        return false;
    }

    private static IGameObject? ResolveGameObject(PlayerSessionState player)
    {
        if (player.Status == PlayerStatus.Dealer)
        {
            var local = DalamudServices.ObjectTable.LocalPlayer;
            if (local != null)
                return local;
        }

        var fromParty = ResolvePartyMemberGameObject(player);
        if (fromParty != null)
            return fromParty;

        var targetName = NormalizeName(player.Identity.Name);
        if (string.IsNullOrWhiteSpace(targetName))
            return null;

        foreach (var obj in DalamudServices.ObjectTable)
        {
            if (obj == null)
                continue;

            var objectName = obj.Name.TextValue.Trim();
            if (string.IsNullOrWhiteSpace(objectName))
                continue;

            if (string.Equals(NormalizeName(objectName), targetName, StringComparison.OrdinalIgnoreCase))
                return obj;
        }

        return null;
    }

    private static IGameObject? ResolvePartyMemberGameObject(PlayerSessionState player)
    {
        foreach (var member in DalamudServices.PartyList)
        {
            if (!IsMatchingPartyMember(member.Name.TextValue.Trim(), member.World.ValueNullable?.Name.ExtractText() ?? string.Empty, player))
                continue;

            var gameObject = member.GameObject;
            if (gameObject != null)
                return gameObject;

            if (member.EntityId != 0)
            {
                var byEntity = DalamudServices.ObjectTable.SearchByEntityId(member.EntityId);
                if (byEntity != null)
                    return byEntity;
            }
        }

        return null;
    }

    private static Vector3? ResolvePartyMemberPosition(PlayerSessionState player)
    {
        foreach (var member in DalamudServices.PartyList)
        {
            if (!IsMatchingPartyMember(member.Name.TextValue.Trim(), member.World.ValueNullable?.Name.ExtractText() ?? string.Empty, player))
                continue;

            return member.Position;
        }

        return null;
    }

    private static bool IsMatchingPartyMember(string memberName, string memberWorld, PlayerSessionState player)
    {
        var targetName = NormalizeName(player.Identity.Name);
        if (string.IsNullOrWhiteSpace(targetName))
            return false;

        if (!string.Equals(NormalizeName(memberName), targetName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Some party-list entries do not expose a useful world in all contexts. Name match is enough when
        // the world is missing; otherwise prefer Name@World so cross-world parties stay safe.
        return string.IsNullOrWhiteSpace(player.Identity.World)
            || string.IsNullOrWhiteSpace(memberWorld)
            || string.Equals(NormalizeName(memberWorld), NormalizeName(player.Identity.World), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeName(string name)
    {
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 64)];
        var count = 0;
        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;
            if (count >= buffer.Length)
                break;
            buffer[count++] = char.ToLowerInvariant(ch);
        }
        return new string(buffer[..count]);
    }

    private void LogProjectionWarningThrottled(PlayerSessionState player, string reason)
    {
        var key = player.Identity.ToString();

        // Avoid frame-by-frame warning spam. Log immediately the first time, then at most once
        // every 30 seconds per player while continuing to draw a visible fallback overlay.
        if (projectionWarningTimes.TryGetValue(key, out var last) && (DateTime.UtcNow - last).TotalSeconds < 30)
            return;

        projectionWarningTimes[key] = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(reason))
            reason = "the character could not be projected this frame";

        log.Add(LogCategory.Warnings, $"Overlay could not draw overhead for {player.DisplayName}: {reason}. Showing a screen-space fallback overlay.");
    }

    private static bool IsDemoPlayer(PlayerSessionState player) =>
        string.Equals(player.Identity.World, "Demo", StringComparison.OrdinalIgnoreCase);

    private void DrawOverlayContents(PlayerSessionState player)
    {
        foreach (var line in BuildOverlayLines(player))
            ImGui.TextUnformatted(line);
    }

    private List<string> BuildOverlayLines(PlayerSessionState player)
    {
        var lines = new List<string>();

        if (player.Status is PlayerStatus.SpectatorStaff or PlayerStatus.SittingOut)
        {
            lines.Add(player.DisplayName);
            lines.Add(player.Status == PlayerStatus.SpectatorStaff ? "Spectator" : "Sitting Out");
            return lines;
        }

        if (player.Status == PlayerStatus.Dealer)
        {
            lines.Add("Dealer");
            lines.Add($"Hand: {BlankIfEmpty(session.Round.DealerHand.CardText)}");
            if (!config.Overlay.Compact)
                lines.Add($"Status: {session.Round.Phase}");
            return lines;
        }

        lines.Add(player.DisplayName);

        if (config.Overlay.Compact)
        {
            var hand = player.Hands.FirstOrDefault();
            lines.Add($"Hand: {BlankIfEmpty(hand?.CardText)}");
            lines.Add($"Bet: {player.Bank.ActiveBet:N0} | Bank: {player.Bank.Available:N0}");
            return lines;
        }

        if (player.Hands.Count == 0)
        {
            lines.Add("Hand: -");
        }
        else
        {
            foreach (var hand in player.Hands)
            {
                lines.Add($"Hand {hand.HandNumber}: {BlankIfEmpty(hand.CardText)} = {hand.TotalText}");
                lines.Add($"Bet: {hand.Bet:N0} gil");
            }
        }

        lines.Add($"Bank: {player.Bank.Available:N0} gil");
        lines.Add($"Status: {player.Status}");
        return lines;
    }

    private static string BlankIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? "-" : text;

    private static string SafeId(string text) => NormalizeName(text);

    private static uint ToColor(int r, int g, int b, int a)
    {
        return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
    }
}
