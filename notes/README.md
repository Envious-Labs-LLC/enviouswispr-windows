# Notes

One file per topic. Append, do not rewrite history. Terse: bullets and tables, no preamble, no restating
the question.

**Every claim carries a label.** `MEASURED` (ran it, read the output — say what you ran) / `READ` (in the
source or docs — cite the path) / `ASSUMED` (reasoned, unchecked). Date every entry. A dead end is a
result: record it with the reason so nobody re-tries it.

Record the finding, not the journey. "X does not exist on Windows; the replacement is Y" beats three
paragraphs about how you looked.

Starting files, create as needed:

| File | Holds |
|---|---|
| `portability-map.md` | What ports as-is, what needs an equivalent, what is a rewrite |
| `api-equivalents.md` | Each Apple API to its Windows counterpart, or "none" |
| `language-options.md` | Swift vs C# vs Rust vs cross-platform, with costs and a recommendation |
| `toolchain.md` | What was installed on the rig and why |
| `open-questions.md` | Needs a founder decision |
| `dead-ends.md` | Tried, did not work, why |
