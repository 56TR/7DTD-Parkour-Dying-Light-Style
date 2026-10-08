# Retarget CMU motion capture (cgspeed "Daz-friendly" BVH, public domain-like: no restrictions) onto the
# 7 Days to Die third-person skeleton. Same world-delta method as retarget_ual.py; the BVH reference pose is
# all-zero rotations (the skeleton's OFFSETs = T-pose). Facing is normalised so the clip starts looking +Z.
#
#   analyse:  python -I retarget_bvh.py analyse <file.bvh|.zip>
#   convert:  python -I retarget_bvh.py convert <file.bvh|.zip> <skeleton_3p.json> <out_dir> <name> <start_s> <end_s> [loop] [mirror] [nohips] [hangpose]
import json
import math
import os
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from retarget_ual import Target, qinv, qmul, qnorm, qrot, slerp, to_unity_q, to_unity_v  # noqa: E402

FPS = 30.0

# 7DTD bone <- cgspeed Daz-friendly CMU joint
MAP = {
    'Hips': 'Hips', 'Spine1': 'LowerBack', 'Spine2': 'Spine', 'Spine3': 'Spine1', 'Neck': 'Neck', 'Head': 'Head',
    'LeftShoulder': 'LeftShoulder', 'LeftArm': 'LeftArm', 'LeftForeArm': 'LeftForeArm', 'LeftHand': 'LeftHand',
    'RightShoulder': 'RightShoulder', 'RightArm': 'RightArm', 'RightForeArm': 'RightForeArm', 'RightHand': 'RightHand',
    'LeftUpLeg': 'LeftUpLeg', 'LeftLeg': 'LeftLeg', 'LeftFoot': 'LeftFoot', 'LeftToeBase': 'LeftToeBase',
    'RightUpLeg': 'RightUpLeg', 'RightLeg': 'RightLeg', 'RightFoot': 'RightFoot', 'RightToeBase': 'RightToeBase',
}


def axis_q(axis, deg):
    h = math.radians(deg) * 0.5
    s = math.sin(h)
    return {'X': (s, 0.0, 0.0, math.cos(h)), 'Y': (0.0, s, 0.0, math.cos(h)), 'Z': (0.0, 0.0, s, math.cos(h))}[axis]


class Bvh:
    def __init__(self, path):
        if path.lower().endswith('.zip'):
            z = zipfile.ZipFile(path)
            text = z.read([n for n in z.namelist() if n.lower().endswith('.bvh')][0]).decode('latin-1')
        else:
            text = open(path, encoding='latin-1').read()
        tok = text.split()
        self.names, self.parent, self.offset, self.channels = [], [], [], []
        i, stack = 0, []
        while tok[i] != 'MOTION':
            t = tok[i]
            if t in ('ROOT', 'JOINT'):
                self.names.append(tok[i + 1])
                self.parent.append(stack[-1] if stack else -1)
                self.offset.append((0.0, 0.0, 0.0))
                self.channels.append([])
                stack.append(len(self.names) - 1)
                i += 2
            elif t == 'End':
                depth = 0
                while True:
                    i += 1
                    if tok[i] == '{':
                        depth += 1
                    elif tok[i] == '}':
                        depth -= 1
                        if depth == 0:
                            break
                i += 1
            elif t == 'OFFSET':
                self.offset[stack[-1]] = tuple(float(x) for x in tok[i + 1:i + 4])
                i += 4
            elif t == 'CHANNELS':
                n = int(tok[i + 1])
                self.channels[stack[-1]] = tok[i + 2:i + 2 + n]
                i += 2 + n
            elif t == '}':
                stack.pop()
                i += 1
            else:
                i += 1
        i += 1
        nframes = int(tok[i + 1])
        self.dt = float(tok[i + 4])
        i += 5
        width = sum(len(c) for c in self.channels)
        vals = [float(x) for x in tok[i:i + nframes * width]]
        self.frames = [vals[f * width:(f + 1) * width] for f in range(nframes)]
        self.index = {n: k for k, n in enumerate(self.names)}

    def length(self):
        return len(self.frames) * self.dt

    def world(self, f):
        """World positions / rotations (BVH space) of all joints at frame f (float -> lerp)."""
        f0 = max(0, min(int(math.floor(f)), len(self.frames) - 1))
        f1 = min(f0 + 1, len(self.frames) - 1)
        k = f - math.floor(f)
        return self._world(self.frames[f0], self.frames[f1], k)

    def _world(self, a, b, k):
        out = []
        ci = 0
        for j, chans in enumerate(self.channels):
            pos = list(self.offset[j])
            q = (0.0, 0.0, 0.0, 1.0)
            qa = qb = (0.0, 0.0, 0.0, 1.0)
            for c in chans:
                va, vb = a[ci], b[ci]
                ci += 1
                if c.endswith('position'):
                    pos['XYZ'.index(c[0])] = va + (vb - va) * k
                else:
                    qa = qmul(qa, axis_q(c[0], va))
                    qb = qmul(qb, axis_q(c[0], vb))
            if any(c.endswith('rotation') for c in chans):
                q = slerp(qa, qb, k)
            p = self.parent[j]
            if p < 0:
                out.append((tuple(pos), q))
            else:
                pp, pq = out[p]
                v = qrot(pq, pos)
                out.append(((pp[0] + v[0], pp[1] + v[1], pp[2] + v[2]), qmul(pq, q)))
        return out

    def rest(self):
        """cgspeed MotionBuilder-friendly releases start with a T-pose frame: use it as the reference pose."""
        return self._world(self.frames[0], self.frames[0], 0.0)


