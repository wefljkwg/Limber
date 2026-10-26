"""
Limber <- GVHMR: track a video with GVHMR (world-grounded 3D human motion) and save it as a Limber clip.

Runs inside GVHMR's own environment, from GVHMR's folder (run.sh takes care of both):
    python limber_gvhmr.py --video in.mp4 --out out.limberclip [--name NAME] [--static]

Prints "PROGRESS <0..1> <text>" lines for Limber's Video tab, and "ERROR <text>" on failure.
Everything stays on this PC.
"""
import argparse
import glob
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time

import numpy as np


def progress(fraction, text):
    print(f"PROGRESS {fraction:.3f} {text}", flush=True)


def fail(text):
    print(f"ERROR {text}", flush=True)
    sys.exit(1)


# ---- Limber's clip file (see Limber/Video/MotionClip.cs)

JOINTS = [
    "Hips", "Spine1", "Spine2", "Spine3", "Neck", "Head",
    "ShoulderL", "UpperArmL", "ElbowL", "WristL", "ShoulderR", "UpperArmR", "ElbowR", "WristR",
    "ThighL", "KneeL", "AnkleL", "FootL", "ToeL", "ThighR", "KneeR", "AnkleR", "FootR", "ToeR",
] + [f"{f}{s}{k}" for s in "LR" for f in ["Thumb", "Index", "Middle", "Ring", "Little"] for k in (1, 2, 3)] + [
    "EyeL", "EyeR",
    "DanP0", "DanP1", "DanP2", "DanP3", "DanP4", "DanP5", "DanP6", "TesticleL", "TesticleR",
    "BustRootL", "BustTipL", "BustRestL", "BustRootR", "BustTipR", "BustRestR",
]
J = {name: i for i, name in enumerate(JOINTS)}
assert len(JOINTS) == 71


def write_dotnet_string(f, s):
    data = s.encode("utf-8")
    n = len(data)
    while True:  # 7-bit encoded length, like .NET's BinaryWriter
        b = n & 0x7F
        n >>= 7
        f.write(bytes([b | (0x80 if n else 0)]))
        if not n:
            break
    f.write(data)


def save_clip(path, name, fps, positions, valid):
    """positions: (frames, 71, 3) float, valid: (frames, 71) bool. One person."""
    frames = positions.shape[0]
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    tmp = path + ".part"
    with open(tmp, "wb") as f:
        f.write(struct.pack("<I", 0x43424D4C))  # "LMBC"
        f.write(struct.pack("<i", 2))
        write_dotnet_string(f, name)
        f.write(struct.pack("<f", float(fps)))
        f.write(struct.pack("<i", 1))            # people
        f.write(struct.pack("<i", len(JOINTS)))
        f.write(struct.pack("<i", frames))
        f.write(struct.pack("<i", frames))       # frames in which the person was found
        for i in range(frames):
            for j in range(len(JOINTS)):
                p = positions[i, j]
                f.write(struct.pack("<?fff", bool(valid[i, j]), float(p[0]), float(p[1]), float(p[2])))
    os.replace(tmp, path)


# ---- SMPL points used (SMPL joint order, and SMPL mesh vertices for eyes and fingertips)

SMPL = dict(pelvis=0, hipL=1, hipR=2, spine1=3, kneeL=4, kneeR=5, spine2=6, ankleL=7, ankleR=8, spine3=9,
            footL=10, footR=11, neck=12, collarL=13, collarR=14, head=15, shoulderL=16, shoulderR=17,
            elbowL=18, elbowR=19, wristL=20, wristR=21, handL=22, handR=23)
VERTS = dict(eyeL=2800, eyeR=6260, thumbL=2746, indexL=2319, middleL=2445, ringL=2556, pinkyL=2673,
             thumbR=6191, indexR=5782, middleR=5905, ringR=6016, pinkyR=6133)


