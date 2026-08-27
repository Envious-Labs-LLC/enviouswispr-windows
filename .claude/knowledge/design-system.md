# EnviousWispr Windows design system

Authoritative visual contract for every pixel the Windows product draws. Source of truth for the values is
the macOS token file preserved in this repository at
`macos-source/Sources/EnviousWisprAppKit/Views/Settings/SettingsDesignTokens.swift`. Where this document
and that file disagree, that file wins and this document is the defect.

Windows is the same product as macOS, not a Windows-flavoured relative. A user who knows the Mac app must
recognise this one instantly. Fluent/WinUI defaults are the failure mode, not the baseline.

## RULE: brand-invariant-light-airy-lavender
EnviousWispr is light, airy and lavender-forward. Warm lavender-white surfaces, near-black text with a
purple cast, purple accent, rainbow spectrum as spice. Dark mode is night-comfort: low-chroma
violet-neutral surfaces, off-white text capped below pure white, desaturated lavender accent, muted
semantics. It is not an inversion and not a black theme.

Never ship: black or noir backgrounds, neon-on-black, cyberpunk or "hacker" aesthetics, high-contrast
dramatic shadowing, gritty or industrial texture. `#0f0a1a` is a TEXT colour, never a background.

**Never apply the rainbow spectrum to type** - letters, headlines, numbers or words. It reads muddy at
every size. The spectrum is reserved for the logo mark, the recording overlay, borders and dividers. For
emphasis in text use the solid accent or weight and size.

## RULE: every-colour-comes-from-a-token
No literal colour may appear in a view. Every brush resolves from `Theme/DesignTokens.xaml` through a
`ThemeResource` lookup, so Light, Dark and HighContrast all follow from one declaration site. A hard-coded
`#RRGGBB` in a `.xaml` view or a `.cs` view file is a defect regardless of whether it happens to match.

## FACT: what actually enforces this document
Three test classes hold this contract up. They are the mechanism; this prose is only the explanation, and
where the two disagree the tests are what ships.

| Enforcer | Refuses |
|---|---|
| `DesignSystemTokenTests` | a literal colour in any view outside `Theme/`; a token missing from any of the three theme dictionaries; a Light or Dark value that disagrees with the macOS source; an overlay colour that is not a pill token; overlay text below the 14px floor; a `MaxWidth` typed as a number instead of a layout token; a choice list that does not pin itself to one full-width column; a setting row whose glyph is not lifted onto its first line of text |
| `XamlResourceResolutionTests` | a `Brand*` or `Pill*` key that resolves nowhere; a style applied to a control type it does not target |
| `WindowMinimumSizeTests` | a window minimum that stops being derived from the sidebar width and the frame inset; a content-card minimum too small to be usable |
| `DesignSystemTokenTests` (types) | a layout token assigned to a property of a different type - the defect that builds clean and then refuses to start |

**Two of these guard defects that are invisible to the compiler and fatal at runtime.** A mistyped
`ThemeResource` key does not fail the build; the page throws when it is opened. A minimum size that stops
tracking the sidebar does not fail anything; the window just becomes unusable at a width nobody tested.
Both were caught only because a test was written for them, and both had their ability to fail proven by
deliberately introducing the defect and watching the test report it. **A gate never observed failing is a
comment** — if you add a fourth enforcer here, prove it the same way. The three layout gates were armed
the same way: a token swapped back to `820`, a column count back to `2`, and one glyph's alignment
removed, all three reported, then reverted byte-identically.

**Enumerate from the document, never from a list of pages kept in the test.** All three layout gates
walk `MainWindow.xaml` and check whatever they find, so a page or a choice list added next month is
covered on arrival. A gate carrying its own roster silently stops covering the thing it was written
for the first time somebody adds a page and does not think to update it.

## RULE: a-change-that-ships-and-does-nothing-looks-like-one-that-never-arrived
**Four fixes in this repo shipped, built clean, passed every gate, and had ZERO effect on the running
app.** Each was found only because someone measured the app afterwards and got a number identical to the
one before.

- A minimum width bound to a list, to equalise cards: measurements byte-identical across two builds. The
  binding could not resolve from inside a style in a resource dictionary, so the value silently stayed 0.
- `MinHeight` set BELOW an element's natural height, to make rows denser. A minimum is a FLOOR; it can
  only push bigger. Inert by construction.
- A check badge hidden from one visual-state group while another group set the same property. Two groups,
  one property, no defined order between them - the other group won.
