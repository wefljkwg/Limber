#!/usr/bin/env bash
# One-time NLF setup inside WSL (Ubuntu) for Limber's VLC Live page.
#   setup_nlf.sh <folder with limber_nlf.py + run.sh>
# Installs into ~/limber-nlf only. Needs an NVIDIA graphics card (CUDA works in WSL with a current driver).
set -euo pipefail
HERE="$1"
ROOT="$HOME/limber-nlf"
MODEL_URL="https://github.com/isarandi/nlf/releases/download/v0.3.2/nlf_l_multi_0.3.2.torchscript"

step() { echo; echo "=== $* ==="; }

step "1/4 System tools (asks for your Linux password)"
sudo apt-get update -y
sudo apt-get install -y wget libgl1 libglib2.0-0

mkdir -p "$ROOT/models"
cd "$ROOT"

step "2/4 Python (Miniconda, only inside $ROOT)"
if [ ! -x "$ROOT/miniconda3/bin/conda" ]; then
  wget -q --show-progress https://repo.anaconda.com/miniconda/Miniconda3-latest-Linux-x86_64.sh -O miniconda.sh
  bash miniconda.sh -b -p "$ROOT/miniconda3"
  rm miniconda.sh
fi
set +u
source "$ROOT/miniconda3/etc/profile.d/conda.sh"
if [ ! -d "$ROOT/miniconda3/envs/nlf" ]; then
  conda create -y -n nlf --override-channels -c conda-forge python=3.11
fi
conda activate nlf
set -u
pip install --upgrade pip
pip install torch==2.6.0 torchvision==0.21.0 --index-url https://download.pytorch.org/whl/cu124
pip install numpy opencv-python-headless

step "3/4 The NLF model (about 500 MB)"
[ -s "$ROOT/models/nlf_l_multi_0.3.2.torchscript" ] || wget -q --show-progress -c "$MODEL_URL" -O "$ROOT/models/nlf_l_multi_0.3.2.torchscript"

step "4/4 Limber's runner"
tr -d '\r' < "$HERE/limber_nlf.py" > "$ROOT/limber_nlf.py"
tr -d '\r' < "$HERE/run.sh" > "$ROOT/run.sh"
chmod +x "$ROOT/run.sh"
python -c "import torch; print('Graphics card for NLF:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'NONE - it will not work without CUDA')"

echo
echo "NLF is ready. On Limber's VLC Live page, choose \"NLF (accurate 3D)\" as the tracker."
