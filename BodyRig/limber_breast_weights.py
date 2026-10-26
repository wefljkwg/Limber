"""
Limber - body rig weights (Blender script)

Weights each rigged part onto Limber's lattice bones, the way a lattice deformer works: every vertex inside the box
is weighted to the corners of the box cell it sits in, so moving the bones bends the whole volume smoothly (no
creases, even when it's scaled up or swings a long way).
  - breasts:   from iv_c_mune_l/r (or j_mune_l/r) onto lmb_lat_mune_l/r_00..26
  - testicles: from iv_kougan_l/r and iv_kintama_phys_l/r onto lmb_lat_kougan_l/r_00..26
The box is 3 x 3 bones across and 3 layers deep (back face, middle, front). Vertices of the part just outside the box blend
smoothly back to the part's own bone.

Models weighted with the old surface-grid version (lmb_mune_* / lmb_kougan_*) can go straight in: their grid weights
are folded back and redone on the lattice.

Parts whose lattice bones aren't in the armature are skipped. Nothing else is changed: weights on other bones (chest,
spine, arms) stay exactly as they were, and at rest every bone sits exactly where the body puts it, so the model looks
the same as before.

Use (Blender 3.6 or newer):
  1. Import the model exported from Penumbra (File > Import > glTF 2.0). Its armature must include the lmb_lat_*
     bones (export it with Limber's rig mod enabled, rebuilt with Limber 0.24 or newer).
  2. Scripting tab > Open this file > Run Script. It prints what it did in the Info area / system console.
  3. Export (File > Export > glTF 2.0, .glb) with Data > Armature > Skinning "Bone Influence" set to 8,
     and import that back in Penumbra's model editor.
"""
import math

import bpy
import numpy as np

# (lattice prefix, old grid prefix, source vertex groups ("{s}" = l / r), move all sources or only the first found)
PARTS = [
    ("lmb_lat_mune_", "lmb_mune_", ["iv_c_mune_{s}", "j_mune_{s}"], False),
    ("lmb_lat_kougan_", "lmb_kougan_", ["iv_kougan_{s}", "iv_kintama_phys_{s}"], True),
]
ROWS, COLUMNS, LAYERS = 3, 3, 2   # must match Limber's rig: 3 x 3 bones, 3 bone layers (box depth 2 cells)
MAX_INFLUENCES = 8   # FFXIV allows 8 bone influences per vertex; set the glTF exporter to the same number
MARGIN = 1e9         # all of the part's weight goes on the box (the model's own weights blend it into the body)


def rig_name(prefix, side, i):
    return f"{prefix}{side}_{i:02d}"


def per_side():
    return ROWS * COLUMNS * (LAYERS + 1)


def find_armature():
    candidates = [o for o in bpy.data.objects if o.type == 'ARMATURE'
                  and any(rig_name(prefix, 'l', 0) in o.data.bones for prefix, _, _, _ in PARTS)]
    if not candidates:
        raise RuntimeError("No armature with Limber's lattice bones (lmb_lat_mune_* / lmb_lat_kougan_*). Rebuild the rig mod "
                           "in Limber, export the model from Penumbra with it enabled in that collection, then import it again.")
    return candidates[0]


def lattice_index(i):
    """Bone number -> (column, row, layer) in box units; layer 0 is the back face."""
    per_layer = ROWS * COLUMNS
    layer, rest = divmod(i, per_layer)
    row, column = divmod(rest, COLUMNS)
    return column, row, layer


def lattice_frame(arm, prefix, side):
    """Fit the box: an affine map from box units (column, row, layer) to world space, from the bones' rest spots."""
    coords, points = [], []
    for i in range(per_side()):
        bone = arm.data.bones.get(rig_name(prefix, side, i))
        if bone is None:
            return None
        p = arm.matrix_world @ bone.head_local
        coords.append([*lattice_index(i), 1.0])
        points.append([p.x, p.y, p.z])
    a = np.array(coords)
    b = np.array(points)
    m, *_ = np.linalg.lstsq(a, b, rcond=None)  # 4 x 3: rows = column, row, layer axes, origin
    forward = m[:3].T                           # 3 x 3: box units -> world offset
    if abs(np.linalg.det(forward)) < 1e-12:
        return None
    return m, np.linalg.inv(forward)


def box_coords(co, frame):
    m, inverse = frame
    return inverse @ (np.array([co.x, co.y, co.z]) - m[3])


def corner_weights(u, v, w):
    """The corners of the cell's Freudenthal tetrahedron holding (u, v, w), with barycentric weights: 4 corners
    (leaving room for the body's own weights in FFXIV's 8 slots), continuous across cells, exactly linear."""
    c0 = min(int(math.floor(u)), COLUMNS - 2)
    r0 = min(int(math.floor(v)), ROWS - 2)
    l0 = min(int(math.floor(w)), LAYERS - 1)
    f = sorted([(u - c0, 0), (v - r0, 1), (w - l0, 2)], key=lambda t: -t[0])
    weights = [1 - f[0][0], f[0][0] - f[1][0], f[1][0] - f[2][0], f[2][0]]
    at = [c0, r0, l0]
    out = []
    for k in range(4):
        if weights[k] > 1e-6:
            out.append(((at[0], at[1], at[2]), weights[k]))
        if k < 3:
            at[f[k][1]] += 1
    return out