- A rule implemented on one page when the case it was written for lived on the other page.
- A `DecimalFormatter` attached to both day fields to stop fractional days. `FractionDigits` is a
  MINIMUM number of fraction digits, not a maximum: it stops zero-padding and rounds nothing. Measured
  after shipping, "12.7" survived the display AND the value untouched. Rounding needs a `NumberRounder`
  beside the formatter - `IncrementNumberRounder { Increment = 1 }`.

**The tell is always the same and it is free: the measurement does not move.** Not "improves a little" -
IDENTICAL. Ask for the number before and the number after, and treat an unchanged number as evidence the
change never reached the thing, not as evidence the change was too small.

**And the inverse failure is cheaper, so prefer it.** Replacing `MinHeight` with `Height` fixed the
density and CLIPPED the group headings - the bottom row of pixels sliced off every glyph. That cost one
round, because it was visible the moment anyone looked. The three inert changes cost three rounds and
would all have shipped believed-working. **A fix that fails loudly beats three that fail silently.**

Corollary for sizing a text-bearing container: shorten the BLOCK with margin, never the text box with a
height. `MinHeight` cannot shrink it and `Height` cannot let it grow, so a fixed height is a clip waiting
for a longer word or a larger font.

## RULE: a-green-suite-here-is-not-evidence-the-app-starts
**Every check in this file parses the views as XML. None of them LOADS them as XAML, and those are
different questions.** A `StaticResource` is assigned without running a type converter, so a token of the
wrong type builds clean, keeps the XML well-formed, passes every gate - and the app exits about two seconds
after launch with `E_XAMLPARSEFAILED`, no window, and nothing on stdout or stderr.

Measured 2026-08-27, and it shipped through four commits before anyone saw it: 41 `ColumnDefinition.Width`
attributes read a token declared `<x:Double>`, where that property is a `GridLength`. Four builds reported
green on an application that could not start. The only reason it surfaced at all is that a person launched
it.

Two standing consequences:

- **The first thing after any rebuild is to LAUNCH IT and confirm a window appears**, before capturing
  anything or drawing any conclusion. A green build is not evidence that the app runs, and a screenshot
  round begun on that assumption wastes the round.
- **Hash `EnviousWispr.App.dll` and `MainWindow.xbf`, never `EnviousWispr.App.exe`.** The exe is the native
  apphost stub and is byte-identical across builds, so comparing it "confirms" delivery of a build that
  never arrived. Same silent-empty family as everything in the validation rules: the tool answers, the
  answer is well-formed, and it is about the wrong thing.

`EveryLayoutTokenIsAssignedToAPropertyOfItsOwnType` now catches the decidable half of this from source, and
reports an unchecked property LOUDLY rather than skipping it - an unknown property is exactly the case
where a wrong answer looks like a right one.

Known reach limits, stated so a green run is not over-read: the resource gate covers only the `Brand*` and
`Pill*` prefixes we own, and its type check cannot see custom controls, WinUI inheritance outside its
explicit table, platform-owned styles, implicit styles, or `BasedOn` chains. What none of them can see at
all is owned below by RULE: verify-rendered-result-not-the-declaration.

## FACT: colour-tokens
Light / Dark. HighContrast maps to the system colours and is listed separately below.

### Surfaces
| Token | Light | Dark | Use |
|---|---|---|---|
| `BrandPageBg` | `#F8F5FF` | `#131019` | Page background inside a content card |
| `BrandCardBg` | `#FFFFFF` | `#201B2B` | Setting cards, header cards |
| `BrandSidebarBg` | `#E8E2F5` | `#1A1623` | Navigation sidebar card |
| `BrandWindowBg` | `#DDD5EE` | `#0D0B12` | Window canvas BEHIND the two frame cards |

The window canvas is deliberately darker than every card so the sidebar and content read as raised panels
on a common ground.

### Text
| Token | Light | Dark | Use |
|---|---|---|---|
| `BrandTextPrimary` | `#0F0A1A` | `#ECE9F4` | Titles, row labels |
| `BrandTextBody` | `#332D47` | `#D5D1E2` | Reading copy, descriptions |
| `BrandTextSecondary` | `#4A3D60` | `#AAA2BF` | Secondary text |
| `BrandTextTertiary` | `#6B5E86` | `#7A7290` | Captions, placeholders |

Dark primary is capped below pure white to limit halation.

