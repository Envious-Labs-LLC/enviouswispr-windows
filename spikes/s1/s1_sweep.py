"""S1 thread sweep refinement: find the sweet spot for the 14900KF (8P+16E).

Default (all 32 logical) was catastrophic (4.2 s). Sweep candidate thread
counts on both clips with stage breakdown at the winner.
"""

import os
import statistics
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort
import soundfile as sf
import onnx_asr

HERE = Path(__file__).parent
MODEL_DIR = HERE / "models" / "parakeet-tdt-0.6b-v3"
CLIPS = [("clip10.wav", 10.0), ("clip20.wav", 20.0)]


def make_model(intra: int):
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.intra_op_num_threads = intra
    so.inter_op_num_threads = 1
    return onnx_asr.load_model(
        "nemo-parakeet-tdt-0.6b-v3",
        path=str(MODEL_DIR),
        quantization="int8",
        sess_options=so,
        providers=["CPUExecutionProvider"],
    )


def timed(m, wav: str, n: int = 5):
    m.recognize(wav)
    ts = []
    for _ in range(n):
        t0 = time.perf_counter()
        m.recognize(wav)
        ts.append(time.perf_counter() - t0)
    return statistics.median(ts)


best = None
if os.environ.get("S1_BEST"):
    best = (int(os.environ["S1_BEST"]), 0.0, 0.0)
else:
    for intra in (6, 8, 10, 12, 14, 16, 20, 24):
        m = make_model(intra)
        r10 = timed(m, str(HERE / "audio" / "clip10.wav"), 3)
        r20 = timed(m, str(HERE / "audio" / "clip20.wav"), 3)
        print(f"intra_op={intra:>2}: 10s={r10*1000:6.0f} ms  20s={r20*1000:6.0f} ms")
        if best is None or r10 < best[1]:
            best = (intra, r10, r20)

print(f"\nstage breakdown at intra_op={best[0]}")

# stage breakdown at the best setting
m = make_model(best[0])
for name, dur in CLIPS:
    wav = str(HERE / "audio" / name)
    m.recognize(wav)
    # find asr core
    asr = next(
        (v for v in vars(m).values() if "Tdt" in type(v).__name__ or "Rnnt" in type(v).__name__),
        None,
    )
    if asr is None:
        asr = next(
            (vv for v in vars(m).values() for vv in vars(v).values()
             if "Tdt" in type(vv).__name__ or "Rnnt" in type(vv).__name__),
            None,
        )
    wave, _ = sf.read(wav, dtype="float32")
    n = len(wave)
    t0 = time.perf_counter()
    feats, flens = asr._preprocessor(wave[None, :].astype(np.float32), np.array([n], dtype=np.int64))
    t_pre = time.perf_counter() - t0
    t0 = time.perf_counter()
    enc_raw, elens = asr._encoder.run(["outputs", "encoded_lengths"], {"audio_signal": feats, "length": flens})
    t_enc = time.perf_counter() - t0
    enc = np.asarray(enc_raw).transpose(0, 2, 1)
    t0 = time.perf_counter()
    res = list(asr._decoding(enc, np.asarray(elens)))  # yields (tokens, timestamps, logprobs)
    t_dec = time.perf_counter() - t0
    tot = t_pre + t_enc + t_dec
    print(
        f"{name} @ intra={best[0]}: pre={t_pre*1000:5.0f} ms enc={t_enc*1000:5.0f} ms "
        f"dec={t_dec*1000:5.0f} ms total={tot*1000:5.0f} ms frames={int(elens[0])} "
        f"tokens={len(res[0][0])}"
    )