def run_gvhmr(video, workdir, static):
    """Runs GVHMR's demo until its results file is written, then stops it (the preview videos it renders
    afterwards aren't needed). Returns the results file."""
    cmd = [sys.executable, "tools/demo/demo.py", f"--video={video}", f"--output_root={workdir}"]
    if static:
        cmd.append("-s")
    progress(0.05, "Finding the person and their 2D pose (the first run also loads the models)...")
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)
    results = None
    log = []
    stages = [("bbx", 0.15, "Following the person..."), ("vitpose", 0.35, "Reading their 2D pose..."),
              ("vit_features", 0.55, "Reading body features..."), ("slam", 0.65, "Working out the camera..."),
              ("hmr4d", 0.8, "Solving the 3D motion...")]
    try:
        for line in proc.stdout:
            log.append(line.rstrip())
            low = line.lower()
            for key, frac, text in stages:
                if key in low:
                    progress(frac, text)
            found = glob.glob(os.path.join(workdir, "**", "hmr4d_results.pt"), recursive=True)
            if found:
                results = found[0]
                break
        if results is None:
            proc.wait()
            found = glob.glob(os.path.join(workdir, "**", "hmr4d_results.pt"), recursive=True)
            results = found[0] if found else None
    finally:
        if proc.poll() is None:
            # Let it finish writing the file (its size stops changing), then stop it.
            last = -1
            for _ in range(30):
                size = os.path.getsize(results) if results and os.path.exists(results) else -1
                if size > 0 and size == last:
                    break
                last = size
                time.sleep(1)
            proc.kill()
    if results is None:
        tail = " | ".join(l for l in log[-6:] if l.strip())
        fail("GVHMR didn't produce a result. Last output: " + tail[-600:])
    return results


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--name", default=None)
    ap.add_argument("--static", action="store_true", help="the camera doesn't move")
    a = ap.parse_args()

    if not os.path.isfile(a.video):
        fail("That video file doesn't exist (as seen from Linux): " + a.video)
    name = a.name or os.path.splitext(os.path.basename(a.video))[0]

    import cv2
    cap = cv2.VideoCapture(a.video)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    cap.release()
    if not (1 <= fps <= 240):
        fps = 30.0

    workdir = tempfile.mkdtemp(prefix="limber_gvhmr_")
    try:
        # GVHMR names its outputs after the video; give it a plain copy so odd file names can't trip it up.
        ext = os.path.splitext(a.video)[1] or ".mp4"
        local = os.path.join(workdir, "input" + ext)
        shutil.copyfile(a.video, local)
        results = run_gvhmr(local, os.path.join(workdir, "out"), a.static)

        progress(0.88, "Building the skeleton...")
        import torch
        sys.path.insert(0, os.getcwd())
        from hmr4d.utils.smplx_utils import make_smplx

        pred = torch.load(results, map_location="cpu")
        params = {k: v.float() for k, v in pred["smpl_params_global"].items()}
        device = "cuda" if torch.cuda.is_available() else "cpu"
        smplx = make_smplx("supermotion").to(device)
        smplx2smpl = torch.load("hmr4d/utils/body_model/smplx2smpl_sparse.pt").to(device)
        regressor = torch.load("hmr4d/utils/body_model/smpl_neutral_J_regressor.pt").to(device)

        frames = params["body_pose"].shape[0]
        verts_all, joints_all = [], []
        with torch.no_grad():
            for s in range(0, frames, 256):
                chunk = {k: v[s:s + 256].to(device) for k, v in params.items()}
                out = smplx(**chunk)
                verts = torch.stack([torch.matmul(smplx2smpl, v) for v in out.vertices])  # (n, 6890, 3)
                joints = torch.einsum("jv,lvi->lji", regressor, verts)                     # (n, 24, 3)
                verts_all.append(verts.cpu().numpy())
                joints_all.append(joints.cpu().numpy())
        verts = np.concatenate(verts_all)
        joints = np.concatenate(joints_all)

        progress(0.94, "Converting to Limber's format...")
        positions, valid = convert(joints, verts)
        save_clip(a.out, name, fps, positions, valid)
        progress(1.0, f"Done: {frames} frames.")
    except SystemExit:
        raise
    except Exception as e:  # noqa: BLE001 - report anything to Limber
        fail(f"{type(e).__name__}: {e}")
    finally:
        shutil.rmtree(workdir, ignore_errors=True)


