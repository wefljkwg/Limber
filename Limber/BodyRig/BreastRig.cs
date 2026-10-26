using System;
using System.Numerics;

namespace Limber.BodyRig;

/// <summary>
/// Placement of a part's rig bones, in units of the distance between the left and right bones. For a lattice: the
/// box around the part (Size = width and height, Forward = where its back face is, Depth = how deep it is). For the
/// old surface grid: the grid's size, centre and bulge.
/// </summary>
[Serializable]
public sealed class BreastRigShape
{
    public float Size { get; set; } = 0.80f;    // width and height of the grid
    public float Forward { get; set; } = 0.20f; // grid centre in front of the breast bone
    public float Up { get; set; } = 0.00f;      // grid centre above the breast bone
    public float Out { get; set; } = 0.00f;     // grid centre further out to the side
    public float Depth { get; set; } = 0.30f;   // how far the middle of the grid bulges forward
    public float Height { get; set; }           // a lattice's height when it isn't Size (0 = Size)
    // Where the nipple bone sits (the middle of the areola), from the part's own bone, in separations (x outward).
    // Found on the worn models when the box is fitted; until then the middle of the box's front.
    public bool HasNipple { get; set; }
    public float NippleOut { get; set; }
    public float NippleUp { get; set; }
    public float NippleForward { get; set; }

    public BreastRigShape Clone() => (BreastRigShape)MemberwiseClone();
}

