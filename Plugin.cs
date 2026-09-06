using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DalaFacing;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dalafacing";

    private readonly record struct MeshTriangle(int A, int B, int C, float Brightness);

    private readonly record struct ProjectedTriangle(
        Vector2 A,
        Vector2 B,
        Vector2 C,
        float Depth,
        float Brightness);

    // Closed triangle mesh with outward-facing winding. Splitting every face into
    // triangles lets us sort the whole volume by camera depth instead of relying on
    // one hard-coded draw order that only works from above or below.
    private static readonly MeshTriangle[] ArrowMesh =
    [
        // Bottom cap: shaft + head.
        new(0, 1, 2, 0.78f),
        new(0, 2, 6, 0.78f),
        new(3, 4, 5, 0.78f),

        // Top cap (reverse winding).
        new(7, 9, 8, 1.00f),
        new(7, 13, 9, 1.00f),
        new(10, 12, 11, 1.00f),

        // Seven vertical sides, two triangles per side.
        new(0, 7, 8, 0.86f), new(0, 8, 1, 0.86f),
        new(1, 8, 9, 0.90f), new(1, 9, 2, 0.90f),
        new(2, 9, 10, 0.94f), new(2, 10, 3, 0.94f),
        new(3, 10, 11, 0.96f), new(3, 11, 4, 0.96f),
        new(4, 11, 12, 0.92f), new(4, 12, 5, 0.92f),
        new(5, 12, 13, 0.88f), new(5, 13, 6, 0.88f),
        new(6, 13, 7, 0.84f), new(6, 7, 0, 0.84f),
    ];

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

        Log.Information("DalaFacing v0.2.3 loaded.");
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

        Span<Vector3> vertices = stackalloc Vector3[14];
        Span<Vector2> screen = stackalloc Vector2[14];
        Span<float> depth = stackalloc float[14];
        var viewProjection = Control.Instance()->ViewProjectionMatrix;

        for (var i = 0; i < worldOutline.Length; i++)
        {
            vertices[i] = worldOutline[i] - up;
            vertices[i + 7] = worldOutline[i] + up;
        }

        for (var i = 0; i < vertices.Length; i++)
        {
            if (!GameGui.WorldToScreen(vertices[i], out screen[i]))
                return;

            // In FFXIV's view-projection matrix, clip-space W increases with camera
            // distance. It gives us the depth value WorldToScreen intentionally hides.
            depth[i] = Vector4.Transform(new Vector4(vertices[i], 1f), viewProjection).W;
        }

        var projected = new List<ProjectedTriangle>(ArrowMesh.Length);
        foreach (var triangle in ArrowMesh)
        {
            projected.Add(new ProjectedTriangle(
                screen[triangle.A],
                screen[triangle.B],
                screen[triangle.C],
                (depth[triangle.A] + depth[triangle.B] + depth[triangle.C]) / 3f,
                triangle.Brightness));
        }

        // Painter's algorithm, but with the real camera depth: rear triangles are
        // painted first and every nearer opaque triangle closes the surface over them.
        projected.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));

        var drawList = ImGui.GetForegroundDrawList();
        foreach (var triangle in projected)
        {
            drawList.AddTriangleFilled(
                triangle.A,
                triangle.B,
                triangle.C,
                ToColor(configuration.ArrowColor, triangle.Brightness));
        }

        if (!configuration.DrawOutline)
            return;

        var outlineColor = ImGui.ColorConvertFloat4ToU32(configuration.OutlineColor);
        var thickness = configuration.OutlineThickness;

        // Outlines follow only camera-visible faces. Internal triangulation edges are
        // deliberately excluded, so the mesh still reads as one clean solid arrow.
        var bottomVisible = IsFrontFacing(screen[0], screen[1], screen[2]);
        var topVisible = IsFrontFacing(screen[7], screen[9], screen[8]);
        Span<bool> sideVisible = stackalloc bool[7];

        for (var i = 0; i < sideVisible.Length; i++)
        {
            var next = (i + 1) % sideVisible.Length;
            sideVisible[i] = IsFrontFacing(screen[i], screen[i + 7], screen[next + 7]);
        }

        for (var i = 0; i < sideVisible.Length; i++)
        {
            var next = (i + 1) % sideVisible.Length;
            var previous = (i + sideVisible.Length - 1) % sideVisible.Length;

            if (bottomVisible || sideVisible[i])
                DrawLine(drawList, screen[i], screen[next], outlineColor, thickness);
            if (topVisible || sideVisible[i])
                DrawLine(drawList, screen[i + 7], screen[next + 7], outlineColor, thickness);
            if (sideVisible[previous] || sideVisible[i])
                DrawLine(drawList, screen[i], screen[i + 7], outlineColor, thickness);
        }
    }

    private static bool IsFrontFacing(Vector2 a, Vector2 b, Vector2 c)
    {
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
        if (!ImGui.Begin("DalaFacing - v0.2.3", ref windowOpen))
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
