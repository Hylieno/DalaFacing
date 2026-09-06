using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DalaFacing;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dalafacing";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static ITargetManager TargetManager { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private Configuration configuration;
    private bool windowOpen;
    private ulong? selectedObjectId;
    private string selectedObjectName = "Aucun";

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        SanitizeConfiguration();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Ouvre DalaFacing. Sous-commandes : select, clear.",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;

        Log.Information("DalaFacing v0.2.2 loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        CommandManager.RemoveHandler(CommandName);
    }

    private void OpenMainUi() => windowOpen = true;

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "select":
            case "set":
                SelectCurrentTarget();
                break;
            case "clear":
            case "remove":
                ClearSelection();
                break;
            default:
                windowOpen = true;
                break;
        }
    }

    private void SelectCurrentTarget()
    {
        var target = TargetManager.Target;
        if (target is not IBattleNpc)
        {
            ChatGui.PrintError("[DalaFacing] Cible un ennemi ou un PNJ de combat avant de l'attacher.");
            return;
        }

        selectedObjectId = target.GameObjectId;
        selectedObjectName = target.Name.ToString();
        ChatGui.Print($"[DalaFacing] Flèche attachée à {selectedObjectName}.");
    }

    private void ClearSelection()
    {
        selectedObjectId = null;
        selectedObjectName = "Aucun";
        ChatGui.Print("[DalaFacing] Sélection retirée.");
    }

    private IGameObject? GetSelectedObject()
        => selectedObjectId is { } id ? ObjectTable.SearchById(id) : null;

    private void Draw()
    {
        DrawFacingArrow();
        DrawConfigWindow();
    }

    private void DrawFacingArrow()
    {
        var actor = GetSelectedObject();
        if (actor is null)
            return;

        // FFXIV's actor yaw uses +Z as its zero-direction.
        var forward = new Vector3(MathF.Sin(actor.Rotation), 0f, MathF.Cos(actor.Rotation));
        var right = new Vector3(forward.Z, 0f, -forward.X);
        var center = actor.Position + new Vector3(0f, configuration.HeightAboveGround, 0f);
        var up = Vector3.UnitY * (configuration.ArrowThickness * 0.5f);

        var halfLength = configuration.ArrowLength * 0.5f;
        var shaftHalfWidth = configuration.ArrowWidth * 0.24f;
        var headHalfWidth = configuration.ArrowWidth * 0.5f;

        var tailCenter = center - forward * halfLength;
        var neckCenter = center + forward * (configuration.ArrowLength * 0.16f);
        var tip = center + forward * halfLength;

        // A seven-vertex arrow silhouette, extruded vertically into a real 3D prism.
        Span<Vector3> worldOutline = stackalloc Vector3[7]
        {
            tailCenter - right * shaftHalfWidth,
            tailCenter + right * shaftHalfWidth,
            neckCenter + right * shaftHalfWidth,
            neckCenter + right * headHalfWidth,
            tip,
            neckCenter - right * headHalfWidth,
            neckCenter - right * shaftHalfWidth,
        };

        Span<Vector2> bottom = stackalloc Vector2[7];
        Span<Vector2> top = stackalloc Vector2[7];
        for (var i = 0; i < worldOutline.Length; i++)
        {
            if (!GameGui.WorldToScreen(worldOutline[i] - up, out bottom[i]) ||
                !GameGui.WorldToScreen(worldOutline[i] + up, out top[i]))
                return;
        }

        var drawList = ImGui.GetForegroundDrawList();
        var topColor = ToColor(configuration.ArrowColor, 1.0f);
        var bottomColor = ToColor(configuration.ArrowColor, 0.92f);

        // ImGui has no depth buffer. Cull every polygon facing away from the camera
        // from its projected winding so hidden faces cannot bleed through visible ones.
        var bottomVisible = IsFrontFacing(bottom[0], bottom[1], bottom[2]);
        var topVisible = IsFrontFacing(top[0], top[6], top[2]);

        ReadOnlySpan<float> sideBrightness = stackalloc float[7]
        {
            0.78f, 0.84f, 0.90f, 0.94f, 0.88f, 0.82f, 0.76f,
        };

        Span<bool> sideVisible = stackalloc bool[7];
        for (var i = 0; i < worldOutline.Length; i++)
        {
            var next = (i + 1) % worldOutline.Length;
            sideVisible[i] = IsFrontFacing(bottom[i], top[i], top[next]);
            if (sideVisible[i])
            {
                drawList.AddQuadFilled(bottom[i], top[i], top[next], bottom[next],
                    ToColor(configuration.ArrowColor, sideBrightness[i]));
            }
        }

        // ImGui has no depth buffer, so the camera-facing cap must be painted
        // after every side. Its opaque fill masks all geometry behind it.
        if (topVisible)
            DrawTopFace(drawList, top, topColor);
        else if (bottomVisible)
            DrawBottomFace(drawList, bottom, bottomColor);

        if (!configuration.DrawOutline)
            return;

        var outlineColor = ImGui.ColorConvertFloat4ToU32(configuration.OutlineColor);
        var thickness = configuration.OutlineThickness;

        for (var i = 0; i < top.Length; i++)
        {
            var next = (i + 1) % top.Length;
            var previous = (i + top.Length - 1) % top.Length;

            if (bottomVisible || sideVisible[i])
                DrawLine(drawList, bottom[i], bottom[next], outlineColor, thickness);
            if (topVisible || sideVisible[i])
                DrawLine(drawList, top[i], top[next], outlineColor, thickness);
            if (sideVisible[previous] || sideVisible[i])
                DrawLine(drawList, bottom[i], top[i], outlineColor, thickness);
        }
    }

    private static void DrawBottomFace(ImDrawListPtr drawList, ReadOnlySpan<Vector2> points, uint color)
    {
        drawList.AddQuadFilled(points[0], points[1], points[2], points[6], color);
        drawList.AddTriangleFilled(points[3], points[4], points[5], color);
    }

    private static void DrawTopFace(ImDrawListPtr drawList, ReadOnlySpan<Vector2> points, uint color)
    {
        // Reverse winding from the bottom face: its outward normal points upward.
        drawList.AddQuadFilled(points[0], points[6], points[2], points[1], color);
        drawList.AddTriangleFilled(points[3], points[5], points[4], color);
    }

    private static bool IsFrontFacing(Vector2 a, Vector2 b, Vector2 c)
    {
        // Screen Y grows downward, so a positive signed area is the clockwise,
        // front-facing winding produced by Dalamud's WorldToScreen projection.
        var signedArea = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return signedArea > 0.01f;
    }

    private static uint ToColor(Vector4 color, float brightness)
    {
        var shaded = new Vector4(
            color.X * brightness,
            color.Y * brightness,
            color.Z * brightness,
            1.0f);
        return ImGui.ColorConvertFloat4ToU32(shaded);
    }

    private static void DrawLine(ImDrawListPtr drawList, Vector2 a, Vector2 b, uint color, float thickness)
        => drawList.AddLine(a, b, color, thickness);

    private void DrawConfigWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(490f, 430f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("DalaFacing - v0.2.2", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        var liveObject = GetSelectedObject();
        ImGui.TextWrapped($"Mob attaché : {selectedObjectName}");
        ImGui.TextDisabled(selectedObjectId is null
            ? "Aucune flèche active."
            : liveObject is null
                ? "Le mob n'est plus présent dans l'instance."
                : $"Actif · ObjectId 0x{selectedObjectId.Value:X}");

        if (ImGui.Button("Attacher à ma cible actuelle", new Vector2(250f, 30f)))
            SelectCurrentTarget();

        ImGui.SameLine();
        if (ImGui.Button("Retirer", new Vector2(100f, 30f)))
            ClearSelection();

        ImGui.Separator();

        var height = configuration.HeightAboveGround;
        if (ImGui.SliderFloat("Hauteur depuis le sol", ref height, 0f, 30f, "%.1f y"))
        {
            configuration.HeightAboveGround = height;
            SaveConfiguration();
        }

        var length = configuration.ArrowLength;
        if (ImGui.SliderFloat("Longueur", ref length, 1f, 15f, "%.1f y"))
        {
            configuration.ArrowLength = length;
            SaveConfiguration();
        }

        var width = configuration.ArrowWidth;
        if (ImGui.SliderFloat("Largeur", ref width, 0.3f, 6f, "%.1f y"))
        {
            configuration.ArrowWidth = width;
            SaveConfiguration();
        }

        var arrowThickness = configuration.ArrowThickness;
        if (ImGui.SliderFloat("Épaisseur 3D", ref arrowThickness, 0.1f, 4f, "%.1f y"))
        {
            configuration.ArrowThickness = arrowThickness;
            SaveConfiguration();
        }

        var arrowColor = configuration.ArrowColor;
        if (ImGui.ColorEdit4("Couleur de la flèche", ref arrowColor,
                ImGuiColorEditFlags.NoAlpha))
        {
            configuration.ArrowColor = arrowColor;
            SaveConfiguration();
        }

        var drawOutline = configuration.DrawOutline;
        if (ImGui.Checkbox("Contour", ref drawOutline))
        {
            configuration.DrawOutline = drawOutline;
            SaveConfiguration();
        }

        if (configuration.DrawOutline)
        {
            var outlineColor = configuration.OutlineColor;
            if (ImGui.ColorEdit4("Couleur du contour", ref outlineColor,
                    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
            {
                configuration.OutlineColor = outlineColor;
                SaveConfiguration();
            }

            var outlineThickness = configuration.OutlineThickness;
            if (ImGui.SliderFloat("Épaisseur du contour", ref outlineThickness, 1f, 8f, "%.1f px"))
            {
                configuration.OutlineThickness = outlineThickness;
                SaveConfiguration();
            }
        }

        ImGui.Separator();
        ImGui.TextWrapped("Commandes : /dalafacing select · /dalafacing clear · /dalafacing");
        ImGui.TextDisabled("La sélection reste attachée à ce mob lorsque tu changes de cible.");

        ImGui.End();
    }

    private void SanitizeConfiguration()
    {
        configuration.HeightAboveGround = Math.Clamp(configuration.HeightAboveGround, 0f, 30f);
        configuration.ArrowLength = Math.Clamp(configuration.ArrowLength, 1f, 15f);
        configuration.ArrowWidth = Math.Clamp(configuration.ArrowWidth, 0.3f, 6f);
        configuration.ArrowThickness = Math.Clamp(configuration.ArrowThickness, 0.1f, 4f);
        configuration.OutlineThickness = Math.Clamp(configuration.OutlineThickness, 1f, 8f);
        configuration.ArrowColor = Vector4.Clamp(configuration.ArrowColor, Vector4.Zero, Vector4.One);
        configuration.ArrowColor = new Vector4(
            configuration.ArrowColor.X,
            configuration.ArrowColor.Y,
            configuration.ArrowColor.Z,
            1f);
        configuration.OutlineColor = Vector4.Clamp(configuration.OutlineColor, Vector4.Zero, Vector4.One);
    }

    private void SaveConfiguration()
    {
        SanitizeConfiguration();
        PluginInterface.SavePluginConfig(configuration);
    }
}