/// <summary>
/// One rigged body part: a grid of extra bones per side (Rows x Columns), children of the part's own bone, spread
/// over it like a shell. The mesh gets weighted to them (Blender), and Limber's soft body moves them.
/// </summary>
public sealed class RigPart
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Prefix { get; init; }     // bone names: Prefix + "l_00" ...
    public required int Rows { get; init; }
    public required int Columns { get; init; }
    public required string[] Parents { get; init; }  // candidate parent bones, "{side}" = l / r, first found wins
    public required Func<BreastRigShape> DefaultShape { get; init; }
    /// <summary>
    /// A lattice: a box of bones through the whole part, <see cref="Layers"/> layers of Rows x Columns deep (front
    /// to back, the back face itself being the part's own bone), instead of one surface grid. Each vertex is weighted
    /// to the corners of the box cell it's in, so moving the bones bends the whole volume smoothly, like a lattice
    /// deformer.
    /// </summary>
    public bool Lattice { get; init; }
    public int Layers { get; init; } = 1;
    /// <summary>The lattice's back face is bones too (not just the part's own bone), so the whole box can be resized.</summary>
    public bool BackLayerBones { get; init; }
    /// <summary>Shape sliders grow it from the top (where it hangs) and from the middle between the two sides.</summary>
    public bool GrowsFromTop { get; init; }
    /// <summary>Old surface grid kept in the skeleton (so models weighted to it still load), but not moved.</summary>
    public bool Legacy { get; init; }
    /// <summary>A nipple bone per side (moved with the lattice, sized by the nipple shapes).</summary>
    public bool Nipples { get; init; }
    /// <summary>Radius of what's weighted to the nipple bone, in separations.</summary>
    public float NippleRadius { get; init; } = 0.6f;
    /// <summary>It sticks out backward (the butt): the box grows back from the part's bone instead of forward.</summary>
    public bool Backward { get; init; }
    /// <summary>A body shape grid (only moved by the Body Shape tab, no physics): left out of a mesh with no room.</summary>
    public bool Shape { get; init; }
    /// <summary>One grid across the middle of the body (on a bone with no sides), instead of one per side.</summary>
    public bool Single { get; init; }
    /// <summary>Lined up along a limb, from the part's bone toward this one ("{side}" = l / r), instead of the body's
    /// axes: rows run down the limb, columns across it (inner to outer), layers back to front.</summary>
    public string? Child { get; init; }
    /// <summary>Its height is fitted on its own (a limb is long and thin), not the same as its width.</summary>
    public bool OwnHeight { get; init; }
    /// <summary>Left-right distance to assume when the skeleton's own can't be measured.</summary>
    public float FallbackSeparation { get; init; } = 0.17f;

    public int PerLayer => Rows * Columns;
    public int PerSide => Layers * Rows * Columns;

    /// <summary>Bone name, e.g. lmb_mune_l_00 (row by row, top row first, inner column first).</summary>
    public string BoneName(bool left, int index) => $"{Prefix}{(left ? 'l' : 'r')}_{index:00}";

    public string NippleName(bool left) => $"{Prefix}{(left ? 'l' : 'r')}_nip";

    public bool IsMine(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The nipple bone's spot from the part's bone (model axes, x outward), for <paramref name="separation"/>.</summary>
    public Vector3 NippleOffset(BreastRigShape shape, float separation)
    {
        if (shape.HasNipple)
            return new Vector3(shape.NippleOut, shape.NippleUp, shape.NippleForward) * separation;
        // The middle of the front face.
        return Offset((Layers - 1) * PerLayer + (Rows / 2) * Columns + Columns / 2, shape, separation);
    }

    public string? ParentBone(Func<string, bool> has, bool left)
    {
        foreach (var pattern in Parents)
        {
            var name = pattern.Replace("{side}", left ? "l" : "r");
            if (has(name))
                return name;
        }
        return null;
    }

    /// <summary>
    /// Where grid point <paramref name="index"/> sits relative to the part's bone, in the character's model axes
    /// (x: outward from the body's middle, y: up, z: forward), for a left-right bone separation of
    /// <paramref name="separation"/>.
    /// </summary>
    public Vector3 Offset(int index, BreastRigShape shape, float separation)
    {
        if (Lattice)
        {
            // Nodes on the box's sides; layers from just in front of the back face (the part's bone) to the front.
            int layer = index / PerLayer, rest = index % PerLayer;
            int r = rest / Columns, c = rest % Columns;
            float lu = Columns > 1 ? c / (float)(Columns - 1) - 0.5f : 0f;
            float lv = Rows > 1 ? 0.5f - r / (float)(Rows - 1) : 0f;
            float lw = BackLayerBones ? layer / (float)Math.Max(1, Layers - 1) : (layer + 1) / (float)Layers;
            float height = OwnHeight && shape.Height > 0f ? shape.Height : shape.Size;
            return new Vector3(
                (shape.Out + lu * shape.Size) * separation,
                (shape.Up + lv * height) * separation,
                (shape.Forward + lw * shape.Depth) * separation * (Backward ? -1f : 1f));
        }
        int row = index / Columns, column = index % Columns;
        float u = (column + 0.5f) / Columns - 0.5f;
        float v = 0.5f - (row + 0.5f) / Rows;
        float s = separation;
        float dome = MathF.Max(0f, 1f - (u * u + v * v) * 2f);
        return new Vector3(
            (shape.Out + u * shape.Size) * s,
            (shape.Up + v * shape.Size) * s,
            (shape.Forward + shape.Depth * dome) * s);
    }

    public Vector3 ModelOffset(int index, BreastRigShape shape, float separation, float outwardSign)
    {
        var o = Offset(index, shape, separation);
        return new Vector3(o.X * outwardSign, o.Y, o.Z);
    }

    /// <summary>The part's own axes (model space): outward, up and forward; along a limb for a <see cref="Child"/>
    /// part (up = from the next joint back to the part's bone), else the body's.</summary>
    public (Vector3 Out, Vector3 Up, Vector3 Forward) Axes(Vector3 bone, Vector3? child, float outwardSign)
    {
        var outward = new Vector3(outwardSign, 0f, 0f);
        if (Child == null || child is not { } c || Vector3.DistanceSquared(c, bone) < 1e-8f)
            return (outward, Vector3.UnitY, Vector3.UnitZ);
        var up = Vector3.Normalize(bone - c);
        var o = outward - up * Vector3.Dot(outward, up);
        o = o.LengthSquared() > 1e-6f ? Vector3.Normalize(o) : Vector3.Normalize(Vector3.Cross(up, Vector3.UnitZ));
        var f = Vector3.UnitZ - up * Vector3.Dot(Vector3.UnitZ, up) - o * Vector3.Dot(Vector3.UnitZ, o);
        f = f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vector3.Cross(o, up);
        return (o, up, f);
    }

    /// <summary>Where grid point <paramref name="index"/> sits from the part's bone, in model space, along these axes.</summary>
    public Vector3 PlacedOffset(int index, BreastRigShape shape, float separation, (Vector3 Out, Vector3 Up, Vector3 Forward) axes)
    {
        var o = Offset(index, shape, separation);
        return axes.Out * o.X + axes.Up * o.Y + axes.Forward * o.Z;
    }
}

