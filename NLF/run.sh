#!/usr/bin/env bash
# Limber -> NLF, inside WSL. Started by Limber's VLC Live page; talks over stdin / stdout.
ROOT="$HOME/limber-nlf"
if [ ! -f "$ROOT/miniconda3/etc/profile.d/conda.sh" ] || [ ! -f "$ROOT/limber_nlf.py" ]; then
  echo "ERROR NLF isn't set up in WSL yet. Run setup_nlf.bat from Limber's NLF folder first."
  exit 1
fi
source "$ROOT/miniconda3/etc/profile.d/conda.sh"
conda activate nlf || { echo "ERROR The nlf Python environment is missing. Run setup_nlf.bat again."; exit 1; }
exec python -u "$ROOT/limber_nlf.py"