### Accent - two tokens, and the split is load-bearing
| Token | Light | Dark | Use |
|---|---|---|---|
| `BrandAccent` | `#7C3AED` | `#A78BFA` | Text, outlines, and translucent washes behind ordinary text |
| `BrandAccentSolid` | `#7C3AED` | `#8B46F0` | Opaque fills carrying white or on-accent content |
| `BrandAccentLight` | `#7C3AED` @ 9% | `#A78BFA` @ 16% | The standard soft accent wash |

**The condition is what the fill sits UNDER, not whether it is a fill.** The desaturated lavender reads
washed out beneath white text, so an opaque surface carrying white content takes the solid purple. A
translucent accent wash behind body-coloured text — a navigation row on hover, a selected card's tint —
is not that case and correctly uses `BrandAccent`; substituting the solid purple there would be wrong.

This was originally written as "TEXT and OUTLINES only", which a careful reviewer read as forbidding the
hover washes. The rule had not changed, but a rule that a careful reader misapplies is a rule that needs
rewording. Ref: whole-diff review, 2026-08-27.

### Status
| Token | Light | Dark |
|---|---|---|
| `BrandSuccess` | `#00A366` | `#5CC99A` |
| `BrandWarning` | `#CC7000` | `#E6B766` |
| `BrandWarningSoft` | `#CC7000` @ 10% | `#E6B766` @ 14% |
| `BrandError` | `#C0392B` | `#EF7C89` |
| `BrandToggleOn` | `#00A366` | `#5CC99A` |
| `BrandToggleOff` | `#9B8EB8` | `#4A4360` |
| `BrandDivider` | `#8A2BE2` @ 8% | `#B8AAD6` @ 14% |

### Rainbow spectrum
`BrandSpectrumRed #FF2A40`, `Orange #FF8C00`, `Gold #FFD700`, `Lime #ADFF2F`, `Spring #00FA9A`,
`Cyan #00FFFF`, `Blue #1E90FF`, `Royal #4169E1`, `Violet #8A2BE2`. Identical in both themes.

### HighContrast
Every token above resolves to the matching `SystemColor*` resource. Accent takes `SystemAccentColor`.
Decorative soft washes and glows, including `BrandAccentLight` and `BrandWarningSoft`, collapse to
`Transparent`. Structural borders, including `BrandDivider`, take `SystemColorWindowTextColor` so their
boundaries remain visible. Spectrum colours also collapse to `SystemColorWindowTextColor`. HighContrast
must never invent a colour.

## FACT: type-scale
`Segoe UI Variable` is the Windows body face - it is the platform-correct counterpart to the Mac's
`system-ui`. Plus Jakarta Sans is web and marketing only and must never ship in the app.

**14px is the floor. Nothing renders smaller.** Hierarchy is built UP from 14 with weight and one size
bump, never DOWN with a smaller size.

| Token | Size | Weight | Use |
|---|---|---|---|
| `BrandPageTitleStyle` | 30 | SemiBold | One per page, the page's own name |
| `BrandSectionEyebrowStyle` | 14 | SemiBold, UPPERCASED, accent colour | Label above a card or group |
| `BrandRowTitleStyle` | 16 | SemiBold | Section subject, engine name. One per section |
| `BrandRowLabelStyle` | 14 | SemiBold | Lead line of a control row |
| `BrandBodyStyle` | 14 | Regular, `BrandTextBody` | Descriptions, explainers |
| `BrandHelperStyle` | 14 | Regular, `BrandTextTertiary` | Captions, hints, status |

Titles and body share the primary-adjacent colours so a page never has a bright paragraph shouting over a
dim title. Only helper steps down in colour.

## FACT: layout-constants
| Token | Value | Use |
|---|---|---|
| `BrandWindowCardRadius` | 18 | The two frame cards |
| `BrandWindowFrameInset` | 14 | Inset from window edge AND the gap between the frame cards |
| `BrandSectionRadius` | 14 | Setting cards inside the content card |
| `BrandRowPaddingH` | 14 | Row horizontal padding |
| `BrandRowPaddingV` | 12 | Row vertical padding |
| `BrandSectionSpacing` | 18 | Between setting cards |
| `BrandContentTop` | 18 | Top bar to first content card |
| `BrandContentH` | 24 | Content card horizontal padding |
| `BrandContentBottom` | 32 | Bottom padding |
| `BrandPillRadius` | 100 | Pills and tags |
| `BrandNavRowHeight` | 36 | Nav row height - the macOS sidebar's pitch, matched deliberately |
| `BrandNavGroupHeaderMargin` | 0,10,0,2 | The group heading BLOCK; the text box is never height-constrained |
| `BrandPageContentMaxWidth` | 1040 | The measure of EVERY settings page |
| `BrandInlineContentMaxWidth` | 440 | A column centred inside a page card |
| `BrandRowIconColumnWidth` | 28 | The leading icon column on a setting row |
| `BrandRowIconInset` | 0,2,0,0 | Lifts the glyph onto the row's first text line |

