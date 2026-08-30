"""S1: capture-independent ASR latency — Parakeet TDT 0.6B v3 int8 on Windows.

Measures the model leg of the capture->ASR->finalize pipeline (the part the
port owns): real founder dictation clips (10 s / 20 s, 16 kHz mono, the exact
fed.wav shapes the Mac product feeds its engine) through the same model
family the Mac ships (Parakeet TDT 0.6B v3, int8 tier) via ONNX Runtime.

Usage:
  python s1_latency.py --tier cpu
  python s1_latency.py --tier dml
  python s1_latency.py --tier dml --decoder fp32   # QDQ-free GPU comparison

Tiers:
  cpu : CPUExecutionProvider (i9-14900KF, 32 logical)
  dml : DmlExecutionProvider (RTX 4090) with CPU fallback

Reference points:
  Mac product PostHog medians: 0.61 s no-polish / 1.65 s on-device polish
  Mac backend claim: ~110x real-time on Apple Silicon
  onnx-asr published RTFx (Ryzen 9800X3D / RTX 5070 Ti): CPU int8 ~30.5,
  CUDA ~74-91 — sanity anchors only, not the target.
"""

import argparse
import json
import statistics
import time
from pathlib import Path

import onnx_asr
import onnxruntime as ort

HERE = Path(__file__).parent
MODEL_DIR = HERE / "models" / "parakeet-tdt-0.6b-v3"
AUDIO = HERE / "audio"
CLIPS = [("clip10.wav", 10.0), ("clip20.wav", 20.0)]
WARMUP = 2
RUNS = 7


def providers_for(tier: str):
    if tier == "cpu":
        return ["CPUExecutionProvider"]
    if tier == "dml":
        return ["DmlExecutionProvider", "CPUExecutionProvider"]
    if tier == "cuda":
        return ["CUDAExecutionProvider", "CPUExecutionProvider"]
    raise SystemExit(f"unknown tier {tier}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tier", required=True, choices=["cpu", "dml", "cuda"])
    # int8 = QDQ graphs (matches the Mac's shipped int8 tier; needs only the
    # files on disk). fp32 = unquantized encoder, requires the 2.3 GB
    # encoder-model.onnx.data which is not downloaded by default.
    ap.add_argument("--decoder", default="int8", choices=["int8", "fp32"])
    ap.add_argument("--runs", type=int, default=RUNS)
    args = ap.parse_args()
    quantization = "int8" if args.decoder == "int8" else None

    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL

    t0 = time.perf_counter()
    model = onnx_asr.load_model(
        "nemo-parakeet-tdt-0.6b-v3",
        path=str(MODEL_DIR),
        quantization=quantization,
        sess_options=so,
        providers=providers_for(args.tier),
    )
    load_s = time.perf_counter() - t0

    active = []
    try:
        # onnx-asr hides the sessions; report the tier we asked for instead
        active = providers_for(args.tier)
    except Exception:
        pass

    out = {
        "tier": args.tier,
        "decoder": args.decoder,
        "providers": active,
        "model_load_s": round(load_s, 2),
        "runs_per_clip": args.runs,
        "clips": [],
    }

    print(f"tier={args.tier} decoder={args.decoder} model_load={load_s:.1f}s")
    for name, dur in CLIPS:
        wav = str(AUDIO / name)
        for _ in range(WARMUP):
            model.recognize(wav)

        times = []
        text = ""
        for _ in range(args.runs):
            t0 = time.perf_counter()
            result = model.recognize(wav)
            dt = time.perf_counter() - t0
            times.append(dt)
            text = getattr(result, "text", str(result))

        med = statistics.median(times)
        entry = {
            "clip": name,
            "duration_s": dur,
            "median_s": round(med, 3),
            "min_s": round(min(times), 3),
            "max_s": round(max(times), 3),
            "rtfx": round(dur / med, 1),
            "text_head": text[:80],
        }
        out["clips"].append(entry)
        print(
            f"  {name}: median={med:.3f}s min={min(times):.3f}s "
            f"max={max(times):.3f}s RTFx={dur/med:.1f}x"
        )
        print(f"    text: {text[:80]!r}")

    dst = HERE / f"s1-results-{args.tier}.json"
    dst.write_text(json.dumps(out, indent=2))
    print(f"wrote {dst}")


if __name__ == "__main__":
    main()
