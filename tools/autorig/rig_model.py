"""Split single-joint _yure_ dangles into multi-joint chains (the step the RIG heart did by hand).

For every physics root whose chain is one bone plus an end marker, the segment is divided into 2-4
joints along its own axis. Mesh weights on that bone are redistributed by distance along the axis.
Each separate mesh piece (a chain link, bead, bell, gem...) keeps one weight profile, taken at its
centre, so it moves rigidly; only a piece reaching across much of the dangle bends smoothly.
"""
import collections
import math

from cm3d2 import Model, apply, flatten, matrix, mul, translation, trs

MIN_LENGTH = 0.02         # m; shorter dangles are left alone
TARGET_SEGMENT = 0.012    # m; joint spacing aimed for
MAX_SEGMENTS = 4
FLEXIBLE_EXTENT = 0.35    # a piece spanning more than this share of the dangle bends instead of staying rigid
BLEND_SHARE = 0.25        # weight blend half-width as a share of one segment...
MAX_BLEND = 0.0015        # ...but at most this many metres
WELD = 1e-5               # m; vertices this close are treated as the same point when finding pieces


THIN_RATIO = 0.3          # ...and is less wide than this share of its length
CONTAIN_TOLERANCE = 0.0005  # m; slack when testing whether one piece sits inside another


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def perpendicular_basis(axis):
    helper = [1.0, 0.0, 0.0] if abs(axis[0]) < 0.9 else [0.0, 1.0, 0.0]
    e1 = [axis[1] * helper[2] - axis[2] * helper[1], axis[2] * helper[0] - axis[0] * helper[2],
          axis[0] * helper[1] - axis[1] * helper[0]]
    n = math.sqrt(dot(e1, e1))
    e1 = [c / n for c in e1]
    e2 = [axis[1] * e1[2] - axis[2] * e1[1], axis[2] * e1[0] - axis[0] * e1[2], axis[0] * e1[1] - axis[1] * e1[0]]
    return e1, e2


def smooth(x):
    x = max(0.0, min(1.0, x))
    return x * x * (3 - 2 * x)


def profile(s, joints, blend):
    """Share of the physics weight for each segment bone at distance s along the dangle."""
    steps = [smooth((s - (j - blend)) / (2 * blend)) for j in joints]
    shares, carried = [], 1.0
    for t in steps:
        shares.append(carried * (1 - t))
        carried *= t
    shares.append(carried)
    return shares


def mesh_pieces(m):
    """Union-find over triangles, welding vertices at the same position (UV/normal seams)."""
    parent = list(range(m.vertex_count))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb

    for tris in m.submeshes:
        for i in range(0, len(tris) - 2, 3):
            union(tris[i], tris[i + 1])
            union(tris[i], tris[i + 2])
    seen = {}
    for v, p in enumerate(m.positions):
        key = (round(p[0] / WELD), round(p[1] / WELD), round(p[2] / WELD))
        if key in seen:
            union(v, seen[key])
        else:
            seen[key] = v
    return [find(v) for v in range(m.vertex_count)]


def physics_roots(m):
    children = collections.defaultdict(list)
    for i, p in enumerate(m.parents):
        if p >= 0:
            children[p].append(i)
    roots = [i for i, n in enumerate(m.bone_names)
             if "_yure_" in n and not (m.parents[i] >= 0 and "_yure_" in m.bone_names[m.parents[i]])]
    return roots, children


def dangle_options(options, bone_name):
    """Model-wide options, with any per-dangle entry ("dangles": {bone name: {...}}) on top."""
    merged = {k: v for k, v in (options or {}).items() if k != "dangles"}
    merged.update(((options or {}).get("dangles") or {}).get(bone_name, {}))
    return merged


def joint_positions(length, opts, bone_name):
    """Distances (m) from the dangle's top where the new joints go."""
    if "pivots_cm" in opts:
        joints = [x / 100.0 for x in opts["pivots_cm"]]
        if not joints or any(b <= a for a, b in zip(joints, joints[1:])) or joints[0] <= 0 or joints[-1] >= length:
            raise ValueError("%s: pivots_cm must increase and lie between 0 and %.1f cm"
                             % (bone_name, length * 100))
        return joints
    segments = opts.get("joints") or max(2, min(MAX_SEGMENTS, int(round(length / TARGET_SEGMENT))))
    return [length * k / segments for k in range(1, segments)]


def preset_name(name, preset):
    if preset == "hard":
        return name.replace("_yure_soft_", "_yure_hard_")
    if preset == "keep":
        return name
    return name.replace("_yure_hard_", "_yure_soft_")


