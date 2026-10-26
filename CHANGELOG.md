# Changelog

Newest first. Each version here is a release on GitHub; Dalamud shows the newest entry in the plugin installer.

## v0.59.4

- **Body physics guide:** a "How to use this (step by step)" button at the top of the Body physics page opens a guide
  window: what it does, what you need, setting it up, what each slider does, touching and grabbing, and what to do when
  it doesn't work.

## v0.59.3

- New plugin description in Dalamud's plugin list, covering what Limber does today.

## v0.59.2

First public release.

- **New layout:** a list of pages on the left (Posing, Body, Scene, Koikatsu, Settings) instead of rows of tabs, and a
  Getting started page with a checklist and shortcuts to each feature.
- **Bone scale:** make any bone bigger or smaller, with per-bone choices: whether the bones below it grow with it,
  whether they move with it, and whether it follows the size of the bones above it.
- **Move character:** "Take priority over other plugins (Brio)" keeps Limber's move / turn / scale on top of Brio's.
- **Body shape:** ankle and foot sliders (feet stay whole), Thigh spacing, and Leg spacing now moves the whole leg.
  Building the shape keeps the model's own shading and smooths out folded skin.
- **Body physics:** the live mesh starts again by itself after a redraw (such as a Body shape build).
- Works with YAS skeletons as well as IVCS.
