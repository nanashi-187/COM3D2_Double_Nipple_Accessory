# Accessory auto-rigger

Offline tools that turn single-bone dangling accessories into multi-joint chains, so COM3D2's legacy
chain physics (and the plugin's Swing Strength / Taper options) can bend them like a real chain.

They never modify mod files: every rigged model is written as a new copy with its own menus and a
labelled icon, into a folder you choose (for example `Mod/_Custom_AutoRig`). Delete that folder to undo.

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
python run.py "C:\Games\COM3D2\Mod" accessory_physics_scan.csv "C:\Games\COM3D2\Mod\_Custom_AutoRig" autorig_report.csv
```

For every candidate model:

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

`autorig_report.csv` lists joints, dangle length, rigid and bending piece counts and the result for
each model.

## Files

| File | Purpose |
| --- | --- |
| `cm3d2.py` | Readers/writers for `.model` (layout versions below 2001), `.menu` and `.tex` |
| `rig_model.py` | Re-rigging and verification of one model |
| `run.py` | Batch: rig candidates, write menus and labelled icons, report |
| `scan_accessories.py` | Classify accessory models and mark candidates |