def convert(joints, verts):
    """SMPL joints/vertices in GVHMR's world (Y up, metres) -> Limber's character space:
    +X = the character's right, +Y = up, +Z = forward, hips centred over the floor, feet on the floor."""
    joints = joints.astype(np.float64).copy()
    verts = verts.astype(np.float64).copy()
    n = joints.shape[0]

    # Upside down? (GVHMR's world is Y-up; this only guards against a flipped convention.)
    if np.mean(joints[:, SMPL["head"], 1] - joints[:, SMPL["pelvis"], 1]) < -0.2:
        joints[..., 1] *= -1
        verts[..., 1] *= -1

    # Turn about the vertical so the person faces +Z in the first frame (turns during the clip are kept).
    left = joints[0, SMPL["hipL"]] - joints[0, SMPL["hipR"]]
    forward = np.cross(left, [0.0, 1.0, 0.0])
    yaw = math.atan2(forward[0], forward[2])
    c, s = math.cos(-yaw), math.sin(-yaw)
    rot = np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
    joints = joints @ rot.T
    verts = verts @ rot.T

    # Floor: the soles' usual lowest point over the clip.
    floor = np.percentile(verts[:, :, 1].min(axis=1), 5)
    joints[..., 1] -= floor
    verts[..., 1] -= floor

    # Hips centred horizontally, like Limber's own tracker (no travelling around the room).
    centre = joints[:, SMPL["pelvis"], [0, 2]].copy()
    joints[..., 0] -= centre[:, None, 0]
    joints[..., 2] -= centre[:, None, 1]
    verts[..., 0] -= centre[:, None, 0]
    verts[..., 2] -= centre[:, None, 1]

    # SMPL's +X is the person's left; Limber's +X is their right.
    joints[..., 0] *= -1
    verts[..., 0] *= -1

    pos = np.zeros((n, len(JOINTS), 3))
    ok = np.zeros((n, len(JOINTS)), dtype=bool)

    def put(name, value):
        pos[:, J[name]] = value
        ok[:, J[name]] = True

    def jt(name):
        return joints[:, SMPL[name]]

    def vt(name):
        return verts[:, VERTS[name]]

    hip = (jt("hipL") + jt("hipR")) * 0.5
    sh = (jt("shoulderL") + jt("shoulderR")) * 0.5
    head = jt("head")
    # Same layout as Limber's video tracker, so retargeting treats both alike.
    put("Hips", hip + (sh - hip) * 0.08)
    put("Spine1", hip + (sh - hip) * 0.30)
    put("Spine2", hip + (sh - hip) * 0.55)
    put("Spine3", hip + (sh - hip) * 0.80)
    put("Neck", sh + (head - sh) * 0.25)
    put("Head", sh + (head - sh) * 0.80)
    put("EyeL", vt("eyeL"))
    put("EyeR", vt("eyeR"))

    for side in "LR":
        shoulder = jt("shoulder" + side)
        put("Shoulder" + side, sh + (shoulder - sh) * 0.3)
        put("UpperArm" + side, shoulder)
        put("Elbow" + side, jt("elbow" + side))
        put("Wrist" + side, jt("wrist" + side))
        put("Thigh" + side, jt("hip" + side))
        put("Knee" + side, jt("knee" + side))
        put("Ankle" + side, jt("ankle" + side))
        put("Foot" + side, jt("ankle" + side))
        put("Toe" + side, jt("foot" + side))
        # Palm only (GVHMR doesn't track fingers): first knuckles, so the hand turns with the wrist.
        hand, wrist = jt("hand" + side), jt("wrist" + side)
        put("Thumb" + side + "1", wrist + (vt("thumb" + side) - wrist) * 0.5)
        for limber, smpl in (("Index", "index"), ("Middle", "middle"), ("Ring", "ring"), ("Little", "pinky")):
            put(limber + side + "1", hand + (vt(smpl + side) - hand) * 0.25)

    ok &= np.isfinite(pos).all(axis=2)
    return pos.astype(np.float32), ok


if __name__ == "__main__":
    main()
