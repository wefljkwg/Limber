#!/usr/bin/env bash
# One-time GVHMR setup inside WSL (Ubuntu) for Limber's Video tab.
#   setup_gvhmr.sh <folder with limber_gvhmr.py + run.sh> <folder with the SMPL / SMPL-X downloads>
# Installs into ~/limber-gvhmr only. Needs an NVIDIA graphics card (CUDA works in WSL with a current driver).
set -euo pipefail
HERE="$1"
BODY="$2"
ROOT="$HOME/limber-gvhmr"
CK="$ROOT/GVHMR/inputs/checkpoints"
HF="https://huggingface.co/camenduru/GVHMR/resolve/main"

step() { echo; echo "=== $* ==="; }

step "1/6 System tools (asks for your Linux password)"
sudo apt-get update -y
sudo apt-get install -y git wget unzip ffmpeg build-essential libgl1 libglib2.0-0

mkdir -p "$ROOT"
cd "$ROOT"

step "2/6 Python (Miniconda, only inside $ROOT)"
if [ ! -x "$ROOT/miniconda3/bin/conda" ]; then
  wget -q --show-progress https://repo.anaconda.com/miniconda/Miniconda3-latest-Linux-x86_64.sh -O miniconda.sh
  bash miniconda.sh -b -p "$ROOT/miniconda3"
  rm miniconda.sh
fi
set +u  # conda's shell scripts use unset variables
source "$ROOT/miniconda3/etc/profile.d/conda.sh"
if [ ! -d "$ROOT/miniconda3/envs/gvhmr" ]; then  # (a grep pipeline under pipefail could misfire and recreate it)
  conda create -y -n gvhmr --override-channels -c conda-forge python=3.10
fi
conda activate gvhmr
set -u

step "3/6 GVHMR"
if [ ! -d "$ROOT/GVHMR/.git" ]; then
  git clone --recursive https://github.com/zju3dv/GVHMR "$ROOT/GVHMR"
fi
cd "$ROOT/GVHMR"
pip install --upgrade pip
pip install cython "numpy==1.23.5"
pip install --no-build-isolation chumpy cython_bbox
pip install -r requirements.txt
pip install -e .

step "4/6 Model files (about 5.5 GB)"
mkdir -p "$CK/gvhmr" "$CK/hmr2" "$CK/vitpose" "$CK/yolo" "$CK/body_models/smpl" "$CK/body_models/smplx"
get() { [ -s "$2" ] || wget -q --show-progress -c "$HF/$1" -O "$2"; }
get "gvhmr/gvhmr_siga24_release.ckpt" "$CK/gvhmr/gvhmr_siga24_release.ckpt"
get "hmr2/epoch%3D10-step%3D25000.ckpt" "$CK/hmr2/epoch=10-step=25000.ckpt"
get "vitpose/vitpose-h-multi-coco.pth" "$CK/vitpose/vitpose-h-multi-coco.pth"
get "yolo/yolov8x.pt" "$CK/yolo/yolov8x.pt"

step "5/6 Body models (SMPL / SMPL-X, from your own downloads)"
TMP="$(mktemp -d)"
for z in "$BODY"/*.zip; do [ -e "$z" ] && unzip -q -o "$z" -d "$TMP"; done
cp -n "$BODY"/* "$TMP"/ 2>/dev/null || true
SMPLX="$(find "$TMP" -iname 'SMPLX_NEUTRAL.npz' | head -n1)"
SMPLN="$(find "$TMP" \( -iname 'SMPL_NEUTRAL.pkl' -o -iname 'basicmodel_neutral_lbs_10_207_0_v1.1.0.pkl' -o -iname 'basicModel_neutral_lbs_10_207_0_v1.0.0.pkl' \) | head -n1)"
if [ -z "$SMPLX" ] || [ -z "$SMPLN" ]; then
  echo "Couldn't find SMPLX_NEUTRAL.npz and the neutral SMPL .pkl in: $BODY"
  echo "See README.md in Limber's GVHMR folder for where to get them, then run setup again."
  exit 1
fi
cp "$SMPLX" "$CK/body_models/smplx/SMPLX_NEUTRAL.npz"
cp "$SMPLN" "$CK/body_models/smpl/SMPL_NEUTRAL.pkl"
for g in MALE FEMALE; do
  f="$(find "$TMP" -iname "SMPLX_${g}.npz" | head -n1)"; [ -n "$f" ] && cp "$f" "$CK/body_models/smplx/"
done
rm -rf "$TMP"

step "6/6 Limber's runner"
tr -d '\r' < "$HERE/limber_gvhmr.py" > "$ROOT/limber_gvhmr.py"
tr -d '\r' < "$HERE/run.sh" > "$ROOT/run.sh"
chmod +x "$ROOT/run.sh"
python -c "import torch; print('Graphics card for GVHMR:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'NONE - it will not work without CUDA')"

echo
echo "GVHMR is ready. Use \"Track with GVHMR\" in Limber's Video tab."
