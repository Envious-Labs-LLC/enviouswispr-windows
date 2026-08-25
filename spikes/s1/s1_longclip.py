"""S1 long-clip check: the 95 s dictation on the two viable tiers.

Questions answered:
  - Does the GPU (fp32, QDQ-free) scale linearly enough for 95 s dictations?
  - Where does tuned CPU land on a 95 s clip (the no-GPU worst case)?
"""

import statistics
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort
import onnx_asr

HERE = Path(__file__).parent
MODEL_DIR = HERE / "models" / "parakeet-tdt-0.6b-v3"
WAV = str(HERE / "audio" / "clip94.wav")


def make(providers, intra=None):
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    if intra:
        so.intra_op_num_threads = intra
        so.inter_op_num_threads = 1
    return onnx_asr.load_model(
        "nemo-parakeet-tdt-0.6b-v3",
        path=str(MODEL_DIR),
        quantization=None,  # fp32 (QDQ-free) — the only GPU-viable variant
        sess_options=so,
        providers=providers,
    )


for name, providers, intra in [
    ("cuda-fp32", ["CUDAExecutionProvider", "CPUExecutionProvider"], None),
    ("cpu-fp32", ["CPUExecutionProvider"], 8),
]:
    m = make(providers, intra)
    for _ in range(2):  # warmup
        m.recognize(WAV)
    ts = []
    for _ in range(3):
        t0 = time.perf_counter()
        r = m.recognize(WAV)
        ts.append(time.perf_counter() - t0)
    med = statistics.median(ts)
    print(f"{name:>10}: 95s clip median={med:.3f} s  RTFx={95.0/med:.1f}x  text={str(r)[:60]!r}")
