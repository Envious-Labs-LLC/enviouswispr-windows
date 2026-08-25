"""S1 diagnosis: where does the 3 s CPU time go?

Times preprocessor / encoder / decode-loop separately, measures the per-call
cost of the decoder_joint session, then sweeps ORT intra_op thread counts on
full recognize() runs to find the thread sweet spot (or prove threads are not
the issue).
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
print(f"clip: {n} samples = {n/16000:.2f} s")


def make_model(intra: int | None = None):
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    if intra:
        so.intra_op_num_threads = intra
        so.inter_op_num_threads = 1
    m = onnx_asr.load_model(
        "nemo-parakeet-tdt-0.6b-v3",
        path=str(MODEL_DIR),
        quantization="int8",
        sess_options=so,
        providers=["CPUExecutionProvider"],
    )
    return m


def find_asr(model):
    for v in vars(model).values():
        t = type(v).__name__
        if "Tdt" in t or "Rnnt" in t:
            return v
    # fallback: nested
    for v in vars(model).values():
        if hasattr(v, "__dict__"):
            for vv in vars(v).values():
                if "Tdt" in type(vv).__name__ or "Rnnt" in type(vv).__name__:
                    return vv
    raise SystemExit("could not find the asr core; adapter attrs: " + str(list(vars(model))))


model = make_model()
asr = find_asr(model)
print("asr core:", type(asr).__name__)

# --- preprocessor ---
t0 = time.perf_counter()
feats, flens = asr._preprocessor(wave[None, :].astype(np.float32), np.array([n], dtype=np.int64))
t_pre = time.perf_counter() - t0
print(f"preprocessor: {t_pre*1000:.1f} ms  feats={feats.shape} lens={flens}")

# --- encoder ---
t0 = time.perf_counter()
enc_raw, elens = asr._encoder.run(["outputs", "encoded_lengths"], {"audio_signal": feats, "length": flens})
enc = np.asarray(enc_raw).transpose(0, 2, 1)  # same as _encode
t_enc = time.perf_counter() - t0
print(f"encoder:      {t_enc*1000:.1f} ms  out shape={np.asarray(enc).shape} lens={elens}")

# --- decode loop (full) ---
t0 = time.perf_counter()
results = list(asr._decoding(np.asarray(enc), np.asarray(elens)))
t_dec = time.perf_counter() - t0
print(f"decode loop:  {t_dec*1000:.1f} ms  tokens={len(results[0][0]) if results else '?'}")

# --- single decoder call cost (100 reps of one frame step) ---
enc_arr = np.asarray(enc)[0]
prev_tokens = [asr._blank_idx]
state = asr._create_state()
t0 = time.perf_counter()
logits, step, new_state = asr._decode(prev_tokens, state, enc_arr[100])
for _ in range(100):
    logits, step, state = asr._decode(prev_tokens, state, enc_arr[100])
t1 = time.perf_counter() - t0
print(f"single decode call: {t1/100*1000:.3f} ms/call")
print(f"encoder frames in clip10: {int(elens[0])} -> decode-loop calls ~= {int(elens[0])}")

# --- thread sweep on full recognize ---
for intra in (None, 32, 16, 8, 4):
    m = make_model(intra)
    m.recognize(WAV)  # warmup
    times = []
    for _ in range(3):
        t0 = time.perf_counter()
        m.recognize(WAV)
        times.append(time.perf_counter() - t0)
    label = "default" if intra is None else str(intra)
    print(f"intra_op={label:>8}: median={statistics.median(times)*1000:.0f} ms")