/// <summary>The rigged parts Limber knows. Each is added to any skeleton that has its bones.</summary>
public static class RigParts
{

    // Lattices (3 x 3 x 3 bones per side, back face included): what the soft bodies move.
    public static readonly RigPart Breasts = new()
    {
        Key = "breasts", Label = "Breasts", Prefix = "lmb_lat_mune_", Rows = 3, Columns = 3, Layers = 3, Lattice = true, BackLayerBones = true,
        Nipples = true,
        Parents = ["iv_c_mune_{side}", "j_mune_{side}"],
        DefaultShape = () => new BreastRigShape { Size = 1.0f, Forward = -0.05f, Up = -0.05f, Out = 0.05f, Depth = 0.9f },
        FallbackSeparation = 0.17f,
    };

    public static readonly RigPart Testicles = new()
    {
        Key = "testicles", Label = "Testicles", Prefix = "lmb_lat_kougan_", Rows = 3, Columns = 3, Layers = 3, Lattice = true, BackLayerBones = true,
        GrowsFromTop = true,
        Parents = ["iv_kougan_{side}"],
        DefaultShape = () => new BreastRigShape { Size = 1.4f, Forward = -0.3f, Up = -0.25f, Out = 0f, Depth = 1.0f },
        FallbackSeparation = 0.04f,
    };

    // The butt (IVCS's butt bones): a box per cheek growing back from the bone.
    public static readonly RigPart Butt = new()
    {
        Key = "butt", Label = "Butt", Prefix = "lmb_lat_shiri_", Rows = 3, Columns = 3, Layers = 3, Lattice = true, BackLayerBones = true,
        Backward = true,
        Parents = ["iv_shiri_{side}"],
        DefaultShape = () => new BreastRigShape { Size = 1.3f, Forward = -0.35f, Up = -0.1f, Out = 0.05f, Depth = 1.0f },
        FallbackSeparation = 0.12f,
    };

    // Body shape grids (the Body Shape tab's detail sliders move them; no physics). Small, so they fit next to the
    // rest of the rig in a mesh: a box of 2 x 2 x 2 bones (thighs 3 tall), lined up along each limb.
    private static RigPart ShapeGrid(string key, string label, string bone, string? child, int rows, BreastRigShape shape, float separation, bool single = false)
        => new()
        {
            Key = key, Label = label, Prefix = $"lmb_shp_{key}_", Rows = rows, Columns = 2, Layers = 2, Lattice = true, BackLayerBones = true,
            Shape = true, Single = single, Child = child, OwnHeight = child != null,
            Parents = [bone], DefaultShape = () => shape.Clone(), FallbackSeparation = separation,
        };

