"""S1 corpus quality + engine comparison over the full 453-clip set.

Engines (run sequentially, clean per-engine timing):
  onnx-int8 : onnx-asr + istupakov int8 QDQ pack (CPU, intra_op=8)  [S1 tier]
  onnx-fp32 : onnx-asr + istupakov fp32 pack    (CPU, intra_op=8)  [quality ref]
  sherpa    : sherpa-onnx official v3 int8 model (CPU, 8 threads)  [challenger]

Output: corpus-results/<engine>.json  { clip_id: {text, ms, src} }
Then s1_corpus_diff.py compares texts pairwise.

Usage:
  python s1_corpus.py --engines onnx-int8,onnx-fp32
  python s1_corpus.py --engines sherpa
"""

import argparse
import json
import statistics
import time
from pathlib import Path

import soundfile as sf

HERE = Path(__file__).parent
SAMPLES = Path(r"C:\Users\saura\audio-samples")
OUT_DIR = HERE / "corpus-results"
MODEL_DIR = HERE / "models" / "parakeet-tdt-0.6b-v3"
SHERPA_DIR = HERE / "models" / "sherpa-parakeet-tdt-v3-int8"


def clips():
    for d in sorted(SAMPLES.iterdir()):
        if not d.is_dir():
            continue
        fed = d / "fed.wav"
        raw = d / "raw.wav"
        wav = fed if fed.exists() else raw
        if wav.exists():
            yield d.name, wav, fed.exists()


def make_onnx(quantization):
    import numpy as np
    import onnxruntime as ort
    import onnx_asr

    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.intra_op_num_threads = 8
    so.inter_op_num_threads = 1
    return onnx_asr.load_model(
        "nemo-parakeet-tdt-0.6b-v3",
        path=str(MODEL_DIR),
        quantization=quantization,
        sess_options=so,
        providers=["CPUExecutionProvider"],
    )


def run_onnx(quantization, items):
    model = make_onnx(quantization)
    out = {}
    for clip_id, wav, is_fed in items:
        wave, sr = sf.read(wav, dtype="float32")
        assert sr == 16000, f"{clip_id}: sr={sr}"
        t0 = time.perf_counter()
        text = model.recognize(str(wav))
        ms = (time.perf_counter() - t0) * 1000
        out[clip_id] = {"text": str(text), "ms": round(ms, 1), "src": "fed" if is_fed else "raw"}
        if len(out) % 50 == 0:
            print(f"  {len(out)} clips done", flush=True)
    return out


def make_sherpa():
    import sherpa_onnx as s

    base = SHERPA_DIR
    if not (base / "encoder.int8.onnx").exists():
        raise SystemExit(f"sherpa model missing at {base} (download the tarball first)")
    # sherpa-onnx 1.13.x: constructor is gone; use the from_transducer factory.
    return s.OfflineRecognizer.from_transducer(
        tokens=str(base / "tokens.txt"),
        encoder=str(base / "encoder.int8.onnx"),
        decoder=str(base / "decoder.int8.onnx"),
        joiner=str(base / "joiner.int8.onnx"),
        num_threads=8,
        decoding_method="greedy_search",
    )


def run_sherpa(items):
    rec = make_sherpa()
    import numpy as np

    out = {}
    for clip_id, wav, is_fed in items:
        wave, sr = sf.read(wav, dtype="float32")
        assert sr == 16000
        stream = rec.create_stream()
        stream.accept_waveform(sr, wave)
        t0 = time.perf_counter()
        rec.decode_stream(stream)
        ms = (time.perf_counter() - t0) * 1000
        out[clip_id] = {"text": stream.result.text.strip(), "ms": round(ms, 1), "src": "fed" if is_fed else "raw"}
        if len(out) % 50 == 0:
            print(f"  {len(out)} clips done", flush=True)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engines", required=True, help="comma list: onnx-int8,onnx-fp32,sherpa")
    args = ap.parse_args()
    engines = [e.strip() for e in args.engines.split(",")]
    OUT_DIR.mkdir(exist_ok=True)

    items = list(clips())
    print(f"{len(items)} clips")
    for name in engines:
        t0 = time.perf_counter()
        if name == "onnx-int8":
            out = run_onnx("int8", items)
        elif name == "onnx-fp32":
            out = run_onnx(None, items)
        elif name == "sherpa":
            out = run_sherpa(items)
        else:
            raise SystemExit(f"unknown engine {name}")
        wall = time.perf_counter() - t0
        ms = [v["ms"] for v in out.values()]
        path = OUT_DIR / f"{name}.json"
        path.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
        empties = [k for k, v in out.items() if not v["text"].strip()]
        print(
            f"{name}: {len(out)} clips in {wall:.0f}s  median {statistics.median(ms):.0f} ms  "
            f"p95 {sorted(ms)[int(0.95*len(ms))]:.0f} ms  empty={len(empties)} {empties[:5]}",
            flush=True,
        )


if __name__ == "__main__":
    main()
