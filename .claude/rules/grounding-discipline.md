# Grounding discipline

Never answer from memory when something on disk knows.

## RULE: lookup-ladder
Stop at the first tier that answers. Name any skipped tier.

| Tier | Source | Use when |
|---|---|---|
| 0 | `.claude/knowledge/` | A Windows decision, gotcha, or past investigation. Route via `INDEX.md`. |
| 1 | The cross-platform catalog | Any question about what EnviousWispr does. See CLAUDE.md. |
| 2 | The macOS source | The catalog was thin or answered about another mechanism. |
| 3 | `context7` | Current .NET, WinUI, MSBuild, or SDK docs. Use even when you think you know. |
| 4 | Pinned package sources | Version-specific behaviour, undocumented internals. |
| 5 | Web search | Tiers 0-4 returned nothing. Cite the URL. |
| 6 | Ask Saurabh | Product and business only. Never a technical lookup. |

## RULE: a-catalog-hit-does-not-end-the-search
A hit closes the discovery step only. It never proves the mechanism, current behaviour, or absence.

A hit about a NEIGHBOURING mechanism is not an answer. Verify the row covers the mechanism you are
investigating. The trap is not silence; it is a well-formed answer under a name that matches your symptom.
Measured 2026-08-30: a confident `hallucination-protection` hit described polish-output guards, the real
answer sat in the macOS ASR backend, and the day was lost.

## RULE: read-the-entry-not-the-first-lines
When a file is named, `grep -n "^## RULE:\|^## FACT:"` it and read the matching entry. Opening the first
N lines is not a lookup. Check any scope qualifier and date before citing an entry as current.

## RULE: absence-claims-need-a-search
"We have no X", "X does not exist", and "no consumers" each require a pasted search for the CAPABILITY's
synonyms, never the name you expect. Paste every hit and classify it. An absence claim expires; re-run
someone else's sweep, and your own from an hour ago.

Where a finite authority defines all members (an enum, a switch, a registration list), cite it, paste the
enumeration, and account for every member. Otherwise write "no implementation found in <sources>".

## RULE: verify-before-you-assert
Every fact in a plan, a review prompt, a commit message, or a code comment carries an evidence burden.

- A comment claiming a mechanism ENFORCES something retires the check. Name the mechanism, then ask what
  happens if someone adds a member today. If the answer is "nothing", write real enforcement instead.
- Identifiers, paths, and counts read as authoritative because they look checked. Anything checkable
  mechanically must never be checked by reading.
- Attribute to the run or the `file:line`, never to a person or a session.
- Before accepting OR issuing a correction, resolve it to a `file:line` and read that line.

## RULE: measure-before-you-write-the-number
Produce the number first, then the sentence. State the denominator and the source. Never compose a
quantitative claim and measure only when challenged.
