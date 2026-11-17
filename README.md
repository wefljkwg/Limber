[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/P4J329IONB)

# Limber

## Install

### FFXIV plugin
1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this link into the empty box at the bottom, click the **+** button next to it,
   tick **Enabled** on the new row, and click **Save and Close**:

       https://raw.githubusercontent.com/wefljkwg/Limber/main/repo.json

3. Type `/xlplugins`, search for **Limber**, and click **Install**.
4. Type `/limber1` to open it.

### Koikatsu plugin (only needed to mirror Koikatsu characters)
1. Close Koikatsu.
2. Download **Limber.Koikatsu.zip** from the latest release on this page (right side, under **Releases**).
3. Unzip it into your Koikatsu folder (the one with `Koikatu.exe` and `CharaStudio.exe`).
   It goes into `BepInEx\plugins\Limber`.

Koikatsu needs BepInEx and KKAPI, which any HF Patch or BetterRepack install already has.

### Updating from an older version
- If you used a dev build before, remove it: `/xlplugins` > **Dev Tools**, disable **Limber** / **KoiPoseLink**,
  then remove their paths in `/xlsettings` > **Experimental** > **Dev Plugin Locations**.
- In Koikatsu, delete the old `BepInEx\plugins\KoiPoseLink` folder if you have one.

## What Limber does

Pose, shape and animate characters in group pose. Everything happens on your screen only. Open it with `/limber1` (or `/lmb`).

### New in 0.69.0

