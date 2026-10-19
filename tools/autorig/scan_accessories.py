"""Classify accessory mods by physics rig type and list auto-rig candidates.

Usage: scan_accessories.py <Mod folder> <output csv>
Prints only a summary; the per-item list goes to the CSV.
"""
import collections
import csv
import math
import os
import struct
import sys

CATEGORIES = {
    "accnip": "Nipple",
    "accheso": "Belly button",
    "accmimi": "Earring",
    "accvag": "Front hole",
    "accxxx": "Front hole",  # one edit-menu category backed by two body slots
}
CATEGORY_ORDER = ["Nipple", "Belly button", "Earring", "Front hole"]
MIN_DANGLE = 0.02  # metres: shorter single-bone dangles gain little from extra joints

# Words that mark an item as something that hangs and should bend: label -> spellings.
DANGLE_WORDS = [
    ("chain", ["chain", "チェーン", "チエーン", "チーン",
               "鎖", "くさり", "クサリ"]),
    ("tassel", ["tassel", "タッセル"]),
    ("pendant", ["pendant", "ペンダント"]),
    ("charm", ["charm", "チャーム"]),
    ("dangle", ["dangle", "ぶら下", "ぶらさ"]),
    ("sway", ["揺れ"]),
]

KINDS = ["Rigid (no physics bones)", "Chain, 1 joint", "Chain, multi-joint", "Dynamic bone (.phy)",
         "Skirt-style rig", "Model not in Mod folder", "Unreadable model"]


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

    def f32(self, n):
        v = struct.unpack_from("<%df" % n, self.d, self.p)
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
        v = self.d[self.p:self.p + n].decode("utf-8", "replace")
        self.p += n
        return v


def matched_words(*texts):
    joined = " ".join(t for t in texts if t).lower()
    return [label for label, spellings in DANGLE_WORDS if any(w in joined for w in spellings)]


def read_bones(path):
    r = Reader(open(path, "rb").read())
    r.s()
    ver = r.i32()
    r.s(); r.s()
    count = r.i32()
    names = []
    for _ in range(count):
        names.append(r.s())
        r.u8()
    parents = [r.i32() for _ in range(count)]
    offsets = []
    for _ in range(count):
        pos = r.f32(3)
        r.f32(4)
        if ver >= 2001 and r.u8():
            r.f32(3)
        offsets.append(math.sqrt(sum(c * c for c in pos)))
    return names, parents, offsets


def analyse(path, stem, phy_stems):
    """Return (rig type, physics roots, max joints per chain, longest chain length in m)."""
    names, parents, offsets = read_bones(path)
    children = collections.defaultdict(list)
    for i, p in enumerate(parents):
        if p >= 0:
            children[p].append(i)
    roots = [i for i, n in enumerate(names)
             if "_yure_" in n and not (parents[i] >= 0 and "_yure_" in names[parents[i]])]
    if not roots:
        return "Rigid (no physics bones)", 0, 0, 0.0
    if any("skirt" in names[i].lower() for i in roots):
        return "Skirt-style rig", len(roots), 0, 0.0
    if stem in phy_stems:
        return "Dynamic bone (.phy)", len(roots), 0, 0.0

    max_joints, longest = 0, 0.0
    for root in roots:
        joints, length, bone = 0, 0.0, root
        while children[bone]:
            child = children[bone][0]  # THair1 follows the first child
            joints += 1
            length += offsets[child]
            bone = child
        max_joints = max(max_joints, joints)
        longest = max(longest, length)
    kind = "Chain, 1 joint" if max_joints <= 1 else "Chain, multi-joint"
    return kind, len(roots), max_joints, longest


def read_menu(path):
    r = Reader(open(path, "rb").read())
    r.s(); r.i32(); r.s()
    name, category, description = r.s(), r.s().lower(), r.s()
    if category not in CATEGORIES:
        return None
    r.i32()
    model_files = []
    while r.p < len(r.d):
        n = r.u8()
        if n == 0:
            break
        args = [r.s() for _ in range(n)]
        if args[0].lower() == "additem" and len(args) > 1:
            model_files.append(args[1])
    return name, CATEGORIES[category], description, model_files


