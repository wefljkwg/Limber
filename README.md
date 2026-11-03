[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/P4J329IONB)

# Limber

## Install

### FFXIV plugin
1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this link, tick the box next to it, and click **Save and Close**:

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