def analyse(path):
    b = Bvh(path)
    hip, head = b.index['Hips'], b.index['Head']
    lh, rh, lf, rf = b.index['LeftHand'], b.index['RightHand'], b.index['LeftFoot'], b.index['RightFoot']
    rest = b.rest()
    floor = min(rest[lf][0][1], rest[rf][0][1])
    print(f'{path}: {len(b.frames)} frames, {b.length():.1f}s, rest hip height {rest[hip][0][1] - floor:.1f} units')
    step = max(1, int(round(0.25 / b.dt)))
    prev = None
    for f in range(0, len(b.frames), step):
        w = b.world(f)
        hp = w[hip][0]
        speed = 0.0 if prev is None else math.hypot(hp[0] - prev[0], hp[2] - prev[2]) / (step * b.dt)
        prev = hp
        print(f'  t={f * b.dt:5.2f}s hip={hp[1]:6.1f} head={w[head][0][1]:6.1f} hands={w[lh][0][1]:6.1f}/{w[rh][0][1]:6.1f} '
              f'feet={w[lf][0][1]:6.1f}/{w[rf][0][1]:6.1f} hspeed={speed:5.1f}')


ARMS = ('LeftShoulder', 'LeftArm', 'LeftForeArm', 'LeftHand', 'RightShoulder', 'RightArm', 'RightForeArm', 'RightHand')
# 'hangpose': hanging still from a ledge. Arms come from the mocap frame at start_s (held for the whole clip),
# the rest is built: torso upright, head tilted up at the edge, legs dangling with a small knee bend.
# Extra world-space rotations in Unity space (+Z forward, +X right; negative X angle = swing forward / look up).
HANG_POSE = {
    'Head': ('X', -12.0), 'Neck': ('X', -6.0),
    'LeftUpLeg': ('X', -14.0), 'RightUpLeg': ('X', -8.0),
    'LeftLeg': ('X', 6.0), 'RightLeg': ('X', 10.0),
    'LeftFoot': ('X', 25.0), 'RightFoot': ('X', 28.0), 'LeftToeBase': ('X', 25.0), 'RightToeBase': ('X', 28.0),
}


