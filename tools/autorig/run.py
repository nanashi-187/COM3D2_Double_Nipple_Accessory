"""Auto-rig every candidate from accessory_physics_scan.csv into separate, labelled copies.

Usage: run.py <Mod folder> <scan csv> <output folder> <report csv> [--overrides overrides.json]

Originals are never modified. Each rigged model gets new menus (one per colour/variant) whose name and
icon are labelled: RIG for items whose name matched the dangle-word filter (auto-approved) or that an
override marks as approved, RIG? for items that need an in-game check.

Per-model fixes come from known_overrides.json next to this script, then from --overrides (user entries
win). See README.md for the options.
"""
import argparse
import csv
import io
import json
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from cm3d2 import Menu, Texture  # noqa: E402
from rig_model import rig, verify  # noqa: E402

CATEGORIES = {"accnip", "accheso", "accmimi", "accvag", "accxxx"}
SKIP_DIRS = {"_custom_softphysics", "_custom_autorig"}
OUTPUT_MARKER = "autorig_output.txt"
GENERATED_EXTENSIONS = (".model", ".menu", ".tex")
SPACE = "\u3000"
LABELS = {
    "auto-approved": ("\uff08\u591a\u95a2\u7bc0\u63fa\u308c\uff09", "RIG", (200, 90, 0)),
    "needs review": ("\uff08\u591a\u95a2\u7bc0\u63fa\u308c\u30fb\u8981\u78ba\u8a8d\uff09", "RIG?", (120, 40, 160)),
}
FONT_PATH = "C:/Windows/Fonts/arialbd.ttf"

# Override keys: model-level only, and keys allowed both per model and per dangle.
MODEL_KEYS = {"skip", "approved", "dangles"}
DANGLE_KEYS = {"skip", "joints", "pivots_cm", "all_rigid", "preset"}


# ---- overrides ------------------------------------------------------------------------------------

def check_entry(where, entry, allowed):
    if not isinstance(entry, dict):
        raise ValueError("%s: expected an object" % where)
    for key, value in entry.items():
        if key.startswith("_"):
            continue  # comments
        if key not in allowed:
            raise ValueError("%s: unknown option '%s' (allowed: %s)" % (where, key, ", ".join(sorted(allowed))))
        if key in ("skip", "approved", "all_rigid") and not isinstance(value, bool):
            raise ValueError("%s: '%s' must be true or false" % (where, key))
        if key == "joints" and not (isinstance(value, int) and 2 <= value <= 8):
            raise ValueError("%s: 'joints' must be a whole number from 2 to 8" % where)
        if key == "pivots_cm" and not (isinstance(value, list) and value
                                       and all(isinstance(x, (int, float)) for x in value)):
            raise ValueError("%s: 'pivots_cm' must be a list of numbers, e.g. [1.0, 2.2]" % where)
        if key == "preset" and value not in ("soft", "hard", "keep"):
            raise ValueError("%s: 'preset' must be \"soft\", \"hard\" or \"keep\"" % where)
        if key == "dangles":
            if not isinstance(value, dict):
                raise ValueError("%s: 'dangles' must map bone names to options" % where)
            for bone, sub in value.items():
                check_entry("%s / dangle %s" % (where, bone), sub, DANGLE_KEYS)


def load_overrides(paths):
    merged = {}
    for path in paths:
        if not path or not os.path.exists(path):
            continue
        try:
            data = json.load(open(path, encoding="utf-8-sig"))
        except ValueError as ex:
            raise SystemExit("%s is not valid JSON: %s" % (path, ex))
        if not isinstance(data, dict):
            raise SystemExit("%s must contain one object keyed by model file name" % path)
        for model, entry in data.items():
            if model.startswith("_"):
                continue
            key = model.lower() if model.lower().endswith(".model") else model.lower() + ".model"
            try:
                check_entry("%s: %s" % (os.path.basename(path), model), entry, MODEL_KEYS | DANGLE_KEYS)
            except ValueError as ex:
                raise SystemExit(str(ex))
            merged.setdefault(key, {}).update({k: v for k, v in entry.items() if not k.startswith("_")})
    return merged


