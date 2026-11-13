using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace Proppy;

/// <summary>One placed item, as saved.</summary>
[Serializable]
public sealed class SavedProp
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; } // degrees: yaw, pitch, roll
    public Vector3 Scale { get; set; } = Vector3.One;
    public bool Collision { get; set; } = true;
    public Vector3 SitOffset { get; set; } = new(0f, 0.45f, 0f);
    public float SitTurn { get; set; }
    public Vector3 LieOffset { get; set; } = new(0f, 0.55f, 0f);
    public float LieTurn { get; set; }
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>The items placed in each zone (by territory id), kept as they are changed.</summary>
    public Dictionary<uint, List<SavedProp>> Layouts { get; set; } = new();

    /// <summary>A zone's items come back by themselves when you enter it.</summary>
    public bool AutoRestore { get; set; } = true;

    /// <summary>The gizmo on the chosen item (move / turn / size).</summary>
    public bool ShowGizmo { get; set; } = true;
    public int GizmoMode { get; set; } // 0 move, 1 turn, 2 size
    public bool GizmoWorldAxes { get; set; } = true;

    /// <summary>How far in front of you a new item goes (yalms).</summary>
    public float PlaceDistance { get; set; } = 2f;

    public List<string> Favourites { get; set; } = new();
}