- **Paired Animations** (Posing): line two characters up with each other (face to face, behind, side by side...), give each an animation (any race's version) and start them together. Save the pairing with each one's IK, attached bones, FK pose and Move character.
- **Stomach bulge** (Body shape): a ridge, peaked ridge or dome pushed out of the belly, with length, size, penis shape, creases, tightening and navel stretch, and presets. It can follow the partner's penis, growing and sinking as it moves, and a long one pushes the breasts apart.
- **Body shape**: the detail sliders now sit in the main groups and all work live, with no build. New Neck length, and Leg spacing moves the whole legs down to the feet. Sliders no longer drop off when an animation plays.
- **Body physics**: five sections (base to tip) for the breasts, butt and testicles, each with its own size and position, tilt, nipples up / down, hanging testicles, and colliders that press into the skin. A new **Physics collisions** page and an updated how-to guide.
- **Body swap** (Body): the character becomes another race, whole or body only, with a gender setting and skin tint.
- **Skin effects** (Scene): a wet sheen, a flush, wet eyes, and steam / musk rising off the body, the crotch, the chest, the armpits, the penis or the breath.
- **Sync** (Scene): its own page, and it now syncs whole poses both ways for everything, not only Koikatsu.
- **Always-on looks and presets**: give any player or NPC (a squadron member, say) a saved look and a global preset that goes on whenever they're around.
- **Mesh morphing** (Posing): reshape the face's own mesh with brushes on a face map.
- **Animations**: start frozen, play / pause everyone at once, and play another race's version of any animation (base animations too).
- **Move character**: set a character's place and rotation in the world, or move them from where they started.
- **Camera**: a free camera (WASD, E / Q).
- **Entity spawning and Costume** (Scene): any creature as an actor, or drawn in place of a character.
- **Extras**: walk around on the character select screen (with a following camera and actors), a flat-colour Backdrop, extra minions that follow you, and Limber Poses.
- **Everywhere**: presets and Reset to default on every page, an actor list window, pages that pop out into their own windows, and a layout that fits narrow windows.

### Posing

| Page | What it does |
| --- | --- |
| **IK Posing** | Drag the dots on a character and their arms and legs bend to follow. Right-click a dot to pin it in place, middle-click to let it go. |
| **FK Posing** | Turn each bone directly with rings or numbers. Add your turns on top of the animation, or hold the pose still. |
| **Symmetry** | Pose one side and the other side copies it as a mirror image, live. |
| **Rest pose** | Puts the whole body (or one bone) back in its plain, default stance. |
| **Face Posing** | Move, turn and resize each part of the face (brows, eyelids, eyes, lips, jaw and more) on a face map or right on the face. |
| **Look at** | The head and eyes follow another character, one of their bones, or the camera. Fine adjustments nudge where they look, tilt or turn the head on top, and turn or uncross the eyes, for when the look lands a little off. |
| **Hands** | Ready-made finger poses, such as a fist, an open hand or pointing. |
| **Bone scale** | Make any bone bigger or smaller, choosing whether the bones attached to it grow too. |
| **Attach** | Hold a bone onto another character, for hands on shoulders or holding hands. |
| **Move character** | Move, turn and resize a whole character, along the world's directions or the way they face, or set their place and rotation in the world directly. |
| **Paired Animations** | Line two characters up with each other and start an animation on each at the same moment (any race's version). Save pairings with each one's posing. |
| **Mesh morphing** | Reshape the face's own mesh with brushes (grab, push / pull, pinch, smooth) on a face map. |
| **Animations** | Play any of the game's animations, shown as a grid with icons. Change the speed, start frozen, pause or play everyone at once, and play another race's version of any animation. |
| **Actors** | Add extra characters to the scene, as copies of someone, wearing a saved look, or looking like any named character from the game. With "Also run outside GPose" on, you can add them in the open world too (only on your screen). |

### Body

| Page | What it does |
| --- | --- |
| **Body physics** | Soft, jiggling breasts, butt, testicles and belly that react to movement and touch. Press **Rig** once, then turn each part on or off. The soft belly has its own rig, and the breasts rest on it. The live rig works with no rigging and no redraw, and with separate wobbles each nearby character wobbles on their own. Pick which characters it affects, or leave it on everyone. |
| **Body shape** | Sliders for the body's shape, all shown live as you drag them (no build, no redraw), or built into the character's models. Includes a **Stomach bulge** that can follow the partner's penis. Pick which characters it affects, or leave it on everyone. |
| **Physics collisions** | What pushes each soft part (arms, hands, thighs, other characters, the floor, props) and how it gives way. |
| **Body swap** | The character becomes another race: the whole body, or the body only (keeping the head and face). |
| **Dynamic Bones** | Parts that sway and spring with the body, from stiff to loose. |
| **Limber Animations** | Animations Limber plays by itself: throbbing, an ahegao face, expansion (breasts, butt, penis, and a pregnant belly that grows round) and height growth or shrinking (from ant-sized up to a mountain-sized giant), each with many patterns. Tick the ones you want and start them all at once with **Start all selected**. |

### Scene

| Page | What it does |
| --- | --- |
| **Appearance & gear** | Change a character's race, face, hair, colours, body, outfit and weapons. It has colour pickers, hairstyle previews, saved looks, look-file import and a history of recent looks. Any saved look can go on a player or NPC by name, every time they're around. |
| **Sync** | Sync with another player: whole poses are sent both ways, so you both see the same thing, whatever moves the characters. |
| **Skin effects** | A wet sheen, a flush, wet eyes, and steam / musk rising off the body, fading in and out. |
| **Entity spawning / Costume** | Any creature from the game as an actor you can pose, or drawn in place of a character. |
| **Voice Switcher** | Give any character, each actor on its own, any player voice: from the game's list, another character's voice, or by number. |
| **Camera** | Set the zoom, angles, tilt and position. You can unlock its limits, let it pass through walls, fly a free camera and save camera shots. |
| **Timeline** | Make your own animations by setting keyframes for poses, movement and almost any setting, then playing them back. It can open in its own window. |
| **Devices** | Colliders (like hands or toys) that press the soft body, and motion devices that move bones on their own. |
| **Housing props / World props** | Browse the game's furniture and objects, preview them, and place them in your scene. World props can be sorted by area or by type. Attach any placed prop to a character's bone (a hand, the head, the hips...) so it moves with them. |
| **Effects** | Every visual effect in the game: spells, statuses, emotes, area markers, tethers and the effects placed in every zone (steam, smoke, torches...). Find them by kind (smoke, fire, sparkles, hearts...), by job, by place or by name. Place them in the scene, put them on a bone, or play them on a character so they bind to its hands and head. **Move it** shifts a character's effect to a new spot along the character's axes or the world's. Loop any effect. |
| **Renderer** | See every bone of a character, and mark which bones are soft body parts on bodies Limber doesn't know. |

### Koikatsu link

Needs the Limber Koikatsu plugin (see **Install** above).

| Feature | What it does |
| --- | --- |
| **Live posing** | Characters in Koikatsu pose and animate characters in FFXIV live: body, hands and fingers follow as you play. |
| **Channels and links** | Each Koikatsu character sends on its own channel. Link a channel to any actor, your current target, or your own character. One channel can drive several actors. |
| **Face** | Eyes, mouth, brows, blinking and gaze follow the Koikatsu face. Save in-game faces as presets for each Koikatsu expression. |
| **Smoothing** | Fills in frames between Koikatsu updates, so motion stays smooth at high frame rates. |
| **Pause / resume** | Freezes or restarts every Koikatsu animation from Limber (or `/limber1 pause`). |
| **Sync** | Two players see the same thing: the host's Koikatsu moves both characters on both screens. The other player doesn't need Koikatsu. |
| **Outside group pose** | "My character" links can also move your own character in the open world. |

### Handy extras

- **Undo / Redo**: takes back any change: a bone moved, a slider, a switch.
- **Freeze motion**: holds a character (or every actor) perfectly still.
- **Redraw**: draws a character again so changes to their look show up.
- **Pose files**: save a pose and load it back later.
- **Hide Limber window when UI is hidden**: an option in Advanced settings.
- **Ragdoll mode** (Extras): a character goes limp and falls where they stand; push them again while they're down.
- **Hide mounts** (Extras): riders' mounts aren't drawn, so they look like they're floating.
- **Extra minions** (Extras): up to 12 minions that follow you around.
- **Backdrop** (Extras): a flat colour all round the chosen character, for clean shots.
- **Character select** (Extras): walk around on the character select screen, with a camera that follows and actors you can add.
- **Global presets**: everything a character has, saved as one preset, put on anyone, or on a player or NPC every time they're around.
- **Presets and Reset to default**: on every page.
- **Actor list**: a small window listing every character, to choose, hide or show them.
- **Performance (stutters)** (Advanced): lists any part of Limber that took long enough in a frame to cause a stutter, to track down hitches.

## Planned features

Quality of life and bug fixes always come first.

- [x] Stomach / abdominal bulge (with shape)
- [x] Stomach / belly expansion and impregnation
- [ ] Mouth / throat bulge (with shape)
- [x] Vanilla voice switcher
- [ ] Test the sync player with partnered animations: custom animations, custom + IK adjusted, and Limber Animations
- [ ] Export Koikatsu animations directly from Limber, choosing which animations to export
- [ ] Export characters as they appear, fully rigged and ready to use in VRChat
- [ ] Client-side full body tracking that can record movements and export them to the Timeline
- [ ] Client-side full body tracking that the sync partner can see
- [ ] Improve the experimental VLC video to animation feature
- [ ] Bring Honey Select 2 into Limber the same way as Koikatsu
- [ ] Import minions, mounts, enemies and non-human NPCs with their animations intact and playable, and pose them
- [x] Attach housing props and world props to bones
- [x] Preset saving for every feature
