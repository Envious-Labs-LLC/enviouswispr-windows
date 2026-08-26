# Dictation pipeline contract

## Final text order

The order is part of the product behavior and must not drift casually:

1. Capture audio and freeze the target app when recording begins.
2. Run the selected final ASR engine.
3. Apply custom-word correction.
4. Remove configured filler words and false starts.
5. Convert spoken punctuation and spoken emoji commands.
6. Apply deterministic inverse text normalization for numbers, dates, times, currency, email, and URLs.
7. Optionally polish through EG-1, Ollama, or the selected cloud provider.
8. Restore protected emoji and deterministic tokens that the model was not allowed to alter.
9. Apply cursor-aware insertion repair when safe context is available.
10. Deliver to the frozen target and record the local history result according to user settings.

Every stage has a typed input, typed output, timeout or cancellation policy, and content-free diagnostic.
If a stage fails, return the last valid text. Optional polish failure returns deterministic text.

## Live preview

Preview consumes audio snapshots through a separate small multilingual Whisper engine. It may revise its
own display, but its text never enters final processing, history, analytics, or delivery. Stop and release
preview resources before final ASR begins. Preview failure must not fail recording.

## Session behavior

- Press or hold starts one session. Release stops capture and begins final processing.
- A second activation cannot create overlapping capture or duplicate paste.
- Escape cancels safely and delivers nothing.
- No-speech and empty-output paths are normal outcomes with clear, quiet feedback.
- Device removal, model failure, or accelerator failure preserves captured audio long enough for a safe
  fallback when possible.
- Focus changes during recording do not silently redirect private text to an unintended window.

## Deterministic parity

The Windows deterministic corpus is ported from macOS behavior as platform-neutral fixtures, including
emoji, punctuation, casing, custom words, numbers, dates, currency, URLs, and failure cases. Any intended
platform difference is documented beside the fixture rather than hidden in implementation code.
