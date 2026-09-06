using System.Numerics;
using Dalamud.Configuration;

namespace DalaFacing;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // World-space dimensions in yalms. Position.Y is the object's ground origin.
    public float HeightAboveGround { get; set; } = 4.0f;
    public float ArrowLength { get; set; } = 5.0f;
    public float ArrowWidth { get; set; } = 1.4f;
    public float ArrowThickness { get; set; } = 0.8f;

    public Vector4 ArrowColor { get; set; } = new(1.0f, 0.16f, 0.08f, 0.90f);
    public bool DrawOutline { get; set; } = true;
    public Vector4 OutlineColor { get; set; } = new(0.05f, 0.05f, 0.05f, 1.0f);
    public float OutlineThickness { get; set; } = 3.0f;
}