# ---- files ----------------------------------------------------------------------------------------

def index_files(root):
    found = {".model": {}, ".menu": {}, ".tex": {}}
    for dirpath, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d.lower() not in SKIP_DIRS]
        for f in files:
            ext = os.path.splitext(f)[1].lower()
            if ext in found:
                found[ext].setdefault(f.lower(), os.path.join(dirpath, f))
    return found


def prepare_output(out_dir):
    """Clear files from a previous run, but only in a folder this tool created."""
    os.makedirs(out_dir, exist_ok=True)
    marker = os.path.join(out_dir, OUTPUT_MARKER)
    existing = [f for f in os.listdir(out_dir) if f != OUTPUT_MARKER]
    if existing and not os.path.exists(marker):
        raise SystemExit("%s is not empty and was not created by this tool; choose an empty or new folder."
                         % out_dir)
    for f in existing:
        if f.lower().endswith(GENERATED_EXTENSIONS):
            os.remove(os.path.join(out_dir, f))
    with open(marker, "w", encoding="utf-8") as fh:
        fh.write("Generated by tools/autorig/run.py. This folder is cleared and rewritten on every run;\n"
                 "delete it to remove all auto-rigged copies.\n")


def rig_stem(stem):
    """'item_z1_i_' -> 'itemrig_z1_i_' so colour variants stay grouped with their base item."""
    m = re.match(r"^(.*?)((?:_z\d+)?(?:_i_)?)$", stem, re.IGNORECASE)
    return m.group(1) + "rig" + m.group(2)


def label_name(name, label):
    head, sep, rest = name.partition(SPACE)
    return head + label + sep + rest


