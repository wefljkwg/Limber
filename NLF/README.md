# NLF for Limber's VLC Live page

[NLF (Neural Localizer Fields)](https://github.com/isarandi/nlf) tracks the person in the video VLC is playing as a
full 3D body (with real depth, hands and feet), fast enough to follow the video live. It runs on your own PC, in WSL,
on an NVIDIA graphics card. Nothing is uploaded anywhere.

## What you need
- Windows 10 or 11 with WSL (Ubuntu): in an admin terminal, `wsl --install -d Ubuntu`, then restart.
- An NVIDIA graphics card with a current driver (CUDA works inside WSL with it). An RTX 3060 or better is recommended.
- About 6 GB of free disk space.

## Setting it up (once)
1. Double-click `setup_nlf.bat` in this folder. It installs Python, PyTorch and the NLF model into `~/limber-nlf`
   inside WSL (nothing else is touched). It asks for your Linux password once.
2. In Limber, open the VLC Live page and choose **NLF (accurate 3D)** as the tracker.

## Licence
NLF's code is MIT-licensed; its model is for **non-commercial research use**. Limber doesn't include it: the setup
downloads it to your PC for your own use.
