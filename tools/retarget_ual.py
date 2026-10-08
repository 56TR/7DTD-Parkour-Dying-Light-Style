# Retarget Quaternius Universal Animation Library clips (glTF, CC0) onto the 7 Days to Die third-person skeleton.
#
# Method: both rigs are posed in a T-pose reference (UAL: its "A_TPose" clip, 7DTD: the exported skeleton_3p.json
# rest pose, which is a T-pose). For every frame, each mapped 7DTD bone gets the same WORLD-space rotation change
# relative to that reference as its UAL counterpart:  tgtWorld = srcWorld * inv(srcRefWorld) * tgtRefWorld.
# Unmapped bones (roll / support bones, Spine) keep their rest local rotation. Output = local rotations per frame
# plus a hips offset (root motion removed), as JSON the mod plays on the body after the Animator.
#
# Usage: python -I retarget_ual.py <ual.zip|.glb> <skeleton_3p.json> <out_dir> <ClipName[:outName[:loop]]> ...
import json
import math
import os
import struct
import sys
import zipfile

FPS = 30.0

# 7DTD bone <- UAL2 (UE names) / UAL1 (Rigify DEF- names)
MAP = {
    'Hips': ('pelvis', 'DEF-hips'),
    'Spine1': ('spine_01', 'DEF-spine.001'),
    'Spine2': ('spine_02', 'DEF-spine.002'),
    'Spine3': ('spine_03', 'DEF-spine.003'),
    'Neck': ('neck_01', 'DEF-neck'),
    'Head': ('Head', 'DEF-head'),
    'LeftShoulder': ('clavicle_l', 'DEF-shoulder.L'),
    'LeftArm': ('upperarm_l', 'DEF-upper_arm.L'),
    'LeftForeArm': ('lowerarm_l', 'DEF-forearm.L'),
    'LeftHand': ('hand_l', 'DEF-hand.L'),
    'RightShoulder': ('clavicle_r', 'DEF-shoulder.R'),
    'RightArm': ('upperarm_r', 'DEF-upper_arm.R'),
    'RightForeArm': ('lowerarm_r', 'DEF-forearm.R'),
    'RightHand': ('hand_r', 'DEF-hand.R'),
    'LeftUpLeg': ('thigh_l', 'DEF-thigh.L'),
    'LeftLeg': ('calf_l', 'DEF-shin.L'),
    'LeftFoot': ('foot_l', 'DEF-foot.L'),
    'LeftToeBase': ('ball_l', 'DEF-toe.L'),
    'RightUpLeg': ('thigh_r', 'DEF-thigh.R'),
    'RightLeg': ('calf_r', 'DEF-shin.R'),
    'RightFoot': ('foot_r', 'DEF-foot.R'),
    'RightToeBase': ('ball_r', 'DEF-toe.R'),
}
ROOTS = ('root',)


# ---------------------------------------------------------------- quaternion / vector math (x, y, z, w)

def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qinv(q):
    x, y, z, w = q
    n = x * x + y * y + z * z + w * w
    return (-x / n, -y / n, -z / n, w / n)


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / n for c in q)


def qrot(q, v):
    p = qmul(qmul(q, (v[0], v[1], v[2], 0.0)), qinv(q))
    return (p[0], p[1], p[2])


def slerp(a, b, t):
    d = sum(x * y for x, y in zip(a, b))
    if d < 0:
        b = tuple(-c for c in b)
        d = -d
    if d > 0.9995:
        return qnorm(tuple(x + (y - x) * t for x, y in zip(a, b)))
    th = math.acos(min(1.0, d))
    s = math.sin(th)
    wa, wb = math.sin((1 - t) * th) / s, math.sin(t * th) / s
    return tuple(wa * x + wb * y for x, y in zip(a, b))


def to_unity_q(q):  # glTF right-handed -> Unity left-handed (mirror X)
    return (q[0], -q[1], -q[2], q[3])


def to_unity_v(v):
    return (-v[0], v[1], v[2])


