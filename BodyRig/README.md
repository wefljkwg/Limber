# Limber body rig: weighting models (Blender)

Step 1 (done in game, Rigging tab) added a lattice of extra bones to your skeleton through the "Limber Breast Rig"
Penumbra mod: a box 3 x 3 bones across and 2 layers deep around each breast (`lmb_lat_mune_*`) and each testicle
(`lmb_lat_kougan_*`), 18 per side. Place the box with the pink dots so it holds the whole part: back face at the
body, front dots at the front of it. This step weights the model to the lattice like a lattice deformer: every
vertex is weighted to the corners of the box cell it sits in, so the whole volume bends smoothly (no creases when it
swings or is scaled up). At rest the model looks exactly the same as before.

The script handles whatever is in the model: breast weights (`iv_c_mune_*` / `j_mune_*`) and testicle weights
(`iv_kougan_*` and `iv_kintama_phys_*`). Models already weighted with the old surface grid (`lmb_mune_*` /
`lmb_kougan_*`) can go straight in: those weights are folded back and redone on the lattice. Run it on each model you
want to move: the body top, outfit tops, and the testicle model (e.g. an equippable "junker" accessory model).

You need: Blender 3.6 or newer, and this folder's `limber_breast_weights.py`.

## 1. Back up the model
In Penumbra, find the body mod's top/chest model you actually use (for RUEXB+ with IVCS, one of the `exqb.mdl` /
`shy.mdl` / ... files under `IVCS/Nipple Options/...`). Copy that `.mdl` file somewhere safe first.

## 2. Export from Penumbra
1. Make sure the collection selected at the top of Penumbra is the one your character uses, with
   "Limber Breast Rig" enabled above your skeleton mod.
2. Open the body mod > Advanced Editing > Models, pick that `.mdl`, and click **Export to glTF**. Save the `.glb`.

## 3. Weight it in Blender
1. File > Import > glTF 2.0, pick the `.glb`.
2. Scripting tab > Open > `limber_breast_weights.py` > **Run Script**. A popup says how many vertices it changed.
   (Optional: in Weight Paint mode, look at the `lmb_lat_mune_*` vertex groups to see the spread.)
3. File > Export > glTF 2.0: format **.glb**, and under Data > Armature > Skinning set **Bone Influence to 8**.

## 4. Import back into Penumbra
Same Models page: tick **Keep current materials** and **Keep current attributes**, click **Import from glTF**,
pick the new `.glb`, then **Save**. Redraw your character: it should look exactly as before.

Notes
- Only models you run this on get the rig. Outfits with their own chest models keep their own weights; run the
  same steps on any outfit top you want to move too.
- If anything looks wrong, put the backed-up `.mdl` back (or disable the mod) and tell Limber's author what you saw.

## Blend shapes (Limber 0.30)

The Soft breasts / Soft testicles sections of the Rigging tab have **Blend shapes** sliders. They are sculpted
shapes that blend live from 0 to 1 (Ctrl+click a slider to type up to 3), mix with each other and with the Size /
Width / ... sliders, and the physics moves around the result.

- Testicles: Spherical, Oval, Melons, Clenched, Heavy, Tiny, Apart, Uneven.
- Breasts: Small, Large, Massive, Melons, Saggy, Droopy, Bigger nipples, Smaller nipples.

They work by moving the rig's lattice bones, so they reshape the whole surface of any model weighted to the rig.
Each one was sculpted per vertex, checked in renders, and turned into bone offsets in the box's own axes and size,
so it fits any body. The nipple shapes use a nipple bone per side. Re-rig the breasts with the one-click button
(boxes fitted) to get it.

Weighting changes in 0.30 (re-rig with the one-click button to get them):
- Each vertex uses the 4 corners of its lattice cell's tetrahedron, not all 8 trilinear corners. Most vertices also
  carry body weights, and FFXIV allows 8 weights in total, so the 8-corner version silently dropped corners. That
  caused dents whenever the box was reshaped.
- Where a part fades into the body, its share is evened out over the surface. Open edges, which are seams with the
  body, stay on the body. This stops puckering when the part is scaled.
- Testicles now grow from the top and back, where they hang from.