def rig(data, new_name, options=None):
    """Return (new model bytes, report dict). Raises ValueError when nothing can be rigged.

    options (from overrides.json) may set joints, pivots_cm, all_rigid and preset for the whole
    model, and per dangle under "dangles": {root bone name: {...}}.
    """
    m = Model(data)
    roots, children = physics_roots(m)
    pieces = mesh_pieces(m)

    plans = []
    for y in roots:
        kids = children[y]
        if not kids or children[kids[0]]:
            continue  # already a multi-joint chain, or no end marker
        nub = kids[0]
        offset = m.local[nub][:3]
        length = math.sqrt(sum(c * c for c in offset))
        if length < MIN_LENGTH or m.bone_names[y] not in m.skin_bones:
            continue
        opts = dangle_options(options, m.bone_names[y])
        if opts.get("skip"):
            continue
        joints = joint_positions(length, opts, m.bone_names[y])
        smallest = min(b - a for a, b in zip([0.0] + joints, joints + [length]))
        axis = [c / length for c in offset]
        plans.append({"bone": y, "nub": nub, "length": length, "axis": axis, "joints": joints,
                      "skin": m.skin_bones.index(m.bone_names[y]),
                      "blend": min(MAX_BLEND, BLEND_SHARE * smallest),
                      "all_rigid": bool(opts.get("all_rigid")), "preset": opts.get("preset", "soft")})
    if not plans:
        raise ValueError("no single-joint dangle of %.0f cm or more to rig" % (MIN_LENGTH * 100))

    # ---- bones: new segment bones go right after their root so they are its first child.
    renamed = {n: n for n in m.bone_names}
    for p in plans:
        name = m.bone_names[p["bone"]]
        renamed[name] = preset_name(name, p["preset"])
    order, new_local, new_parent = [], {}, {}
    plan_by_bone = {p["bone"]: p for p in plans}
    used = set(m.bone_names)
    for i, name in enumerate(m.bone_names):
        order.append(("old", i))
        plan = plan_by_bone.get(i)
        if not plan:
            continue
        base = name.replace("_yure_hard_", "_").replace("_yure_soft_", "_").replace("_yure_", "_").strip("_")
        plan["new_names"] = []
        previous_name, previous_s = renamed[name], 0.0
        for k, s in enumerate(plan["joints"], start=2):
            seg_name = "%s_rigseg%d" % (base, k)
            while seg_name in used:
                seg_name += "x"
            used.add(seg_name)
            step = s - previous_s
            new_local[seg_name] = [plan["axis"][0] * step, plan["axis"][1] * step, plan["axis"][2] * step, 0, 0, 0, 1]
            new_parent[seg_name] = previous_name
            plan["new_names"].append(seg_name)
            order.append(("new", seg_name))
            previous_name, previous_s = seg_name, s
        rest = plan["length"] - previous_s
        nub_local = list(m.local[plan["nub"]])
        nub_local[:3] = [plan["axis"][0] * rest, plan["axis"][1] * rest, plan["axis"][2] * rest]
        plan["nub_local"], plan["nub_parent"] = nub_local, previous_name

    out_names = [renamed[m.bone_names[i]] if kind == "old" else i for kind, i in order]
    nub_overrides = {p["nub"]: p for p in plans}
    bones = []
    for kind, ref in order:
        if kind == "old":
            i = ref
            if i in nub_overrides:
                p = nub_overrides[i]
                bones.append((renamed[m.bone_names[i]], m.bone_flags[i], out_names.index(p["nub_parent"]), p["nub_local"]))
            else:
                parent = out_names.index(renamed[m.bone_names[m.parents[i]]]) if m.parents[i] >= 0 else -1
                bones.append((renamed[m.bone_names[i]], m.bone_flags[i], parent, m.local[i]))
        else:
            bones.append((ref, 0, out_names.index(new_parent[ref]), new_local[ref]))

    # ---- skin bones and bind poses: existing indices kept, segment bones appended.
    skin_bones = [renamed[n] for n in m.skin_bones]
    bindposes = list(m.bindposes)
    for p in plans:
        root_bind = matrix(m.bindposes[p["skin"]])
        p["seg_skin"] = []
        for name, s in zip(p["new_names"], p["joints"]):
            a = p["axis"]
            p["seg_skin"].append(len(skin_bones))
            skin_bones.append(name)
            bindposes.append(flatten(mul(translation(-a[0] * s, -a[1] * s, -a[2] * s), root_bind)))

    # ---- weights
    for p in plans:
        bind = matrix(m.bindposes[p["skin"]])
        p["s"] = {}
        for v, (idx, w) in enumerate(m.weights):
            if any(b == p["skin"] and x > 0 for b, x in zip(idx, w)):
                q = apply(bind, m.positions[v])
                p["s"][v] = q[0] * p["axis"][0] + q[1] * p["axis"][1] + q[2] * p["axis"][2]
        e1, e2 = perpendicular_basis(p["axis"])
        by_piece = collections.defaultdict(list)
        for v, s in p["s"].items():
            q = apply(bind, m.positions[v])
            by_piece[pieces[v]].append((s, dot(q, e1), dot(q, e2)))
        p["piece_s"], p["rigid"], p["flexible"] = {}, 0, 0
        boxes = {}
        for piece, values in by_piece.items():
            lo = [min(x[k] for x in values) for k in range(3)]
            hi = [max(x[k] for x in values) for k in range(3)]
            along, across = hi[0] - lo[0], max(hi[1] - lo[1], hi[2] - lo[2])
            # Only a long, thin piece (cord, ribbon, one-piece chain) bends; a pendant as wide as
            # it is tall (heart, bell, gem) stays rigid however far it reaches.
            if not p["all_rigid"] and along > FLEXIBLE_EXTENT * p["length"] and across < THIN_RATIO * along:
                p["flexible"] += 1
            else:
                p["piece_s"][piece] = sum(x[0] for x in values) / len(values)
                boxes[piece] = (lo, hi)
                p["rigid"] += 1

        # A piece lying entirely inside a larger rigid piece (a gem set in a frame) moves with it,
        # so attached parts never drift apart. Interlocking chain links are not inside each other.
        def volume(box):
            return (box[1][0] - box[0][0]) * (box[1][1] - box[0][1]) * (box[1][2] - box[0][2])

        ordered = sorted(boxes, key=lambda k: volume(boxes[k]), reverse=True)
        for i, small in enumerate(ordered):
            slo, shi = boxes[small]
            for big in ordered[:i]:
                blo, bhi = boxes[big]
                if all(blo[k] - CONTAIN_TOLERANCE <= slo[k] and shi[k] <= bhi[k] + CONTAIN_TOLERANCE for k in range(3)):
                    p["piece_s"][small] = p["piece_s"][big]
                    break

    weights = []
    for v, (idx, w) in enumerate(m.weights):
        influence = collections.defaultdict(float)
        for b, x in zip(idx, w):
            if x > 0:
                influence[b] += x
        for p in plans:
            physics = influence.pop(p["skin"], 0.0)
            if physics <= 0:
                continue
            s = p["piece_s"].get(pieces[v], p["s"][v])
            for target, share in zip([p["skin"]] + p["seg_skin"], profile(s, p["joints"], p["blend"])):
                if share > 1e-6:
                    influence[target] += physics * share
        top = sorted(((x, b) for b, x in influence.items()), reverse=True)[:4]
        total = sum(x for x, _ in top) or 1.0
        top = [(x / total, b) for x, b in top]
        while len(top) < 4:
            top.append((0.0, 0))
        weights.append(([b for _, b in top], [x for x, _ in top]))

    report = {
        "dangles": len(plans),
        "segments": "+".join(str(len(p["joints"]) + 1) for p in plans),
        "length_cm": "+".join("%.1f" % (p["length"] * 100) for p in plans),
        "rigid_pieces": sum(p["rigid"] for p in plans),
        "flexible_pieces": sum(p["flexible"] for p in plans),
    }
    return m.write(new_name, bones, skin_bones, bindposes, weights), report