# ---------------------------------------------------------------- glTF reading

def load_glb(path):
    if path.lower().endswith('.zip'):
        z = zipfile.ZipFile(path)
        name = [n for n in z.namelist() if n.lower().endswith('.glb') and 'mannequin' not in n.lower()][0]
        data = z.read(name)
    else:
        data = open(path, 'rb').read()
    jlen = struct.unpack_from('<I', data, 12)[0]
    gltf = json.loads(data[20:20 + jlen])
    off = 20 + jlen
    blen = struct.unpack_from('<I', data, off)[0]
    return gltf, data[off + 8: off + 8 + blen]


def accessor(gltf, binchunk, idx):
    a = gltf['accessors'][idx]
    bv = gltf['bufferViews'][a['bufferView']]
    n = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}[a['type']]
    assert a['componentType'] == 5126, 'only float accessors'
    start = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = bv.get('byteStride', 4 * n)
    out = []
    for i in range(a['count']):
        vals = struct.unpack_from('<%df' % n, binchunk, start + i * stride)
        out.append(vals[0] if n == 1 else vals)
    return out


class Source:
    def __init__(self, path):
        self.gltf, self.bin = load_glb(path)
        nodes = self.gltf['nodes']
        self.names = [nd.get('name', str(i)) for i, nd in enumerate(nodes)]
        self.index = {n: i for i, n in enumerate(self.names)}
        self.parent = {}
        for i, nd in enumerate(nodes):
            for c in nd.get('children', []):
                self.parent[c] = i
        self.rest = [(tuple(nd.get('translation', (0, 0, 0))), tuple(nd.get('rotation', (0, 0, 0, 1))))
                     for nd in nodes]
        self.anims = {a['name']: a for a in self.gltf['animations']}

    def clip(self, name):
        """Per-node sampler functions for one animation: node -> {'translation': (times, values), ...}."""
        a = self.anims[name]
        ch = {}
        length = 0.0
        for c in a['channels']:
            s = a['samplers'][c['sampler']]
            times = accessor(self.gltf, self.bin, s['input'])
            vals = accessor(self.gltf, self.bin, s['output'])
            ch.setdefault(c['target']['node'], {})[c['target']['path']] = (times, vals)
            length = max(length, times[-1])
        return ch, length

    @staticmethod
    def sample(track, t, is_rot):
        times, vals = track
        if t <= times[0]:
            return vals[0]
        if t >= times[-1]:
            return vals[-1]
        lo, hi = 0, len(times) - 1
        while hi - lo > 1:
            mid = (lo + hi) // 2
            if times[mid] <= t:
                lo = mid
            else:
                hi = mid
        k = (t - times[lo]) / max(times[hi] - times[lo], 1e-9)
        a, b = vals[lo], vals[hi]
        return slerp(a, b, k) if is_rot else tuple(x + (y - x) * k for x, y in zip(a, b))

    def world(self, ch, t):
        """World (position, rotation) of every node at time t, glTF space."""
        n = len(self.names)
        local = []
        for i in range(n):
            tr, rot = self.rest[i]
            tracks = ch.get(i, {})
            if 'translation' in tracks:
                tr = self.sample(tracks['translation'], t, False)
            if 'rotation' in tracks:
                rot = qnorm(self.sample(tracks['rotation'], t, True))
            local.append((tr, rot))
        out = [None] * n

        def get(i):
            if out[i] is not None:
                return out[i]
            tr, rot = local[i]
            p = self.parent.get(i)
            if p is None:
                out[i] = (tr, rot)
            else:
                pp, pr = get(p)
                v = qrot(pr, tr)
                out[i] = ((pp[0] + v[0], pp[1] + v[1], pp[2] + v[2]), qmul(pr, rot))
            return out[i]

        for i in range(n):
            get(i)
        return out


# ---------------------------------------------------------------- target skeleton

