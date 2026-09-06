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

        Log.Information("DalaFacing v0.2.0 loaded.");
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
        var bottomColor = ToColor(configuration.ArrowColor, 0.32f);

        // Bottom first, then the vertical sides, then the top. This painter order is
        // tuned for FFXIV's normal over-the-shoulder camera and keeps the volume clear.
        DrawArrowFace(drawList, bottom, bottomColor);

        ReadOnlySpan<float> sideBrightness = stackalloc float[7]
        {
            0.42f, 0.58f, 0.72f, 0.82f, 0.62f, 0.48f, 0.36f,
        };

        for (var i = 0; i < worldOutline.Length; i++)
        {
            var next = (i + 1) % worldOutline.Length;
            drawList.AddQuadFilled(bottom[i], bottom[next], top[next], top[i],
                ToColor(configuration.ArrowColor, sideBrightness[i]));
        }

        DrawArrowFace(drawList, top, topColor);

        if (!configuration.DrawOutline)
            return;

        var outlineColor = ImGui.ColorConvertFloat4ToU32(configuration.OutlineColor);
        var thickness = configuration.OutlineThickness;

        for (var i = 0; i < top.Length; i++)
        {
            var next = (i + 1) % top.Length;
            DrawLine(drawList, bottom[i], bottom[next], outlineColor, thickness);
            DrawLine(drawList, top[i], top[next], outlineColor, thickness);
            DrawLine(drawList, bottom[i], top[i], outlineColor, thickness);
        }
    }

    private static void DrawArrowFace(ImDrawListPtr drawList, ReadOnlySpan<Vector2> points, uint color)
    {
        // Shaft rectangle + triangular head. The overlap is intentional and seamless.
        drawList.AddQuadFilled(points[0], points[1], points[2], points[6], color);
        drawList.AddTriangleFilled(points[3], points[4], points[5], color);
    }

    private static uint ToColor(Vector4 color, float brightness)
    {
        var shaded = new Vector4(
            color.X * brightness,
            color.Y * brightness,
            color.Z * brightness,
            color.W);
        return ImGui.ColorConvertFloat4ToU32(shaded);
    }

    private static void DrawLine(ImDrawListPtr drawList, Vector2 a, Vector2 b, uint color, float thickness)
        => drawList.AddLine(a, b, color, thickness);

    private void DrawConfigWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(490f, 430f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("DalaFacing - v0.2.0", ref windowOpen))
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
                ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
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
        configuration.OutlineColor = Vector4.Clamp(configuration.OutlineColor, Vector4.Zero, Vector4.One);
    }

    private void SaveConfiguration()
    {
        SanitizeConfiguration();
        PluginInterface.SavePluginConfig(configuration);
    }
}
