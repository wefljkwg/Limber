#!/usr/bin/env bash
# Limber -> GVHMR, inside WSL. Called by Limber's Video tab:  run.sh <windows video path> <windows clip path> [options]
ROOT="$HOME/limber-gvhmr"
if [ ! -f "$ROOT/miniconda3/etc/profile.d/conda.sh" ] || [ ! -d "$ROOT/GVHMR" ]; then
  echo "ERROR GVHMR isn't set up in WSL yet. Run setup_gvhmr.bat from Limber's GVHMR folder first."
  exit 1
fi
source "$ROOT/miniconda3/etc/profile.d/conda.sh"
conda activate gvhmr || { echo "ERROR The gvhmr Python environment is missing. Run setup_gvhmr.bat again."; exit 1; }
VIDEO="$(wslpath -u "$1")"
OUT="$(wslpath -u "$2")"
shift 2
cd "$ROOT/GVHMR" && exec python -u "$ROOT/limber_gvhmr.py" --video "$VIDEO" --out "$OUT" "$@"
