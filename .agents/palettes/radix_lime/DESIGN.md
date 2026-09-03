---
name: Radix Lime
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    lime-1: '#fcfdfa'
    lime-2: '#f8faf3'
    lime-3: '#eef6d6'
    lime-4: '#e2f0bd'
    lime-5: '#d3e7a6'
    lime-6: '#c2da91'
    lime-7: '#abc978'
    lime-8: '#8db654'
    lime-9: '#bdee63'
    lime-10: '#b0e64c'
    lime-11: '#5c7c2f'
    lime-12: '#37401c'
  dark:
    lime-1: '#11130c'
    lime-2: '#151a10'
    lime-3: '#1f2917'
    lime-4: '#29371d'
    lime-5: '#334423'
    lime-6: '#3d522a'
    lime-7: '#496231'
    lime-8: '#577538'
    lime-9: '#bdee63'
    lime-10: '#d4ff70'
    lime-11: '#bde56c'
    lime-12: '#e3f7ba'
---

## Radix Lime

A 12-step color scale from [Radix Colors](https://www.radix-ui.com/colors) (`@radix-ui/colors` 3.0.0). Each step targets a specific UI use case, so steps can be composed without hand-tuning: text on steps 11/12 is guaranteed to meet APCA contrast targets against the background steps (1-5) of the same scale.

### Step semantics

- **Step 1** — App background
- **Step 2** — Subtle background
- **Step 3** — UI element background
- **Step 4** — Hovered UI element background
- **Step 5** — Active / Selected UI element background
- **Step 6** — Subtle borders and separators
- **Step 7** — UI element border and focus rings
- **Step 8** — Hovered UI element border
- **Step 9** — Solid backgrounds
- **Step 10** — Hovered solid backgrounds
- **Step 11** — Low-contrast text
- **Step 12** — High-contrast text

### Dark mode

The `dark` token set is a drop-in replacement for the `light` set: apply the same step numbers and the scale works unchanged on a dark background. The frontmatter above lists both sets (sRGB hex; P3 wide-gamut variants are available in the source package for displays that support them).

### Alpha variants

Every step also ships as an alpha variant (`lime-a1` … `lime-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `lime-alpha.css` / `lime-dark-alpha.css`.
