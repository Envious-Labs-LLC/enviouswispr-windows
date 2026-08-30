"""DML failure diagnosis: capture ORT logs to see what falls back to CPU.

If the int8 QDQ graph isn't fusable on DML, every QuantizeLinear/
DequantizeLinear pair (and possibly whole subgraphs) bounces to the CPU EP —
which would explain RTFx < 1 on a 24 GB card.
"""

import io
from contextlib import redirect_stderr
from pathlib import Path

import onnxruntime as ort

HERE = Path(__file__).parent
MODEL_DIR = HERE / "models" / "parakeet-tdt-0.6b-v3"
ENCODER = MODEL_DIR / "encoder-model.int8.onnx"
DECODER = MODEL_DIR / "decoder_joint-model.int8.onnx"

so = ort.SessionOptions()
so.log_severity_level = 1  # INFO: placement/fallback notices
so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL

# ORT Python logs go to stderr
buf = io.StringIO()
with redirect_stderr(buf):
    try:
        enc = ort.InferenceSession(
            str(ENCODER), so, providers=["DmlExecutionProvider", "CPUExecutionProvider"]
        )
        dec = ort.InferenceSession(
            str(DECODER), so, providers=["DmlExecutionProvider", "CPUExecutionProvider"]
        )
        print("ENCODER providers:", enc.get_providers())
        print("DECODER providers:", dec.get_providers())
    except Exception as e:
        print(f"SESSION CREATE FAILED: {e!r}")

logs = buf.getvalue()
lines = [l for l in logs.splitlines() if l.strip()]
print(f"--- {len(lines)} log lines ---")
# print lines mentioning fallback/CPU/graph/DML placement
keys = ("fallback", "CPU", "graph", "Dml", "not supported", "QDQ", "quant")
shown = 0
for l in lines:
    if any(k.lower() in l.lower() for k in keys):
        print(l[:300])
        shown += 1
        if shown > 60:
            break
print(f"(shown {shown} of {len(lines)})")
