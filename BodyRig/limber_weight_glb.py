"""
Limber - lattice weights straight on a .glb (no Blender needed)

Does the same as limber_breast_weights.py, but on the .glb file Penumbra exports, with plain Python + numpy:
  python limber_weight_glb.py exported.glb rigged.glb
Then import rigged.glb back in Penumbra's model editor (Keep current materials / attributes ticked).
Export the model with Limber's rig mod enabled, so its skeleton has the lmb_lat_* bones.
"""
import json
import math
import struct
import sys

import numpy as np

PARTS = [
    ("lmb_lat_mune_", "lmb_mune_", ["iv_c_mune_{s}", "j_mune_{s}"], False),
    ("lmb_lat_kougan_", "lmb_kougan_", ["iv_kougan_{s}", "iv_kintama_phys_{s}"], True),
]
ROWS, COLUMNS, LAYERS = 3, 3, 2  # 3 x 3 bones, 3 bone layers (back face included), box depth 2 cells
MAX_INFLUENCES = 8
MARGIN = 1e9  # all of the part's weight goes on the box


def load(path):
    data = open(path, 'rb').read()
    n = struct.unpack('<I', data[12:16])[0]
    gltf = json.loads(data[20:20 + n])
    o = 20 + n
    bl = struct.unpack('<I', data[o:o + 4])[0]
    return gltf, bytearray(data[o + 8:o + 8 + bl])


def save(path, gltf, binary):
    js = json.dumps(gltf, separators=(',', ':')).encode()
    js += b' ' * ((4 - len(js) % 4) % 4)
    binary = bytes(binary) + b'\0' * ((4 - len(binary) % 4) % 4)
    total = 12 + 8 + len(js) + 8 + len(binary)
    with open(path, 'wb') as f:
        f.write(struct.pack('<III', 0x46546C67, 2, total))
        f.write(struct.pack('<II', len(js), 0x4E4F534A) + js)
        f.write(struct.pack('<II', len(binary), 0x004E4942) + binary)


class View:
    """An accessor read / written in place (interleaved buffers too)."""
    TYPES = {5121: np.uint8, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
    COUNTS = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}

    def __init__(self, gltf, binary, index):
        a = gltf['accessors'][index]
        bv = gltf['bufferViews'][a['bufferView']]
        self.dtype = np.dtype(self.TYPES[a['componentType']])
        self.n = self.COUNTS[a['type']]
        self.count = a['count']
        self.normalized = a.get('normalized', False) and a['componentType'] != 5126
        self.offset = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
        self.stride = bv.get('byteStride', self.dtype.itemsize * self.n)
        self.binary = binary

    def read(self):
        out = np.zeros((self.count, self.n), dtype=np.float64)
        for i in range(self.count):
            o = self.offset + i * self.stride
            out[i] = np.frombuffer(self.binary, dtype=self.dtype, count=self.n, offset=o)
        if self.normalized:
            out /= np.iinfo(self.dtype).max
        return out

    def write(self, values):
        for i in range(self.count):
            o = self.offset + i * self.stride
            v = np.asarray(values[i], dtype=np.float64)
            if self.normalized:
                v = np.round(v * np.iinfo(self.dtype).max)
            self.binary[o:o + self.dtype.itemsize * self.n] = v.astype(self.dtype).tobytes()