    public static readonly RigPart ShapeThighs = ShapeGrid("thigh", "Thighs (shape)", "j_asi_a_{side}", "j_asi_b_{side}", 3,
        new BreastRigShape { Size = 0.9f, Height = 2.2f, Up = -1.1f, Out = 0f, Forward = -0.45f, Depth = 0.9f }, 0.18f);
    public static readonly RigPart ShapeCalves = ShapeGrid("calf", "Calves (shape)", "j_asi_c_{side}", "j_asi_d_{side}", 2,
        new BreastRigShape { Size = 0.6f, Height = 1.8f, Up = -0.9f, Out = 0f, Forward = -0.3f, Depth = 0.6f }, 0.2f);
    public static readonly RigPart ShapeUpperArms = ShapeGrid("uarm", "Upper arms (shape)", "j_ude_a_{side}", "j_ude_b_{side}", 2,
        new BreastRigShape { Size = 0.25f, Height = 0.7f, Up = -0.35f, Out = 0f, Forward = -0.12f, Depth = 0.25f }, 0.35f);
    public static readonly RigPart ShapeForearms = ShapeGrid("farm", "Forearms (shape)", "j_ude_b_{side}", "j_te_{side}", 2,
        new BreastRigShape { Size = 0.2f, Height = 0.65f, Up = -0.33f, Out = 0f, Forward = -0.1f, Depth = 0.2f }, 0.35f);
    public static readonly RigPart ShapeHips = ShapeGrid("hips", "Hips (shape)", "j_kosi", null, 2,
        new BreastRigShape { Size = 1.6f, Up = 0f, Out = 0f, Forward = -0.6f, Depth = 1.2f }, 0.17f, single: true);
    public static readonly RigPart ShapeBelly = ShapeGrid("belly", "Belly (shape)", "j_sebo_a", null, 2,
        new BreastRigShape { Size = 1.5f, Up = 0.3f, Out = 0f, Forward = -0.6f, Depth = 1.2f }, 0.17f, single: true);
    public static readonly RigPart ShapeChest = ShapeGrid("chest", "Chest (shape)", "j_sebo_b", null, 2,
        new BreastRigShape { Size = 1.6f, Up = 0.3f, Out = 0f, Forward = -0.6f, Depth = 1.2f }, 0.17f, single: true);
    public static readonly RigPart ShapeNeck = ShapeGrid("neck", "Neck (shape)", "j_kubi", null, 2,
        new BreastRigShape { Size = 0.6f, Up = 0.3f, Out = 0f, Forward = -0.3f, Depth = 0.6f }, 0.17f, single: true);

    /// <summary>The body shape grids (rigged with the rest; moved only by the Body Shape tab).</summary>
    public static readonly RigPart[] ShapeParts = [ShapeHips, ShapeThighs, ShapeCalves, ShapeBelly, ShapeChest, ShapeUpperArms, ShapeForearms, ShapeNeck];

    // The old surface grids: still built (models weighted to them keep loading and just don't move), never simulated.
    public static readonly RigPart LegacyBreasts = new()
    {
        Key = "breasts_grid", Label = "Breasts (old grid)", Prefix = "lmb_mune_", Rows = 4, Columns = 4, Legacy = true,
        Parents = ["iv_c_mune_{side}", "j_mune_{side}"],
        DefaultShape = () => new BreastRigShape(),
        FallbackSeparation = 0.17f,
    };

    public static readonly RigPart LegacyTesticles = new()
    {
        Key = "testicles_grid", Label = "Testicles (old grid)", Prefix = "lmb_kougan_", Rows = 3, Columns = 3, Legacy = true,
        Parents = ["iv_kougan_{side}"],
        DefaultShape = () => new BreastRigShape { Size = 1.3f, Forward = 0f, Up = -0.1f, Out = 0f, Depth = 0.25f },
        FallbackSeparation = 0.04f,
    };

    /// <summary>The parts shown in the Rigging tab and moved by the soft bodies.</summary>
    public static readonly RigPart[] All = [Breasts, Testicles, Butt];

    /// <summary>Everything the rig mod adds to the skeleton (old grids first, so they keep their place).</summary>
    // (The body shape grids aren't built: on top of the rest, the skeleton has more bones than the game moves models
    // with, so skin on them stayed behind. Kept defined so models rigged with them can be rigged again without.)
    public static readonly RigPart[] Built = [LegacyBreasts, LegacyTesticles, Breasts, Testicles, Butt];

    /// <summary>Everything one-click rigging does: the soft parts first (they get room in a mesh first), then the shape grids.</summary>
    public static readonly RigPart[] Rigged = [.. All];

    public static bool IsRigBone(string name)
    {
        foreach (var part in ShapeParts)
            if (part.IsMine(name))
                return true;
        foreach (var part in Built)
            if (part.IsMine(name))
                return true;
        return false;
    }
}

/// <summary>The breast part, by its old name.</summary>
public static class BreastRig
{
    public static RigPart Part => RigParts.Breasts;
    public static bool IsRigBone(string name) => RigParts.IsRigBone(name);
}
