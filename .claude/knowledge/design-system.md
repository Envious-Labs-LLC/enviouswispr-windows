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

Enforced by `DesignSystemTokenTests`. That test is the mechanism; it fails on any literal colour outside
the token dictionary and on any token missing from any of the three theme dictionaries.

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
| `BrandAccent` | `#7C3AED` | `#A78BFA` | TEXT and OUTLINES only |
| `BrandAccentSolid` | `#7C3AED` | `#8B46F0` | FILLED surfaces carrying white text or glyphs |
| `BrandAccentLight` | `#7C3AED` @ 9% | `#A78BFA` @ 16% | Soft accent wash |

The desaturated lavender is for text and outlines. Used as a fill under white it reads washed out, so
filled selected surfaces take the solid purple.

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

## RULE: page-header-card
Every page opens with a header card: a leading icon tile filled `BrandAccentLight` with the glyph in
`BrandAccent`, the page title in `BrandPageTitleStyle`, and one line of body copy under it. The header is
the first content object, separated from the top bar by `BrandContentTop`.

## RULE: choices-are-selectable-cards-not-radio-buttons
Engine, mode and appearance choices render as selectable cards: a card that shows the option's name, its
one-line description, and - when selected - a 2px `BrandAccent` ring plus a filled `BrandAccentSolid`
check badge carrying a white glyph. A bare `RadioButton` list is not the product.

## RULE: rows-carry-icons
Every control row in a setting card leads with a glyph in `BrandTextSecondary`, then the label in
`BrandRowLabelStyle`, then optional helper text, then the control trailing. A row without an icon reads as
a form, and the product is not a form.

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