def bone_number(column, row, layer):
    return layer * ROWS * COLUMNS + row * COLUMNS + column


def smoothstep(x):
    x = min(max(x, 0.0), 1.0)
    return x * x * (3 - 2 * x)


def meshes_for(arm):
    for obj in bpy.data.objects:
        if obj.type != 'MESH':
            continue
        uses = obj.parent == arm or any(m.type == 'ARMATURE' and m.object == arm for m in obj.modifiers)
        if uses:
            yield obj


def process_mesh(obj, prefix, old_prefix, sources, merge, side, frame, parent_name, report):
    groups = obj.vertex_groups
    found = [groups.get(name.replace("{s}", side)) for name in sources]
    found = [g for g in found if g is not None]
    old = [g for g in groups if g.name.startswith(f"{old_prefix}{side}_")]
    if not found and not old:
        return 0
    # The back face goes on the bone the lattice hangs from in the skeleton, so the two always move together.
    parent_group = groups.get(parent_name) or groups.new(name=parent_name)
    source_groups = (found if merge else found[:1]) + old
    if parent_group in found and parent_group not in source_groups:
        source_groups.append(parent_group)
    source_indices = {g.index for g in source_groups}
    rig_groups = [groups.get(rig_name(prefix, side, i)) or groups.new(name=rig_name(prefix, side, i)) for i in range(per_side())]
    rig_indices = {g.index for g in rig_groups}
    world = obj.matrix_world
    changed = 0

    for v in obj.data.vertices:
        w = 0.0
        others = []
        for g in v.groups:
            if g.group in source_indices or g.group in rig_indices or g.group == parent_group.index:
                w += g.weight
            elif g.weight > 0:
                others.append(g.weight)
        if w <= 0:
            continue

        u, t, d = box_coords(world @ v.co, frame)
        # Inside the box (clamped), and how far outside it the vertex is: fade the lattice out over the margin.
        cu = min(max(u, 0.0), COLUMNS - 1)
        ct = min(max(t, 0.0), ROWS - 1)
        cd = min(max(d, 0.0), LAYERS)
        # Only the sides count: past the front face it moves with the front layer (so a tip that pokes out of the
        # box still moves fully), and behind the back face is the part's own bone anyway.
        outside = math.sqrt((u - cu) ** 2 + (t - ct) ** 2)
        share = 1.0 - smoothstep(outside / MARGIN)

        wanted = {}   # group index -> weight
        on_parent = w * (1.0 - share)
        for (c, r, l), weight in corner_weights(cu, ct, cd):
            index = rig_groups[bone_number(c, r, l)].index
            wanted[index] = wanted.get(index, 0.0) + w * share * weight
        if on_parent > 1e-6:
            wanted[parent_group.index] = wanted.get(parent_group.index, 0.0) + on_parent

        slots = MAX_INFLUENCES - len(others)
        if slots < 1:
            continue  # no room: leave this vertex as it is
        ranked = sorted(wanted.items(), key=lambda kv: kv[1], reverse=True)
        if len(ranked) > slots:
            # No room for every corner: what doesn't fit goes on the part's own bone (keeps the blend even).
            kept = [(i, x) for i, x in ranked if i != parent_group.index][:slots - 1]
            parent_share = sum(x for _, x in ranked) - sum(x for _, x in kept)
            picked = kept + [(parent_group.index, parent_share)]
        else:
            picked = ranked
        total = sum(x for _, x in picked)
        if total <= 1e-9:
            continue

        for g in source_groups + rig_groups + [parent_group]:
            g.remove([v.index])
        by_index = {g.index: g for g in rig_groups}
        by_index[parent_group.index] = parent_group
        for index, x in picked:
            x = x * w / total
            if x > 1e-5:
                by_index[index].add([v.index], x, 'REPLACE')
        changed += 1

    names = "+".join(g.name for g in source_groups)
    report.append(f"{obj.name}: {changed} vertices of {names} put on {rig_name(prefix, side, 0)}..{per_side() - 1:02d}")
    return changed


def main():
    arm = find_armature()
    report = []
    total = 0
    for prefix, old_prefix, sources, merge in PARTS:
        for side in ("l", "r"):
            frame = lattice_frame(arm, prefix, side)
            if frame is None:
                continue  # this part's lattice isn't in the skeleton
            first = arm.data.bones.get(rig_name(prefix, side, 0))
            parent = getattr(first, "parent", None)
            parent_name = parent.name if parent is not None else sources[0].replace("{s}", side)
            for obj in meshes_for(arm):
                total += process_mesh(obj, prefix, old_prefix, sources, merge, side, frame, parent_name, report)
    if total == 0:
        report.append("No vertices weighted to a rigged part's bones (breasts: iv_c_mune_* / j_mune_*; "
                      "testicles: iv_kougan_* / iv_kintama_phys_*) were found on meshes using this armature.")
    for line in report:
        print("[Limber] " + line)
    msg = f"Limber lattice weights: {total} vertices updated."
    print("[Limber] " + msg)

    def draw(self, context):
        for line in report[-12:]:
            self.layout.label(text=line)
    try:
        bpy.context.window_manager.popup_menu(draw, title=msg, icon='INFO')
    except Exception:
        pass


main()
