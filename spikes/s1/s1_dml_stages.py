"""DML vs CPU per-stage comparison (run when the GPU is idle).

Isolates which piece is pathological on DML:
  - encoder single call (the 652 MB int8 QDQ graph)
  - one decoder_joint step via asr._decode (per-frame loop member)
"""

import statistics
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort
import soundfile as sf
import onnx_asr

HERE = Path(__file__).parent
MODEL_DIR = HERE / "models" / "parakeet-tdt-0.6b-v3"
WAV = str(HERE / "audio" / "clip10.wav")

wave, _ = sf.read(WAV, dtype="float32")
n = len(wave)


def make(providers):
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.intra_op_num_threads = 8
    so.inter_op_num_threads = 1
    return onnx_asr.load_model(
        "nemo-parakeet-tdt-0.6b-v3",
        path=str(MODEL_DIR),
        quantization="int8",
        sess_options=so,
        providers=providers,
    )


def find_asr(model):
    for v in vars(model).values():
        t = type(v).__name__
        if "Tdt" in t or "Rnnt" in t:
            return v
    for v in vars(model).values():
        if hasattr(v, "__dict__"):
            for vv in vars(v).values():
                if "Tdt" in type(vv).__name__ or "Rnnt" in type(vv).__name__:
                    return vv
    raise SystemExit("could not find the asr core")


def timed(fn, n=5):
    fn()  # warmup
    ts = []
    for _ in range(n):
        t0 = time.perf_counter()
        fn()
        ts.append(time.perf_counter() - t0)
    return statistics.median(ts)


for tier, providers in [
    ("dml", ["DmlExecutionProvider", "CPUExecutionProvider"]),
    ("cpu", ["CPUExecutionProvider"]),
]:
    m = make(providers)
    asr = find_asr(m)
    feats, flens = asr._preprocessor(wave[None, :].astype(np.float32), np.array([n], dtype=np.int64))
    enc_raw, elens = asr._encoder.run(["outputs", "encoded_lengths"], {"audio_signal": feats, "length": flens})
    enc = np.asarray(enc_raw).transpose(0, 2, 1)

    t_enc = timed(lambda: asr._encoder.run(["outputs", "encoded_lengths"], {"audio_signal": feats, "length": flens}))

    prev_tokens = [asr._blank_idx]
    state = asr._create_state()
    frame = enc[0][100]
    logits, step, new_state = asr._decode(prev_tokens, state, frame)
    state = new_state
    t_dec = timed(lambda: asr._decode(prev_tokens, state, frame))

    print(
        f"{tier:>4}: encoder(10s)={t_enc*1000:8.0f} ms | decoder step={t_dec*1000:6.3f} ms/call "
        f"(x{int(elens[0])} calls = {t_dec*elens[0]*1000:.0f} ms)"
    )