The frame cards' radius is larger than the inner setting cards so the frame clearly contains the content
rather than competing with it.

## RULE: two-card-window-frame
The window is not a stock WinUI `NavigationView` on a Mica sheet. It is two equal floating cards on a
darker canvas:

- The window canvas paints `BrandWindowBg` edge to edge. No Mica, no Acrylic behind the frame - a system
  backdrop makes the canvas the desktop's colour and destroys the raised-panel reading. Mica is allowed
  only in the title bar strip.
- A sidebar card (`BrandSidebarBg`, radius 18) and a content card (`BrandPageBg`, radius 18) sit side by
  side, each inset 14 from the window edge and separated from each other by 14, so the spacing reads
  uniform on all four sides.
- Both cards carry a 1px `BrandDivider` border.
- Inside the content card, setting cards use `BrandCardBg` at radius 14.

## FACT: navigation-sizing-numbers
Measured on the running app, not derived from row arithmetic. **Every figure previously derived by
multiplying rows by row height was wrong** - 754 against a real 896, then 731 against a real 832 - because
the arithmetic does not know what the five group headings cost, and the headings are the expensive part.

| Quantity | Value |
|---|---|
| Nav content, full scroll extent | 832 DIP |
| Window chrome above and below the nav | 219 DIP |
| Window height to show the nav whole | 1051 DIP (default asks 1060) |
| Nav row height / pitch | 32 / 36 DIP - the macOS pitch |
| Group heading block | 56 DIP from previous row bottom to next row top |

The default window asks for 1060 DIP and **clamps to 94% of the display work area**, so it opens whole on
a 4K or 1440p screen and opens to fit, scrolling, on anything shorter. That is what the Mac does too: its
sidebar is a ScrollView and does not fit its own list either.

**Do not buy the fit out of the heading margins.** The lead-in above each heading is what makes five
groups read as groups rather than one long list; trimming it is the single change that moves the sidebar
from "reads well" to "tight". Grow the window while headroom remains, and accept scrolling once it does
not.

## RULE: one-measure-for-every-page
Every page caps its content at `BrandPageContentMaxWidth` and centres it in the content card. **No page
sets its own number.** Measured on Windows before this rule existed: three pages had picked 900, 820 and
440 independently, so the content column visibly changed width as the user clicked from one nav row to the
next and the frame appeared to twitch. A column nested inside a page card uses
`BrandInlineContentMaxWidth`.

The cap is a MEASURE, not a fill: on a maximised window there is meant to be canvas either side, the same
way the Mac window is not full screen. Widening the cap until the slack disappears trades readable line
length for the appearance of using the space.

## RULE: page-header-card
Every page opens with a header card: a leading icon tile filled `BrandAccentLight` with the glyph in
`BrandAccent`, the page title in `BrandPageTitleStyle`, and one line of body copy under it. The header is
the first content object, separated from the top bar by `BrandContentTop`.

## RULE: choices-are-selectable-cards-not-radio-buttons
Engine, mode and appearance choices render as selectable cards: a card that shows the option's name, its
one-line description, and - when selected - a 2px `BrandAccent` ring plus a filled `BrandAccentSolid`
check badge carrying a white glyph. A bare `RadioButton` list is not the product.

**One full-width column, always.** Pin the column count rather than letting the layout negotiate
one: a negotiated count sizes each card to its own text, and six provider cards then render at six
different widths as a staircase down the page. The Mac stacks engine choices full width and always
has, so multi-column was never the design.

## RULE: rows-carry-icons
Every control row in a setting card leads with a glyph in `BrandTextSecondary`, then the label in
`BrandRowLabelStyle`, then optional helper text, then the control trailing. A row without an icon reads as
a form, and the product is not a form.

**The glyph aligns to the row's FIRST LINE OF TEXT, never to the row's vertical centre.** A row is
usually a label, a control and a line of helper text, so a centred glyph floats level with the middle
of the control and detaches from the label it belongs to - read down a page, the icons stop being part
of their rows and become a stray column of marks in the gutter. `BrandRowIconInset` carries the
correction.

