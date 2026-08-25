"""S1 model download: Parakeet TDT 0.6B v3 ONNX (canonical istupakov pack).

Int8 encoder (self-contained QDQ graph) + int8 and fp32 decoder/joint + NeMo
log-mel preprocessor + vocab. Skips the 2.3 GB fp32 encoder weights — not
needed for S1 (int8 matches the shipped Mac tier; fp32 decoder covers the
GPU-tier comparison where QDQ fusion on DML may be poor).
"""

from pathlib import Path

from huggingface_hub import snapshot_download

dest = Path(__file__).parent / "models" / "parakeet-tdt-0.6b-v3"
snapshot_download(
    repo_id="istupakov/parakeet-tdt-0.6b-v3-onnx",
    local_dir=str(dest),
    allow_patterns=[
        "encoder-model.int8.onnx",
        "decoder_joint-model.int8.onnx",
        "decoder_joint-model.onnx",
        "nemo128.onnx",
        "vocab.txt",
        "config.json",
    ],
)
for p in sorted(dest.iterdir()):
    print(f"{p.name}: {p.stat().st_size / 1e6:.1f} MB")
print("DOWNLOAD COMPLETE")