def main():
    root, out_csv = sys.argv[1], sys.argv[2]
    models, phy_stems, menus = {}, set(), []
    for dirpath, dirs, files in os.walk(root):
        # Copies this project generated itself (SOFT/RIG hearts) are not mod-author items.
        dirs[:] = [d for d in dirs if d.lower() not in ("_custom_softphysics", "_custom_autorig")]
        for f in files:
            low = f.lower()
            path = os.path.join(dirpath, f)
            if low.endswith(".model"):
                models.setdefault(low, path)
            elif low.endswith(".phy"):
                phy_stems.add(low[:-4])
            elif low.endswith(".menu"):
                menus.append(path)

    rows, seen = [], set()
    for path in menus:
        try:
            menu = read_menu(path)
        except Exception:  # noqa: BLE001
            continue
        if menu is None:
            continue
        name, category, description, model_files = menu
        base = os.path.basename(path).lower()
        stem = base[:-5]
        if "_z" in stem and stem.rsplit("_z", 1)[-1][:1].isdigit():
            continue  # colour variant of another item
        for model in sorted(set(m.lower() for m in model_files)):
            key = (category, model)
            if key in seen:
                continue  # several menus sharing one model count once
            seen.add(key)
            model_path = models.get(model)
            if not model_path:
                kind, roots, joints, length = "Model not in Mod folder", 0, 0, 0.0
            else:
                try:
                    kind, roots, joints, length = analyse(model_path, model[:-6], phy_stems)
                except Exception:  # noqa: BLE001
                    kind, roots, joints, length = "Unreadable model", 0, 0, 0.0
            candidate = kind == "Chain, 1 joint" and length >= MIN_DANGLE
            words = matched_words(name, description, base, model)
            rows.append([category, name.split("　")[0], base, model, kind, roots, joints,
                         "%.1f" % (length * 100), "yes" if candidate else "", ", ".join(words),
                         "auto-approved" if candidate and words else ("needs review" if candidate else "")])

    with open(out_csv, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(["Category", "Item", "Menu", "Model", "Rig type", "Physics roots", "Joints per chain",
                    "Chain length (cm)", "Auto-rig candidate", "Name matches", "Auto-rig status"])
        w.writerows(sorted(rows))

    table = collections.defaultdict(collections.Counter)
    candidates, named, filtered = collections.Counter(), collections.Counter(), collections.Counter()
    for row in rows:
        table[row[0]][row[4]] += 1
        candidates[row[0]] += bool(row[8])
        named[row[0]] += bool(row[9])
        filtered[row[0]] += row[10] == "auto-approved"

    print("\t".join(["Category", "Models", "Rigid", "1 joint", "Multi-joint", "Dyn bone", "Other/missing",
                     "Candidates", "Name match", "Auto-approved", "Needs review"]))
    for cat in CATEGORY_ORDER:
        if cat not in table:
            continue
        c = table[cat]
        other = c["Skirt-style rig"] + c["Model not in Mod folder"] + c["Unreadable model"]
        print("\t".join(str(x) for x in [cat, sum(c.values()), c[KINDS[0]], c[KINDS[1]], c[KINDS[2]], c[KINDS[3]],
                                         other, candidates[cat], named[cat], filtered[cat],
                                         candidates[cat] - filtered[cat]]))
    print("	".join(str(x) for x in ["TOTAL", len(rows), "", "", "", "", "", sum(candidates.values()), sum(named.values()),
                                     sum(filtered.values()), sum(candidates.values()) - sum(filtered.values())]))
    print()
    print("Candidates passing the filter, by matched word:")
    for word, count in collections.Counter(w for r in rows if r[10] == "auto-approved" for w in r[9].split(", ")).most_common():
        print("  %3d  %s" % (count, word))
    print()
    print("Name-matched items that are not candidates, by rig type:")
    for kind, count in collections.Counter(r[4] for r in rows if r[9] and not r[8]).most_common():
        print("  %3d  %s" % (count, kind))
    print()
    print("Auto-approved:")
    for row in sorted(rows):
        if row[10] == "auto-approved":
            print("  %-12s | %-8s | %s | %s | %s cm" % (row[0], row[9], row[1][:34], row[2], row[7]))


main()