## RULE: the-brand-mark-is-vector-and-appears-in-the-top-bar
The rainbow waveform mark sits beside the wordmark in the window top bar and in the recording overlay. It
ships as `Assets/Brand/EnviousWisprMark.svg`, whose geometry and colours mirror the website favicon. Do not
substitute a raster icon in the top bar, and do not add a brand-signature footer.

## RULE: recording-overlay-follows-the-appearance-catalog
The overlay is not one hard-coded look. It renders whichever appearance the user selected - Capsule,
Reading Well, Level Rail - at whichever placement they chose, and it remembers the wordless and with-words
choices separately. Every colour in it resolves from the tokens above; the level meter is driven by real
capture level, never by fixed bar heights.

## RULE: verify-rendered-result-not-the-declaration
Reading back a style declaration confirms the cascade, never the layout. A test that asserts a
`ThemeResource` key was set proves nothing about what the user sees. Assert measured geometry and resolved
brushes, and treat any absolute-size assertion as a measurement of the host machine - gate it, and say out
loud that a gated row proves nothing on CI.

**No enforcer in this repository can see a rendered pixel, so every geometric and perceptual claim in this
document is unverified until a human looks.** Whether the two cards read as raised rather than as regions
of one sheet, whether a focus ring survives a filled purple row, whether the pill stays legible over
someone else's document, whether a header card's icon tile sits right beside wrapped copy at the minimum
window width — all of that is asserted here and checked nowhere. Say so when you ship UI, rather than
letting a green suite imply it was looked at. The entire design system landed this way once, in one night,
against code and the macOS token files and never against a screen.

## RULE: a-handled-routed-event-does-not-reach-the-system-wide-hook
**The recording key is a low-level keyboard hook, so it fires BEFORE any window sees the key.** Setting
`e.Handled = true` in a XAML `KeyDown` handler is a statement about the window's routed event and has no
bearing on the hook at all.

Measured on the running app: pressing the recording key inside its own capture field on the Keybinds page
started a real recording, which ran for 64 seconds. The capture handler had marked the event handled. The
field was also read-only, so once it held a different value there was no way to restore it in-session -
the key that would restore it records instead of typing.

**So a keybind field cannot fix this in its key handler. The hook has to be told to stand down**, which is
what `HotkeyEdgeTracker.SetCapturingKeybind` does, driven by the field's FOCUS rather than its keystrokes.

**The half that matters more than the fix: standing down must never strand a recording.** Capture never
suppresses a key while a gesture is part-way through or a toggle-mode recording is running, because
swallowing the release edge would leave a recording nothing could end - a worse defect than the one being
removed. Deliberate consequence: press the recording key mid-recording and it stops the recording rather
than being captured.

Same reasoning applies to any future field that captures keys. Enumerate the fields FROM THE MARKUP, not
from the three anyone remembers: miss one and that field alone records, which is invisible beside two that
behave.

## FACT: the-app-has-two-top-level-windows-and-the-overlay-can-win-a-uia-race
A UI Automation lookup of `FindFirst(Children, ProcessId)` returns the FIRST top-level window for the
process. EnviousWispr has two: the main window and the recording overlay pill. When the pill appears it
can become that first window, and every query then runs against the pill's tree and returns empty.

**An empty result reads exactly like a dead app**, and that is the reading it got the first time. Select
the window by NAME - "EnviousWispr" - not by process. Ref: Windows session, 2026-08-27.

## RULE: every-severity-needs-its-own-tint-and-the-set-is-the-unit
The four notification severities are the app's whole vocabulary for "how bad is this", and each has to
be recognisable BEFORE the words are read.

Error and Success both pointed at `BrandCardBgColor`, so an ERROR arrived with no colour behind it at
all - the same white card as everything else, distinguishable only by a small icon. Warning and
Informational had tinted backgrounds. **Nobody chose that**: `BrandWarningSoftColor` was the only soft
tint that existed, so the two severities with no token of their own fell back to the surface colour.

**The gate covers the whole SET - four severities by two properties, eight cells - and not the two that
were wrong.** Fixing the pair someone noticed leaves the same hole open for whichever cell is added next,
and the hole is silent by construction: a missing tint renders as the card, which looks deliberate.

Soft tints are the base colour at low alpha: `#1A` in Light, `#24` in Dark. **High Contrast sets every
soft tint to `Transparent`** so the system's own colours decide, which is what High Contrast is for.

**Read this beside RULE: a-change-that-ships-and-does-nothing.** Those four were changes that ARRIVED and
had no effect. This is the other half: a token that was never created, whose absence renders as a
plausible default rather than as a gap.

