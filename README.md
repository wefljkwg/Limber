[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/P4J329IONB)

# Limber

## Install

### FFXIV plugin
1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this link into the empty box at the bottom, click the **+** button next to it,
   tick **Enabled** on the new row, and click **Save and Close**:

       https://raw.githubusercontent.com/wefljkwg/Limber/main/repo.json

3. Type `/xlplugins`, search for **Limber**, and click **Install**.
4. Type `/limber` to open it.

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

Pose, shape and animate characters in group pose. Everything happens on your screen only. Open it with `/limber` (or `/lmb`).

### Posing

| Page | What it does |
| --- | --- |
| **IK Posing** | Drag the dots on a character and their arms and legs bend to follow. Right-click a dot to pin it in place, middle-click to let it go. |
| **FK Posing** | Turn each bone directly with rings or numbers. Add your turns on top of the animation, or hold the pose still. |
| **Symmetry** | Pose one side and the other side copies it as a mirror image, live. |
| **Rest pose** | Puts the whole body (or one bone) back in its plain, default stance. |
| **Face Posing** | Move, turn and resize each part of the face (brows, eyelids, eyes, lips, jaw and more) on a face map or right on the face. |
| **Look at** | The head and eyes follow another character, one of their bones, or the camera. |
| **Hands** | Ready-made finger poses, such as a fist, an open hand or pointing. |
| **Bone scale** | Make any bone bigger or smaller, choosing whether the bones attached to it grow too. |
| **Attach** | Hold a bone onto another character, for hands on shoulders or holding hands. |
| **Move character** | Move, turn and resize a whole character, along the world's directions or the way they face. |
| **Animations** | Play any of the game's animations, shown as a grid with icons. You can change the speed too. |
| **Actors** | Add extra characters to the scene, as copies of someone, wearing a saved look, or looking like any named character from the game. |

### Body

| Page | What it does |
| --- | --- |
| **Body physics** | Soft, jiggling body parts that react to movement and touch. Press **Rig** once, then turn each part on or off. |
| **Body shape** | Sliders for the body's shape, shown live or built into the character's models. |
| **Dynamic Bones** | Parts that sway and spring with the body, from stiff to loose. |
| **Limber Animations** | Animations Limber plays by itself: throbbing, twerking, an ahegao face and expansion, each with many patterns. |
| **Oral** | A two-character mode with head and mouth movement. The mouth rig can be turned off. |

### Scene

| Page | What it does |
| --- | --- |
| **Appearance & gear** | Change a character's race, face, hair, colours, body, outfit and weapons. It has colour pickers, hairstyle previews, saved looks and look-file import. |
| **Camera** | Set the zoom, angles, tilt and position. You can unlock its limits, let it pass through walls and save camera shots. |
| **Timeline** | Make your own animations by setting keyframes for poses, movement and almost any setting, then playing them back. It can open in its own window. |
| **Devices** | Colliders (like hands or toys) that press the soft body, and motion devices that move bones on their own. |
| **Housing props / World props** | Browse the game's furniture and objects, preview them, and place them in your scene. World props can be sorted by area or by type. |
| **Renderer** | See every bone of a character, and mark which bones are soft body parts on bodies Limber doesn't know. |

### Handy extras

- **Undo / Redo**: takes back any change: a bone moved, a slider, a switch.
- **Freeze motion**: holds a character (or every actor) perfectly still.
- **Redraw**: draws a character again so changes to their look show up.
- **Pose files**: save a pose and load it back later.
- **Hide Limber window when UI is hidden**: an option in Advanced settings.
