# GVHMR for Limber (optional, recorded videos only)

[GVHMR](https://github.com/zju3dv/GVHMR) fits a full 3D human body to a whole video at once. It's much more
accurate than Limber's built-in trackers for hard clips: people lying down, push-ups, turning, parts of the body
hidden. It's slow and too heavy for live VLC tracking, so it's only in the Video tab.

It runs on your PC only, inside WSL (Windows' built-in Linux), on an **NVIDIA** graphics card.
It needs about 20 GB of disk space.

## One-time setup

1. **WSL with Ubuntu.** In an admin terminal: `wsl --install -d Ubuntu`, restart, then open "Ubuntu" from the
   Start menu once to choose a Linux username and password. A current NVIDIA driver on Windows is all WSL needs
   for the graphics card.
2. **Body models.** These are free, but their licence means you have to download them yourself:
   - SMPL-X: sign up at <https://smpl-x.is.tue.mpg.de>, then download **SMPL-X v1.1 (NPZ+PKL)**.
   - SMPL: sign up at <https://smpl.is.tue.mpg.de>, then download **SMPL for Python, v1.1.0**.

   Put both zips together in one folder. There's no need to unzip them.
3. Double-click **`setup_gvhmr.bat`** in this folder and give it that folder. It installs Python, GVHMR and its
   model files (about 5.5 GB, from GVHMR's Hugging Face mirror) into `~/limber-gvhmr` inside WSL. It asks for
   your Linux password once. It takes a while.

## Using it

In Limber's Video tab, pick a video and click **Track with GVHMR**. The clip appears in the clip list like any
other one. Tick "The camera doesn't move" when it doesn't: that's faster and steadier.

GVHMR tracks the body, not the fingers. The hands still turn with the wrists.

The licences of GVHMR and its model files apply. They're for non-commercial use: see the GVHMR repository.