def label_icon(data, text, colour):
    tex = Texture(data)
    if not (tex.image.startswith(b"\x89PNG") or tex.image.startswith(b"\xff\xd8")):
        return None  # compressed texture formats are left alone
    img = Image.open(io.BytesIO(tex.image)).convert("RGBA")
    w, h = img.size
    band = max(12, h * 18 // 80)
    overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    draw.rectangle([0, h - band, w, h], fill=colour + (230,))
    font = ImageFont.truetype(FONT_PATH, 15 if h >= 64 else max(8, band - 4))
    box = draw.textbbox((0, 0), text, font=font)
    draw.text(((w - (box[2] - box[0])) / 2 - box[0], h - band + (band - (box[3] - box[1])) / 2 - box[1]),
              text, font=font, fill=(255, 255, 255, 255))
    out = io.BytesIO()
    Image.alpha_composite(img, overlay).save(out, "PNG")
    tex.image = out.getvalue()
    return tex.to_bytes()


def unique(name, taken):
    stem, ext = os.path.splitext(name)
    candidate, n = name, 2
    while candidate.lower() in taken:
        candidate = "%s%d%s" % (stem, n, ext)
        n += 1
    taken.add(candidate.lower())
    return candidate


# ---- main -----------------------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description="Auto-rig single-joint dangling accessories into labelled copies.")
    parser.add_argument("mod_folder")
    parser.add_argument("scan_csv")
    parser.add_argument("output_folder")
    parser.add_argument("report_csv")
    parser.add_argument("--overrides", help="your own overrides.json (applied after known_overrides.json)")
    args = parser.parse_args()

    known = os.path.join(os.path.dirname(os.path.abspath(__file__)), "known_overrides.json")
    overrides = load_overrides([known, args.overrides])
    if args.overrides and not os.path.exists(args.overrides):
        raise SystemExit("overrides file not found: %s" % args.overrides)

    prepare_output(args.output_folder)
    files = index_files(args.mod_folder)
    taken = set(files[".model"]) | set(files[".menu"]) | set(files[".tex"])

    status = {}
    for row in csv.DictReader(open(args.scan_csv, encoding="utf-8-sig")):
        if row["Auto-rig candidate"]:
            model = row["Model"].lower()
            if status.get(model) != "auto-approved":
                status[model] = row["Auto-rig status"]
    for model, entry in overrides.items():
        if model in status and entry.get("approved"):
            status[model] = "auto-approved"

    # Menus in the accessory categories that use a candidate model, including colour variants.
    menus_by_model = {}
    for low, path in files[".menu"].items():
        try:
            menu = Menu(open(path, "rb").read())
        except Exception:  # noqa: BLE001
            continue
        if menu.category.lower() not in CATEGORIES:
            continue
        for cmd in menu.commands:
            if cmd[0].lower() == "additem" and len(cmd) > 1 and cmd[1].lower() in status:
                menus_by_model.setdefault(cmd[1].lower(), set()).add(low)

    report, rigged = [], {}
    for model, model_status in sorted(status.items()):
        entry = overrides.get(model, {})
        row = {"Model": model, "Status": model_status, "Overrides": json.dumps(entry, ensure_ascii=False) if entry else "",
               "Rigged model": "", "Dangles": "", "Joints": "", "Length (cm)": "", "Rigid pieces": "",
               "Bending pieces": "", "Menus": 0, "Result": ""}
        if entry.get("skip"):
            row["Result"] = "skipped: override"
            report.append(row)
            continue
        try:
            original = open(files[".model"][model], "rb").read()
            new_file = unique(model[:-6] + "_rig.model", taken)
            data, info = rig(original, os.path.splitext(new_file)[0], entry)
            problems = verify(original, data)
            if problems:
                raise ValueError("; ".join(problems))
            open(os.path.join(args.output_folder, new_file), "wb").write(data)
            rigged[model] = new_file
            row.update({"Rigged model": new_file, "Dangles": info["dangles"], "Joints": info["segments"],
                        "Length (cm)": info["length_cm"], "Rigid pieces": info["rigid_pieces"],
                        "Bending pieces": info["flexible_pieces"], "Result": "ok"})
        except Exception as ex:  # noqa: BLE001
            row["Result"] = "skipped: %s" % ex
        report.append(row)

    menu_count, icon_count = 0, 0
    written_icons = {}
    for row in report:
        model = row["Model"]
        if model not in rigged:
            continue
        label, banner, colour = LABELS[row["Status"]]
        for menu_low in sorted(menus_by_model.get(model, ())):
            menu = Menu(open(files[".menu"][menu_low], "rb").read())
            new_menu = unique(rig_stem(menu_low[:-5]) + ".menu", taken)
            menu.path = "assets/menu/menu/" + new_menu[:-5] + ".txt"
            menu.name = label_name(menu.name, label)
            for cmd in menu.commands:
                key = cmd[0].lower()
                if key == "name" and len(cmd) > 1:
                    cmd[1] = label_name(cmd[1], label)
                elif key == "additem" and len(cmd) > 1 and cmd[1].lower() in rigged:
                    cmd[1] = rigged[cmd[1].lower()]
                elif key == "icons" and len(cmd) > 1:
                    icon = cmd[1].lower()
                    cache_key = (icon, banner)
                    if cache_key not in written_icons:
                        written_icons[cache_key] = None
                        src = files[".tex"].get(icon)
                        if src:
                            labelled = label_icon(open(src, "rb").read(), banner, colour)
                            if labelled:
                                name = unique(icon[:-4] + ("_rig.tex" if banner == "RIG" else "_rigq.tex"), taken)
                                open(os.path.join(args.output_folder, name), "wb").write(labelled)
                                written_icons[cache_key] = name
                                icon_count += 1
                    if written_icons[cache_key]:
                        cmd[1] = written_icons[cache_key]
            open(os.path.join(args.output_folder, new_menu), "wb").write(menu.to_bytes())
            row["Menus"] += 1
            menu_count += 1

    with open(args.report_csv, "w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=list(report[0].keys()))
        writer.writeheader()
        writer.writerows(report)

    ok = [r for r in report if r["Result"] == "ok"]
    print("models: %d rigged, %d skipped | menus: %d | icons: %d | overrides applied: %d"
          % (len(ok), len(report) - len(ok), menu_count, icon_count, sum(1 for r in report if r["Overrides"])))
    for s in ("auto-approved", "needs review"):
        print("  %-14s %d models" % (s, sum(1 for r in ok if r["Status"] == s)))
    for r in report:
        if r["Result"] != "ok":
            print("  SKIPPED", r["Model"], "-", r["Result"])


main()
