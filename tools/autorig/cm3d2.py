"""Readers and writers for the COM3D2 file formats the auto-rigger touches (.model, .menu, .tex)."""
import struct


class Reader:
    def __init__(self, data):
        self.d, self.p = data, 0

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.p)[0]
        self.p += 4
        return v

    def floats(self, n):
        v = list(struct.unpack_from("<%df" % n, self.d, self.p))
        self.p += 4 * n
        return v

    def s(self):
        n, shift = 0, 0
        while True:
            b = self.u8()
            n |= (b & 0x7F) << shift
            shift += 7
            if not b & 0x80:
                break
        v = self.d[self.p:self.p + n].decode("utf-8")
        self.p += n
        return v


def wstr(s):
    b = s.encode("utf-8")
    n = len(b)
    out = bytearray()
    while True:
        byte = n & 0x7F
        n >>= 7
        if n:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            break
    return bytes(out) + b


class Model:
    """A .model file (layout versions below 2001). Keeps byte ranges so untouched data is copied verbatim."""

    def __init__(self, data):
        self.data = data
        r = Reader(data)
        self.signature = r.s()
        self.version = r.i32()
        if self.version >= 2001:
            raise ValueError("model version %d not supported" % self.version)
        self.name = r.s()
        self.root = r.s()
        count = r.i32()
        self.bone_names, self.bone_flags = [], []
        for _ in range(count):
            self.bone_names.append(r.s())
            self.bone_flags.append(r.u8())
        self.parents = [r.i32() for _ in range(count)]
        self.local = [r.floats(7) for _ in range(count)]  # position xyz, rotation xyzw
        self.vertex_count = r.i32()
        self.submesh_count = r.i32()
        skin_count = r.i32()
        self.skin_bones = [r.s() for _ in range(skin_count)]
        self.bindposes = [r.floats(16) for _ in range(skin_count)]
        self.vertex_block_start = r.p
        self.positions = []
        for _ in range(self.vertex_count):
            self.positions.append(r.floats(3))
            r.floats(5)
        tangents = r.i32()
        r.floats(4 * tangents)
        self.weights_start = r.p
        self.weights = []
        for _ in range(self.vertex_count):
            idx = list(struct.unpack_from("<4H", data, r.p))
            r.p += 8
            self.weights.append((idx, r.floats(4)))
        self.weights_end = r.p
        self.submeshes = []
        for _ in range(self.submesh_count):
            n = r.i32()
            self.submeshes.append(struct.unpack_from("<%dH" % n, data, r.p))
            r.p += 2 * n

    def write(self, name, bones, skin_bones, bindposes, weights):
        """bones: list of (name, flag, parent index, local[7])."""
        out = bytearray()
        out += wstr(self.signature) + struct.pack("<i", self.version) + wstr(name) + wstr(self.root)
        out += struct.pack("<i", len(bones))
        for bone_name, flag, _, _ in bones:
            out += wstr(bone_name) + bytes([flag])
        for _, _, parent, _ in bones:
            out += struct.pack("<i", parent)
        for _, _, _, local in bones:
            out += struct.pack("<7f", *local)
        out += struct.pack("<iii", self.vertex_count, self.submesh_count, len(skin_bones))
        for skin_name in skin_bones:
            out += wstr(skin_name)
        for bind in bindposes:
            out += struct.pack("<16f", *bind)
        out += self.data[self.vertex_block_start:self.weights_start]
        for idx, w in weights:
            out += struct.pack("<4H", *idx) + struct.pack("<4f", *w)
        out += self.data[self.weights_end:]
        return bytes(out)


class Menu:
    def __init__(self, data):
        r = Reader(data)
        self.signature, self.version = r.s(), r.i32()
        self.path, self.name, self.category, self.description = r.s(), r.s(), r.s(), r.s()
        r.i32()
        self.commands = []
        while r.p < len(r.d):
            n = r.u8()
            if n == 0:
                break
            self.commands.append([r.s() for _ in range(n)])

    def to_bytes(self):
        body = bytearray()
        for cmd in self.commands:
            body.append(len(cmd))
            for arg in cmd:
                body += wstr(arg)
        body.append(0)
        head = wstr(self.signature) + struct.pack("<i", self.version)
        head += wstr(self.path) + wstr(self.name) + wstr(self.category) + wstr(self.description)
        return head + struct.pack("<i", len(body)) + bytes(body)


class Texture:
    """A .tex container; image bytes are PNG/JPEG for the versions handled here."""

    def __init__(self, data):
        r = Reader(data)
        self.signature, self.version, self.path = r.s(), r.i32(), r.s()
        self.rects = None
        self.size = None
        if self.version >= 1011:
            count = r.i32()
            self.rects = [r.floats(4) for _ in range(count)]
        if self.version >= 1010:
            self.size = (r.i32(), r.i32(), r.i32())  # width, height, format
        n = r.i32()
        self.image = r.d[r.p:r.p + n]

    def to_bytes(self):
        out = wstr(self.signature) + struct.pack("<i", self.version) + wstr(self.path)
        if self.rects is not None:
            out += struct.pack("<i", len(self.rects))
            for rect in self.rects:
                out += struct.pack("<4f", *rect)
        if self.size is not None:
            out += struct.pack("<3i", *self.size)
        return out + struct.pack("<i", len(self.image)) + self.image


# ---- 4x4 matrices (Unity stores Matrix4x4 column-major: m[row + 4 * col]) ----

def matrix(cols):
    return [[cols[r + 4 * c] for c in range(4)] for r in range(4)]


def flatten(m):
    return [m[r][c] for c in range(4) for r in range(4)]


def mul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def apply(m, v):
    return [m[r][0] * v[0] + m[r][1] * v[1] + m[r][2] * v[2] + m[r][3] for r in range(3)]


def translation(x, y, z):
    return [[1, 0, 0, x], [0, 1, 0, y], [0, 0, 1, z], [0, 0, 0, 1]]


def trs(local):
    x, y, z, qx, qy, qz, qw = local
    return [[1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw), x],
            [2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw), y],
            [2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy), z],
            [0, 0, 0, 1]]
