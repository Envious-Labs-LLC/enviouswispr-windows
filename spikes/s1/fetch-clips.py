"""Fetch LibriSpeech test-clean clips, resample to 16 kHz mono wav.

Keeps the clip closest to 10 s (clip10.wav) and closest to 20 s (clip20.wav).
16 kHz is what nemo128.onnx expects at the front end; onnx-asr would resample
anyway, but pinning the input removes one variable from the timing.
"""

import urllib.request
from pathlib import Path

import soundfile as sf
import numpy as np

audio = Path(__file__).parent / "audio"
audio.mkdir(exist_ok=True)

# whisper.cpp repo test clips — real human speech, stable raw URLs
# (openslr.org 404s on the old per-clip paths, MEASURED 2026-08-24)
CLIPS = [
    ("jfk.wav", "https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/jfk.wav"),          # ~11 s
    ("assorted.wav", "https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/assorted.wav"),  # ~15 s
    ("crazy_dude.wav", "https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/crazy_dude.wav"),  # ~40 s
]

def resample(x: np.ndarray, sr: int, target: int = 16000) -> np.ndarray:
    if sr == target:
        return x
    n = int(len(x) * target / sr)
    xp = np.linspace(0.0, len(x) - 1, num=len(x))
    xn = np.linspace(0.0, len(x) - 1, num=n)
    return np.interp(xn, xp, x).astype(np.float32)

cands = []
for name, url in CLIPS:
    dst = audio / name
    if not dst.exists():
        print(f"fetch {name}")
        urllib.request.urlretrieve(url, dst)
    data, sr = sf.read(dst, dtype="float32", always_2d=False)
    if data.ndim == 2:
        data = data.mean(axis=1)
    x = resample(data, sr)
    dur = len(x) / 16000.0
    cands.append((name, x, dur))
    print(f"{name}: sr={sr} dur={dur:.2f}s")

# Take the first N seconds of continuous-speech clips — no silence padding,
# so the frame count is real dictation-like speech. jfk (~11s) -> 10s,
# crazy_dude (~40s) -> 20s. Both leave the tiers timed on identical, known
# durations of genuine speech.
SOURCES = {10.0: "jfk.wav", 20.0: "crazy_dude.wav"}
by_name = {name: (x, dur) for name, x, dur in cands}

for target, out in ((10.0, "clip10.wav"), (20.0, "clip20.wav")):
    name = SOURCES[target]
    x, dur = by_name[name]
    n = int(target * 16000)
    if len(x) < n:
        raise SystemExit(f"{name} is only {dur:.1f}s, need {target}s")
    x = x[:n]
    dst = audio / out
    sf.write(dst, x, 16000)
    print(f"{out}: source={name} source_dur={dur:.2f}s -> trimmed to {target}s")
