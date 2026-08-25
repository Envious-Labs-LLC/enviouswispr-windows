"""S1 test clips: the founder's real dictation, from C:\\Users\\saura\\audio-samples.

Each sample dir holds raw.wav (full capture), fed.wav (what the engine received),
and meta.json. fed.wav is the honest S1 input — it is what the production
pipeline feeds the ASR engine, post-VAD-trim. 16 kHz mono PCM_16, exactly the
nemo128 front-end format, so no resampling is introduced.

Picks (both classified asr_complete on the Mac, i.e. clean runs):
  clip10.wav <- 76F98C4A-E639-48A8-B32D-4E33F928498B  (10.05 s -> 10.0 s)
  clip20.wav <- 68B90FDC-1478-497D-AEDF-86B7EB7B5FA1  (21.21 s -> 20.0 s)
"""

import pathlib
import shutil

import soundfile as sf

SRC = pathlib.Path("C:/Users/saura/audio-samples")
DST = pathlib.Path(__file__).parent / "audio"
DST.mkdir(exist_ok=True)

PICKS = [
    ("76F98C4A-E639-48A8-B32D-4E33F928498B", 10.0, "clip10.wav"),
    ("68B90FDC-1478-497D-AEDF-86B7EB7B5FA1", 20.0, "clip20.wav"),
]

for sid, target, out in PICKS:
    src = SRC / sid / "fed.wav"
    x, sr = sf.read(src, dtype="float32")
    assert sr == 16000, f"expected 16 kHz, got {sr}"
    assert x.ndim == 1, "expected mono"
    src_dur = len(x) / sr
    n = int(target * sr)
    assert len(x) > n, f"{sid} is only {src_dur:.1f}s, need {target}s"
    x = x[:n]
    dst = DST / out
    sf.write(dst, x, sr)
    print(f"{out}: {sid} fed.wav src={src_dur:.2f}s -> trimmed to {target}s")
    meta = (SRC / sid / "meta.json").read_text()
    (DST / f"{out}.meta.json").write_text(meta)
