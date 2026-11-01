"""Limber <-> NLF (Neural Localizer Fields), for the VLC Live page: real-time 3D body tracking.

Runs inside WSL, started by Limber. Talks over stdin / stdout, one line per message:
  Limber -> here:  OPEN <path inside WSL>          open the video VLC is playing
                   FRAME <index>                   track that frame (frames are read here, from the file)
                   QUIT
  here -> Limber:  READY <model> <device>          loaded
                   OPENED <fps> <frames>           the video is open
                   POSE <index> <people> {<box centre x 0..1> <55 x (x y z)>}...
                                                   SMPL-X joints in metres, camera space (x right, y down, z away)
                   ERROR <text>
Nothing is sent anywhere else; it all stays on this PC.
"""
import os
import sys
import time

def say(*parts):
    sys.stdout.write(" ".join(str(p) for p in parts) + "\n")
    sys.stdout.flush()

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    model_file = os.environ.get("LIMBER_NLF_MODEL", os.path.join(here, "models", "nlf_l_multi_0.3.2.torchscript"))
    try:
        import cv2
        import numpy as np
        import torch
        import torchvision  # noqa: F401  (registers torchvision::nms and the other ops the NLF model uses)
    except Exception as e:  # noqa: BLE001
        say("ERROR NLF's Python environment is incomplete (" + str(e) + "). Run setup_nlf.bat again.")
        return 1
    if not os.path.isfile(model_file):
        say("ERROR The NLF model file is missing. Run setup_nlf.bat again.")
        return 1
    if not torch.cuda.is_available():
        say("ERROR No NVIDIA graphics card is available to NLF in WSL (update your NVIDIA driver).")
        return 1
    device = torch.device("cuda")
    model = torch.jit.load(model_file).to(device).eval()
    # The first runs are slow (TorchScript optimises the model on them): get them done before saying READY.
    try:
        dummy = torch.zeros((1, 3, 480, 640), dtype=torch.uint8, device=device)
        with torch.inference_mode(), torch.device("cuda"):
            for _ in range(3):
                model.detect_smpl_batched(dummy, model_name="smplx")
        torch.cuda.synchronize()
    except Exception as e:  # noqa: BLE001
        say("ERROR NLF couldn't run on the graphics card (" + str(e).replace("\n", " ")[:300] + ").")
        return 1
    say("READY", os.path.basename(model_file).replace(" ", "_"), torch.cuda.get_device_name(0).replace(" ", "_"))

    cap = None
    next_index = 0
    for raw in sys.stdin:
        line = raw.strip()
        if not line:
            continue
        cmd, _, arg = line.partition(" ")
        if cmd == "QUIT":
            break
        if cmd == "OPEN":
            if cap is not None:
                cap.release()
            cap = cv2.VideoCapture(arg)
            next_index = 0
            if not cap.isOpened():
                cap = None
                say("ERROR NLF couldn't open the video (" + arg + ").")
                continue
            say("OPENED", cap.get(cv2.CAP_PROP_FPS) or 30, int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0))
            continue
        if cmd != "FRAME" or cap is None:
            continue
        try:
            index = int(arg)
        except ValueError:
            continue
        # Read on from where it is (fast); seek only when it has to.
        if index < next_index or index > next_index + 60:
            cap.set(cv2.CAP_PROP_POS_FRAMES, index)
            next_index = index
        frame = None
        while next_index <= index:
            ok, frame = cap.read()
            next_index += 1
            if not ok:
                frame = None
                break
        if frame is None:
            say("POSE", index, 0)
            continue
        try:
            h, w = frame.shape[:2]
            # Big videos: smaller first (NLF looks at people at a much lower size anyway; joints are in metres, so
            # the size doesn't change them). Saves the copy to the graphics card and the person finder's time.
            if max(h, w) > 960:
                k = 960.0 / max(h, w)
                frame = cv2.resize(frame, (int(w * k), int(h * k)), interpolation=cv2.INTER_AREA)
                h, w = frame.shape[:2]
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            with torch.inference_mode(), torch.device("cuda"):
                image = torch.from_numpy(rgb).to(device).permute(2, 0, 1).unsqueeze(0)
                pred = model.detect_smpl_batched(image, model_name="smplx")
            joints = pred.get("joints3d")
            boxes = pred.get("boxes")
            if joints is None or len(joints) == 0:
                say("POSE", index, 0)
                continue
            j = joints[0].detach().float().cpu().numpy()  # people x J x 3
            b = boxes[0].detach().float().cpu().numpy() if boxes is not None and len(boxes) > 0 else None
            out = []
            people = 0
            for p in range(min(j.shape[0], 4)):
                pts = j[p, :55, :]
                if pts.shape[0] < 55 or not np.all(np.isfinite(pts)):
                    continue
                # Millimetres -> metres (the pelvis-to-neck distance tells which it is).
                torso = float(np.linalg.norm(pts[12] - pts[0]))
                if torso > 10.0:
                    pts = pts / 1000.0
                cx = 0.5
                if b is not None and p < b.shape[0]:
                    cx = float((b[p, 0] + b[p, 2] * 0.5) / max(w, 1)) if b.shape[1] >= 4 else 0.5
                out.append(f"{cx:.4f}")
                out.extend(f"{v:.4f}" for v in pts.reshape(-1))
                people += 1
            say("POSE", index, people, *out)
        except Exception as e:  # noqa: BLE001  (one bad frame: say why, carry on)
            say("ERROR NLF couldn't track frame " + str(index) + " (" + str(e).replace("\n", " ")[:300] + ").")
            say("POSE", index, 0)
    if cap is not None:
        cap.release()
    return 0

if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:  # noqa: BLE001
        say("ERROR NLF stopped: " + str(e).replace("\n", " "))
        sys.exit(1)
