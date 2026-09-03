---
name: Radix Olive
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    olive-1: '#fcfdfc'
    olive-2: '#f8faf8'
    olive-3: '#eff1ef'
    olive-4: '#e7e9e7'
    olive-5: '#dfe2df'
    olive-6: '#d7dad7'
    olive-7: '#cccfcc'
    olive-8: '#b9bcb8'
    olive-9: '#898e87'
    olive-10: '#7f847d'
    olive-11: '#60655f'
    olive-12: '#1d211c'
  dark:
    olive-1: '#111210'
    olive-2: '#181917'
    olive-3: '#212220'
    olive-4: '#282a27'
    olive-5: '#2f312e'
    olive-6: '#383a36'
    olive-7: '#454843'
    olive-8: '#5c625b'
    olive-9: '#687066'
    olive-10: '#767d74'
    olive-11: '#afb5ad'
    olive-12: '#eceeec'
---

## Radix Olive

A 12-step gray scale from [Radix Colors](https://www.radix-ui.com/colors) (`@radix-ui/colors` 3.0.0). Each step targets a specific UI use case, so steps can be composed without hand-tuning: text on steps 11/12 is guaranteed to meet APCA contrast targets against the background steps (1-5) of the same scale.

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

Every step also ships as an alpha variant (`olive-a1` … `olive-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `olive-alpha.css` / `olive-dark-alpha.css`.

### Pairing

This is one of the six Radix grays. Pair it with an accent scale that shares its undertone: `gray` is pure neutral, `mauve` leans purple, `slate` leans blue, `sage` leans green, `olive` leans yellow-green, `sand` leans warm/yellow.