def verify(original, rigged):
    """Structural checks; returns a list of problems (empty when everything holds)."""
    problems = []
    a, b = Model(original), Model(rigged)
    if a.data[a.vertex_block_start:a.weights_start] != b.data[b.vertex_block_start:b.weights_start]:
        problems.append("vertex data changed")
    if a.data[a.weights_end:] != b.data[b.weights_end:]:
        problems.append("submesh/material/morph data changed")
    for v, (idx, w) in enumerate(b.weights):
        if abs(sum(w) - 1) > 1e-4 or any(i >= len(b.skin_bones) for i, x in zip(idx, w) if x > 0):
            problems.append("bad weights at vertex %d" % v)
            break
    for v in range(a.vertex_count):
        kept = sum(x for i, x in zip(*a.weights[v]) if "_yure_" not in a.skin_bones[i])
        now = sum(x for i, x in zip(*b.weights[v]) if "_yure_" not in b.skin_bones[i] and "_rigseg" not in b.skin_bones[i])
        if abs(kept - now) > 1e-3:
            problems.append("non-physics weight changed at vertex %d" % v)
            break

    def world(model, i):
        w = trs(model.local[i])
        while model.parents[i] >= 0:
            i = model.parents[i]
            w = mul(trs(model.local[i]), w)
        return w

    # Each new segment bone must sit where its dangle's root bone says it does: hierarchy x bind pose
    # gives the same mesh frame for the segment bone as for the root it hangs from.
    skin = dict(zip(b.skin_bones, b.bindposes))
    for name in b.skin_bones:
        if "_rigseg" not in name:
            continue
        i = b.bone_names.index(name)
        root = i
        while "_yure_" not in b.bone_names[root]:
            root = b.parents[root]
        frame = mul(world(b, i), matrix(skin[name]))
        reference = mul(world(b, root), matrix(skin[b.bone_names[root]]))
        if max(abs(frame[r][c] - reference[r][c]) for r in range(4) for c in range(4)) > 1e-4:
            problems.append("bind pose of %s disagrees with hierarchy" % name)
    return problems
