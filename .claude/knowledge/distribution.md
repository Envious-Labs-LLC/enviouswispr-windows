# Distribution and update contract

## Primary path

Ship a signed, self-contained direct installer and use Velopack for Sparkle-equivalent Windows updates.
The app supports stable, founder, and beta channels without sharing state between them accidentally.

An update is accepted only after signature and hash validation. Downloads are resumable, installation is
atomic, user data remains outside the install directory, and the last known-good version is recoverable.
The app never updates while recording or processing a dictation.

## Signing

Windows development itself requires no paid developer membership. Public direct distribution should use
trusted code signing. Azure Artifact Signing is the preferred managed option when public distribution
begins. It has an ongoing cost and requires explicit founder approval before enabling paid infrastructure.
Signing reduces publisher warnings, but reputation-based SmartScreen prompts can still occur early in a
new product's life.

## Secondary path

Microsoft Store and MSIX distribution can be evaluated after direct install and update reliability are
proven. Store packaging must not become the only route or weaken local model delivery, BYOK, Ollama, or
offline behavior.