def convert(path, skel, out_dir, name, t0, t1, loop, mirror, nohips=False, hangpose=False):
    b = Bvh(path)
    tgt = Target(skel)
    rest = b.rest()
    hip = b.index['Hips']
    floor = min(rest[b.index['LeftFoot']][0][1], rest[b.index['RightFoot']][0][1])
    hip_rest_h = rest[hip][0][1] - floor
    scale = tgt.by['Hips']['pos'][1] / max(hip_rest_h, 1e-3)
    # facing: yaw of the hip at the start -> make it +Z
    q0 = b.world(t0 / b.dt)[hip][1]
    fwd = qrot(q0, (0.0, 0.0, 1.0))
    yaw = math.degrees(math.atan2(fwd[0], fwd[2]))
    unyaw = axis_q('Y', -yaw)
    start_hip = qrot(unyaw, b.world(t0 / b.dt)[hip][0])
    bones = [k for k in MAP if MAP[k] in b.index and k in tgt.by]
    n = max(2, int(round((t1 - t0) * FPS)) + 1)
    frames, hips = [], []
    for f in range(n):
        t = t0 if hangpose else t0 + min(t1 - t0, f / FPS)
        w = b.world(t / b.dt)
        world = {}
        for name_t in tgt.order:
            bd = tgt.by[name_t]
            pw = world.get(bd['parent'], (0.0, 0.0, 0.0, 1.0))
            if name_t in bones:
                j = b.index[MAP[name_t]]
                sw = to_unity_q(qmul(unyaw, w[j][1]))
                sr = to_unity_q(rest[j][1])
                if hangpose and name_t not in ARMS:
                    ax = HANG_POSE.get(name_t)
                    delta = axis_q(ax[0], ax[1]) if ax else (0.0, 0.0, 0.0, 1.0)
                    world[name_t] = qnorm(qmul(delta, tgt.ref_world[name_t]))
                else:
                    world[name_t] = qnorm(qmul(qmul(sw, qinv(sr)), tgt.ref_world[name_t]))
            else:
                world[name_t] = qmul(pw, tuple(bd['rot']))
        row = []
        for name_t in bones:
            bd = tgt.by[name_t]
            pw = world.get(bd['parent'], (0.0, 0.0, 0.0, 1.0))
            row.extend(round(c, 5) for c in qnorm(qmul(qinv(pw), world[name_t])))
        frames.append(row)
        hp = w[hip][0]
        dy = hp[1] - floor - hip_rest_h  # height above the T-pose floor minus standing hip height
        hips.append([0.0, 0.0 if nohips else round(dy * scale, 4), 0.0])   # vertical only: the motor moves the body
    if mirror:
        frames, bones = mirror_clip(frames, bones)
    doc = {'name': name, 'source': os.path.basename(path), 'license': 'CMU Graphics Lab mocap (cgspeed BVH conversion, no restrictions)',
           'fps': FPS, 'length': round(t1 - t0, 4), 'loop': loop, 'frameCount': len(frames), 'bones': bones,
           'hips': [c for h in hips for c in h], 'rot': [c for fr in frames for c in fr]}
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, name + '.json')
    with open(out, 'w') as fh:
        json.dump(doc, fh, separators=(',', ':'))
    print(f'{name}: {len(frames)} frames, {len(bones)} bones, {t1 - t0:.2f}s, hips dy {min(h[1] for h in hips):.2f}..{max(h[1] for h in hips):.2f} -> {out}')


def mirror_clip(frames, bones):
    """Swap left/right and mirror rotations across the X=0 plane (x, -y, -z, w)."""
    swap = {b: (b.replace('Left', 'Right') if b.startswith('Left') else b.replace('Right', 'Left') if b.startswith('Right') else b) for b in bones}
    idx = {b: i for i, b in enumerate(bones)}
    out = []
    for row in frames:
        new = []
        for b in bones:
            s = idx[swap[b]] * 4
            x, y, z, w = row[s:s + 4]
            new.extend((x, -y, -z, w))
        out.append(new)
    return out, bones


if __name__ == '__main__':
    if sys.argv[1] == 'analyse':
        analyse(sys.argv[2])
    else:
        a = sys.argv[2:]
        convert(a[0], a[1], a[2], a[3], float(a[4]), float(a[5]), 'loop' in a[6:], 'mirror' in a[6:], 'nohips' in a[6:],
                'hangpose' in a[6:])
