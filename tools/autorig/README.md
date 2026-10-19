# Accessory auto-rigger

Offline tools that turn single-bone dangling accessories into multi-joint chains, so COM3D2's legacy
chain physics (and the plugin's Swing Strength / Taper options) can bend them like a real chain.

They never modify mod files: every rigged model is written as a new copy with its own menus and a
labelled icon, into a folder you choose (for example `Mod/_Custom_AutoRig`). Delete that folder to undo.

**This is an experimental, best-effort tool.** It rigs automatically from the model's bones and mesh
layout and cannot see what an accessory is meant to look like. Most items come out fine, some need a
small per-item fix (see [Fixing or skipping an item](#fixing-or-skipping-an-item)), and a few cannot be
auto-rigged well at all (see [Limitations](#limitations)).

Requires Python 3 and Pillow (`pip install pillow`). Windows paths are assumed for the icon font
(`C:/Windows/Fonts/arialbd.ttf`).

## 1. Scan

```powershell
python scan_accessories.py "C:\Games\COM3D2\Mod" accessory_physics_scan.csv
```

Reads every nipple, belly button, earring and front-hole menu (`accnip`, `accheso`, `accmimi`, `accvag`,
`accxxx`), classifies each model's physics rig and prints a summary. The CSV lists every model with:

- **Rig type**: rigid, chain with 1 joint, multi-joint chain, dynamic bone (`.phy`), skirt-style.
- **Auto-rig candidate**: a 1-joint `_yure_` chain at least 2 cm long.
- **Name matches** / **Auto-rig status**: candidates whose item name, description, menu or model file
  name contains a dangle word (chain, チェーン, チエーン, チーン, 鎖, tassel/タッセル, pendant, charm,
  ぶら下, 揺れ) are `auto-approved`; all other candidates are `needs review`.

Folders named `_Custom_SoftPhysics` and `_Custom_AutoRig` are skipped, so generated copies are never
rigged again.

## 2. Rig

```powershell
python run.py "C:\Games\COM3D2\Mod" accessory_physics_scan.csv "C:\Games\COM3D2\Mod\_Custom_AutoRig" autorig_report.csv --overrides overrides.json
```

`--overrides` is optional. For every candidate model:

1. Each single-joint dangle is split into 2–4 joints (about 1.2 cm apart) along its own axis. The root
   bone is set to the `_yure_soft_` preset; new bones are named `*_rigseg2`, `*_rigseg3`, … and carry no
   `_yure_` tag, so they inherit the root's preset.
2. Mesh weights on the dangle are redistributed by distance along it. Every separate mesh piece (chain
   link, bead, bell, gem) keeps a single weight profile taken at its centre, so it moves rigidly; a
   piece inside a larger rigid piece (a gem in a frame) moves with that piece. Only long, thin pieces
   (cords, one-piece chains) bend smoothly.
3. The result is verified: vertex, material and morph data byte-identical to the original, weights
   valid, non-physics weights unchanged, and every new bone's bind pose consistent with the hierarchy.
   Models that fail are skipped and listed.
4. Every menu that uses the model (all colour variants) is copied with `rig` inserted before the
   variant suffix (`item_z1_i_.menu` → `itemrig_z1_i_.menu`), pointing at the rigged model. The name
   gets （多関節揺れ） for auto-approved items or （多関節揺れ・要確認） for items to review, and the icon
   gets an orange **RIG** or purple **RIG?** banner. Icons stored as compressed textures or inside the
   game's own archives keep their original image.

`autorig_report.csv` lists joints, dangle length, rigid and bending piece counts, applied overrides and
the result for each model.

The output folder is cleared and rewritten on every run, so re-running after changing overrides never
leaves stale copies. To protect your files, the tool only clears a folder it created itself (it leaves an
`autorig_output.txt` marker there); it refuses to write into any other non-empty folder. Restart the
game after a run so the new items are picked up.

## Fixing or skipping an item

Check the purple **RIG?** items in-game. When one looks wrong, find its model file name in
`autorig_report.csv` (column `Model`) and add an entry to your own `overrides.json`, then run step 2
again with `--overrides overrides.json`:

```json
{
  "chain_piercing_nip.model": { "pivots_cm": [1.0, 2.2, 2.9] },
  "some_bell_pierce.model":   { "all_rigid": true },
  "tag_piercing.model":       { "joints": 2, "preset": "hard" },
  "clover_pierce.model":      { "approved": true },
  "broken_item.model":        { "skip": true },
  "three_tassels.model":      { "dangles": { "Bone_Ear2_yure_soft_": { "skip": true } } }
}
```

| Option | Effect | Use it when |
| --- | --- | --- |
| `"skip": true` | Don't rig this model (or, inside `dangles`, this one dangle). | It can't be fixed or you prefer the original. |
| `"joints": N` | Split into N joints (2–8), evenly spaced. | Too stiff (more joints) or too floppy / wobbly (fewer). |
| `"pivots_cm": [a, b, …]` | Put the bends exactly at these distances (cm) from the top of the dangle; gives one more joint than values listed. | A pendant hinges in the wrong place: put the last value where it actually hangs from the chain. |
| `"all_rigid": true` | Never bend any mesh piece; each piece moves whole. | Something solid (a bar, a long charm) bends. |
| `"preset": "soft" \| "hard" \| "keep"` | Physics preset of the dangle: soft (default), hard (stiffer), or keep the creator's. | Overall swing too loose or too tight. |
| `"approved": true` | Mark the item as checked: RIG banner and （多関節揺れ） label. | You checked it and it's fine. |
| `"dangles": { "<bone>": { … } }` | Apply `skip`, `joints`, `pivots_cm`, `all_rigid` or `preset` to one dangle of a multi-dangle model. Bone names are the `_yure_` bones of the model. | Only one of several tassels is wrong. |

Keys starting with `_` are ignored, so `"_comment": "…"` can hold notes. Model names may be written with
or without `.model`. Unknown options or wrong value types stop the run with a message saying what to fix.

Fixes for specific mods that are confirmed to work are collected in `known_overrides.json` next to the
tool and applied automatically; your own file is applied on top of it.

## Limitations

The tool cannot rig these well. For them, skipping the item (or leaving it out of the copy folder) is the
supported answer; the original mod always stays as its creator made it.

- **Accessories with no physics bones.** About half of all accessories are rigid by design; there is
  nothing to split. Rigging them needs bones added by hand in a 3D editor.
- **Dynamic-bone (`.phy`) and skirt-style rigs.** These use different physics systems and are left alone.
- **Dangles that are not straight.** Joints are placed along a straight line from the dangle's top to its
  end marker; a curved or looping chain bends at points that do not match its shape.
- **Several parts merged into one mesh piece.** A piece moves as one unit, so a chain and pendant
  modelled as a single piece either bends as a whole (if it is thin) or stays stiff.
- **Short dangles.** Below 2 cm nothing is rigged; just above it, two joints can look wobbly — use
  `"joints": 2` with `"preset": "hard"` or skip.
- **Models stored in the game's own archives** (not in the Mod folder) cannot be read and are not rigged.
- **Exact look in game.** The tool checks that every file is valid and that the item looks identical at
  rest, but not how it swings; that only shows in-game.

## Files

| File | Purpose |
| --- | --- |
| `cm3d2.py` | Readers/writers for `.model` (layout versions below 2001), `.menu` and `.tex` |
| `rig_model.py` | Re-rigging (with per-model options) and verification of one model |
| `run.py` | Batch: overrides, rig candidates, write menus and labelled icons, report |
| `scan_accessories.py` | Classify accessory models and mark candidates |
| `known_overrides.json` | Shared per-mod fixes applied automatically |
