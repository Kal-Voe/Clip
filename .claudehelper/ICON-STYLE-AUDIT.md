# Clip — Icon & Styling Uniformity Audit
_2026-09-01 · scope: `src/Clip.Shell`, `src/Clip.Watcher`, `assets/icons`_

## Verdict

The **colour system is good** (a real token set, one dark theme, honest naming mostly).
The **icon system is not a system** — it is six unrelated icon families rendered at
eight different sizes with a fixed pen, so apparent line weight varies **~2x across the
window and ~9x between the smallest chrome icon and the largest item glyph.**

---

## 1. Six icon families in one window

| # | Family | Grid | Style | Where it shows |
|---|--------|------|-------|----------------|
| A | Hand-drawn WPF vectors (`RenderChromeIcon`, `RenderItemVectorIcon`) | 24 | outline, round caps/joins, stroke 1.8–2.2, some with a 0.16-alpha fill | search, gear, plus, chevrons, expand, text/link/email/folder/image/file glyphs |
| B | `file-icon-*.svg` (svgrepo "file_xx" set) | 512 | **solid filled** document + dog-ear + bold Arial 3-letter label | 55 reachable file extensions |
| C | `file-icon-plaintext.svg` | 24 | outline-converted-to-fill, stroke 1.5 | **every copied text clip** — the most common row in the app |
| D | `file-icon-audio.svg` | 122.88 | solid filled music note | all audio files |
| E | `file-60.svg` | 1024 | solid filled document | extension-less files |
| F | Windows shell icons | n/a | **full colour, glossy, 3D** | `.pdf .doc .docx .xls .xlsx .xlsm .ppt .pptx .vsd .vsdx` |

A list showing a screenshot, a copied paragraph, a `.json`, and a `.pdf` shows four
different design languages stacked vertically. Family F is the worst offender — a
colourful Office icon next to flat monochrome glyphs.

**Symptom of the mismatch already in the code:** `RowIconSize()` returns **22** for
text/audio and **28** for everything else. That is a hand-tuned optical correction
because family C/D fill their canvas edge-to-edge while family A has built-in padding.

---

## 2. Line weight is not constant — the biggest visible defect

Every vector icon is drawn on a 24×24 grid with a **fixed** pen, then scaled by the
`Image` element. Actual on-screen stroke = `pen × displaySize / 24`:

| Icon | Pen | Shown at | **Real stroke** |
|------|-----|----------|-----------------|
| Filter chevrons (date/file/media) | 2.2 | 11 px | **1.01 px** |
| Settings dropdown chevron | 2.2 | 11 px | 1.01 px |
| New-snippet plus | 2.2 | 12 px | 1.10 px |
| Settings gear | 1.8 | 16 px | 1.20 px |
| Search lens | 2.2 | 14 px | 1.28 px |
| Expand arrows | 2.2 | 15 px | 1.38 px |
| Row item glyphs (text/audio) | 1.8 | 22 px | **1.65 px** |
| Row + preview header glyphs | 1.8 | 28 px | **2.10 px** |
| Empty-preview placeholder glyph | 1.8 | 128 px | **9.60 px** |

The gear reads noticeably heavier than the chevrons beside it; the row glyphs read
twice as heavy as the chrome; the 128 px placeholder is a blob. The `1.8` on the gear
is already an ad-hoc patch for exactly this problem — applied to one icon only.

Fix: derive pen thickness from display size so every icon lands on one target weight.

---

## 3. Duplicated icon code

- `CreateDropdownChevronIcon()` (line ~14074) is a **byte-for-byte reimplementation** of
  `RenderChromeIcon(ChevronDown)` (line ~10494) — same three points, same 2.2 pen,
  same 24 grid, separate cache. Two implementations of one icon.