class Target:
    def __init__(self, path):
        d = json.load(open(path))
        self.bones = d['bones']
        self.by = {b['name']: b for b in self.bones}
        self.order = [b['name'] for b in self.bones]   # parents come before children (exported depth-first)
        self.ref_world = {}
        for b in self.bones:
            p = b['parent']
            q = tuple(b['rot'])
            self.ref_world[b['name']] = qmul(self.ref_world[p], q) if p in self.ref_world else q


def retarget(src, tgt, clip, ref_clip, mapping_col):
    ch, length = src.clip(clip)
    ref_ch, _ = src.clip(ref_clip)
    ref = src.world(ref_ch, 0.0)
    rest_hips = None
    bones = [b for b in MAP if MAP[b][mapping_col] in src.index and b in tgt.by]
    frames, hips = [], []
    n = max(2, int(round(length * FPS)) + 1)

    hips_i = src.index[MAP['Hips'][mapping_col]]
    root_i = next((src.index[r] for r in ROOTS if r in src.index), None)

    def hips_local(w):
        hp = to_unity_v(w[hips_i][0])
        if root_i is None:
            return hp
        rp = to_unity_v(w[root_i][0])
        return (hp[0] - rp[0], hp[1] - rp[1], hp[2] - rp[2])

    ref_h = hips_local(ref)
    tgt_h = tgt.by['Hips']['pos'][1]
    scale = tgt_h / max(ref_h[1], 1e-3)

    for f in range(n):
        t = min(length, f / FPS)
        w = src.world(ch, t)
        world = {}
        local_out = []
        for name in tgt.order:
            b = tgt.by[name]
            pw = world.get(b['parent'], (0.0, 0.0, 0.0, 1.0))
            if name in bones:
                si = src.index[MAP[name][mapping_col]]
                sw = to_unity_q(w[si][1])
                sr = to_unity_q(ref[si][1])
                wq = qnorm(qmul(qmul(sw, qinv(sr)), tgt.ref_world[name]))
                world[name] = wq
            else:
                world[name] = qmul(pw, tuple(b['rot']))
        for name in bones:
            b = tgt.by[name]
            pw = world.get(b['parent'], (0.0, 0.0, 0.0, 1.0))
            lq = qnorm(qmul(qinv(pw), world[name]))
            local_out.extend(round(c, 5) for c in lq)
        frames.append(local_out)
        h = hips_local(w)
        hips.append([round((h[0] - ref_h[0]) * scale, 4), round((h[1] - ref_h[1]) * scale, 4), round((h[2] - ref_h[2]) * scale, 4)])
    return bones, frames, hips, length


def main():
    src_path, skel_path, out_dir = sys.argv[1:4]
    src = Source(src_path)
    tgt = Target(skel_path)
    col = 0 if 'pelvis' in src.index else 1
    ref_clip = 'A_TPose'
    os.makedirs(out_dir, exist_ok=True)
    for spec in sys.argv[4:]:
        parts = spec.split(':')
        clip, out = parts[0], (parts[1] if len(parts) > 1 and parts[1] else parts[0])
        loop = len(parts) > 2 and parts[2] == 'loop'
        bones, frames, hips, length = retarget(src, tgt, clip, ref_clip, col)
        # flat arrays so Unity's JsonUtility can read it: frames = frame-major [bone0 xyzw, bone1 xyzw, ...]
        doc = {'name': out, 'source': clip, 'license': 'CC0 (Quaternius Universal Animation Library)',
               'fps': FPS, 'length': round(length, 4), 'loop': loop, 'frameCount': len(frames), 'bones': bones,
               'hips': [c for h in hips for c in h], 'rot': [c for fr in frames for c in fr]}
        path = os.path.join(out_dir, out + '.json')
        with open(path, 'w') as fh:
            json.dump(doc, fh, separators=(',', ':'))
        print(f'{out}: {len(frames)} frames, {len(bones)} bones, {length:.2f}s, hips dy min {min(h[1] for h in hips):.2f} -> {path}')


if __name__ == '__main__':
    main()