def node_matrices(gltf):
    nodes = gltf['nodes']
    parent = {}
    for i, nd in enumerate(nodes):
        for c in nd.get('children', []):
            parent[c] = i

    def local(nd):
        if 'matrix' in nd:
            return np.array(nd['matrix']).reshape(4, 4).T
        t = nd.get('translation', [0, 0, 0])
        x, y, z, w = nd.get('rotation', [0, 0, 0, 1])
        s = nd.get('scale', [1, 1, 1])
        r = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                      [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                      [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
        m = np.eye(4)
        m[:3, :3] = r * np.array(s)
        m[:3, 3] = t
        return m

    world = {}

    def get(i):
        if i not in world:
            world[i] = (get(parent[i]) if i in parent else np.eye(4)) @ local(nodes[i])
        return world[i]
    for i in range(len(nodes)):
        get(i)
    return world, parent


def lattice_index(i):
    per_layer = ROWS * COLUMNS
    layer, rest = divmod(i, per_layer)
    row, column = divmod(rest, COLUMNS)
    return column, row, layer


def bone_number(column, row, layer):
    return layer * ROWS * COLUMNS + row * COLUMNS + column


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

def smoothstep(x):
    x = min(max(x, 0.0), 1.0)
    return x * x * (3 - 2 * x)


def main(src, dst):
    gltf, binary = load(src)
    world, parent = node_matrices(gltf)
    names = [nd.get('name', '') for nd in gltf['nodes']]
    index_of = {n: i for i, n in enumerate(names)}
    report = []
    total = 0
    for ni, nd in enumerate(gltf['nodes']):
        if 'mesh' not in nd or 'skin' not in nd:
            continue
        joints = gltf['skins'][nd['skin']]['joints']
        joint_of = {names[j]: k for k, j in enumerate(joints)}
        mesh_world = world[ni]
        for pi, prim in enumerate(gltf['meshes'][nd['mesh']]['primitives']):
            at = prim['attributes']
            if 'JOINTS_0' not in at:
                continue
            slots = [k for k in range(4) if f'JOINTS_{k}' in at]
            jv = [View(gltf, binary, at[f'JOINTS_{k}']) for k in slots]
            wv = [View(gltf, binary, at[f'WEIGHTS_{k}']) for k in slots]
            J = np.concatenate([v.read() for v in jv], axis=1).astype(int)
            W = np.concatenate([v.read() for v in wv], axis=1)
            pos = View(gltf, binary, at['POSITION']).read()
            pos = (mesh_world @ np.c_[pos, np.ones(len(pos))].T).T[:, :3]
            changed = 0
            for prefix, old_prefix, sources, merge in PARTS:
                for side in ('l', 'r'):
                    before = changed
                    bones = [f"{prefix}{side}_{i:02d}" for i in range(ROWS * COLUMNS * (LAYERS + 1))]
                    if any(b not in joint_of for b in bones):
                        continue
                    coords = np.array([[*lattice_index(i), 1.0] for i in range(len(bones))])
                    points = np.array([world[index_of[b]][:3, 3] for b in bones])
                    m, *_ = np.linalg.lstsq(coords, points, rcond=None)
                    inverse = np.linalg.inv(m[:3].T)
                    first = index_of[bones[0]]
                    parent_name = names[parent[first]] if first in parent else sources[0].replace('{s}', side)
                    if parent_name not in joint_of:
                        continue
                    src_names = [s.replace('{s}', side) for s in sources]
                    used = {int(j) for j, x in zip(J.flatten(), W.flatten()) if x > 0}
                    present = [s for s in src_names if s in joint_of and joint_of[s] in used]
                    if not present and not any(n.startswith(f"{old_prefix}{side}_") and joint_of[n] in used for n in joint_of):
                        continue
                    use = set(present if merge else present[:1])
                    use |= {n for n in joint_of if n.startswith(f"{old_prefix}{side}_")}
                    use |= set(bones)
                    use.add(parent_name)
                    use_idx = {joint_of[n] for n in use}
                    for v in range(len(pos)):
                        w = sum(W[v, k] for k in range(J.shape[1]) if W[v, k] > 0 and J[v, k] in use_idx)
                        if w <= 0:
                            continue
                        # Only vertices that really belong to this part (not just the parent bone, e.g. iv_c_mune
                        # used elsewhere): need weight on one of the part's own source / rig bones.
                        others = [(J[v, k], W[v, k]) for k in range(J.shape[1]) if W[v, k] > 0 and J[v, k] not in use_idx]
                        u, t, d = inverse @ (pos[v] - m[3])
                        cu, ct, cd = min(max(u, 0), COLUMNS - 1), min(max(t, 0), ROWS - 1), min(max(d, 0), LAYERS)
                        share = 1.0 - smoothstep(math.sqrt((u - cu) ** 2 + (t - ct) ** 2) / MARGIN)
                        wanted = {}
                        on_parent = w * (1 - share)
                        for (c, r, l), x in corner_weights(cu, ct, cd):
                            j = joint_of[bones[bone_number(c, r, l)]]
                            wanted[j] = wanted.get(j, 0) + w * share * x
                        pj = joint_of[parent_name]
                        if on_parent > 1e-6:
                            wanted[pj] = wanted.get(pj, 0) + on_parent
                        room = MAX_INFLUENCES - len(others)
                        if room < 1:
                            continue
                        ranked = sorted(wanted.items(), key=lambda kv: kv[1], reverse=True)
                        if len(ranked) > room:
                            kept = [(i, x) for i, x in ranked if i != pj][:room - 1]
                            ranked = kept + [(pj, w - sum(x for _, x in kept))]
                        entries = others + ranked
                        entries = [(j, x) for j, x in entries if x > 1e-5]
                        entries.sort(key=lambda e: e[1], reverse=True)
                        entries = entries[:J.shape[1]]
                        s = sum(x for _, x in entries)
                        J[v] = 0
                        W[v] = 0
                        for k, (j, x) in enumerate(entries):
                            J[v, k] = j
                            W[v, k] = x / s
                        changed += 1
                    report.append(f"{gltf['meshes'][nd['mesh']].get('name')}[{pi}] {prefix}{side}: {changed - before} vertices")
            for k, (a, b) in enumerate(zip(jv, wv)):
                a.write(J[:, 4 * k:4 * k + 4])
                b.write(W[:, 4 * k:4 * k + 4])
            total += changed
    save(dst, gltf, binary)
    for line in report:
        print('[Limber]', line)
    print(f'[Limber] {total} vertices put on the lattice -> {dst}')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