- `OpenWithWindow` and `ExcludedAppPickerWindow` are near-identical ~250-line copies
  (same 620×520, same radius 14, same 46/44/*/46 row layout, **two separate `IconCache`
  dictionaries**), each taking the palette as **11 positional brush arguments** instead
  of reading resources. Change one, forget the other.

---

## 4. Non-icons standing in for icons

- The submenu arrow in the action menu is the **ASCII character `">"`** in a `TextBlock`
  (line ~5102). The app has a proper vector chevron 4000 lines away.
- `"●"` used as a glyph.

---

## 5. Dead icon assets — 15 of 70 files are unreachable

**Never referenced anywhere (6):** `dropdown-arrow-svgrepo-com.svg`,
`expand-alt-svgrepo-com.svg`, `folder-svgrepo-com.svg`, `hyperlink-icon.svg`,
`settings-svgrepo-com.svg`, `text_underline_icon_high_fidelity.svg` — all superseded
by code-drawn vectors.

**Shipped but shadowed (9):** `file-icon-{pdf,doc,docx,xls,xlsx,ppt,pptx,vsd,vsdx}.svg`
are never reached because `ShouldUseWindowsFileIcon()` intercepts those extensions first
and serves the Windows shell icon instead.

---

## 6. Packaging bug (latent)

`assets/icons/*.svg` is copied to output by **`Clip.Watcher.csproj` only**
(`Clip.Watcher.csproj:8`). `Clip.Shell` is the project that *consumes* them and has no
such `Content` include. It works today only because both land in the same output folder.
Build or publish the Shell alone and every file-type icon silently degrades to the
generic vector document glyph. One-line fix.

---

## 7. Colour and shape tokens

**Good:** 18 named brushes in `MainWindow.xaml` + a mirrored `SettingsPalette` in code.
One dark theme, no light-mode leftovers.

**Problems:**
- **37 hard-coded hex literals** in `src/Clip.Shell/*.cs` that bypass the tokens,
  including `#FF6363` (= `Accent`, written out 4×), `#D56B5D` (= `Danger`),
  `#717176` (scrollbar thumb, defined **3×**: `App.xaml` ×2, `MainWindow.xaml` ×1),
  and `#989898` (a chevron grey that is not a token at all).
- **`Muted` naming is not a scale.** `Muted` `#8F898F`, `Muted2` `#C7C1C7`,
  `Muted3` `#6B656B` — `Muted2` is the *brightest* of the three. The numbers imply
  escalating de-emphasis; the values do the opposite.
- **Corner radius has 9 distinct values** — 3, 4, 5, 6, 7, 8, 10, 11, 14 — with no
  scale behind them. 5/6/7 and 10/11 are indistinguishable pairs.
- Font sizes are fine: 11/12/13/15/16/18, one stray `8`.

---

## 8. Off-brand surfaces

- **Tray menu** is a stock WinForms `ContextMenuStrip` — light grey Windows chrome,
  square corners, system font, hanging off a dark app (`Clip.Watcher/Program.cs`).
- **6 stock Win32 `MessageBox.Show` dialogs** (1 in Watcher, 5 in Shell).

Both are outside the palette entirely. Lower priority than the icons — they are modal
and brief — but they are the two places the illusion breaks completely.

---

## 9. Things that are already right (do not "fix" these)

- No icon font, no emoji-as-icon anywhere. Menus and settings nav are deliberately
  text-only and consistently so.
- The light cream app tile against a dark palette is a documented, deliberate choice.
- Round caps + round joins on every hand-drawn icon.
- Everything sits on a 24×24 grid.
- Scrollbars are uniform app-wide (6 px, `#717176`, radius 3).

---

## Fix order

1. **Normalise line weight** — derive pen thickness from display size. One constant.
2. **Dedupe the chevron**; replace the `">"` character with the real chevron.
3. **Delete the 15 dead SVGs**; fix the Shell packaging include.
4. **Collapse the file-type icon families** — the big one, see fork below.
5. Tokenise the 37 loose hexes; rename `Muted*` to a real scale; cut radii to 4 values.
6. Re-skin the tray menu and replace the MessageBoxes.

### The one real fork (step 4)

Family B (55 icons) has to go — a solid, labelled, dog-eared document does not belong
next to a 1.3 px outline glyph. Two ways:

- **(a) Redraw in-house** — 55 icons on the 24 grid in the existing outline style.
  Perfect match, zero dependency, but it is 55 icons to draw and maintain.
- **(b) Adopt Lucide** (MIT, 24 grid, stroke 2, round caps — *already this app's style*)
  for the ~12 real categories (code, archive, doc, sheet, slide, image, video, audio,
  disk, exe, config, generic) and drop per-extension icons, OR keep per-extension by
  overlaying a small extension label under a shared Lucide document mark.

**Recommendation: (b)**, category-based. It matches family A exactly out of the box,
kills 55 files, and 12 marks is a set a person can actually hold in their head. It also
lets family F (the colour Windows Office icons) be dropped — `.docx` becomes the doc
mark like everything else, which is what "one design style" actually requires.
