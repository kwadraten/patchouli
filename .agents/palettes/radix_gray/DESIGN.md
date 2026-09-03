---
name: Radix Gray
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    gray-1: '#fcfcfc'
    gray-2: '#f9f9f9'
    gray-3: '#f0f0f0'
    gray-4: '#e8e8e8'
    gray-5: '#e0e0e0'
    gray-6: '#d9d9d9'
    gray-7: '#cecece'
    gray-8: '#bbbbbb'
    gray-9: '#8d8d8d'
    gray-10: '#838383'
    gray-11: '#646464'
    gray-12: '#202020'
  dark:
    gray-1: '#111111'
    gray-2: '#191919'
    gray-3: '#222222'
    gray-4: '#2a2a2a'
    gray-5: '#313131'
    gray-6: '#3a3a3a'
    gray-7: '#484848'
    gray-8: '#606060'
    gray-9: '#6e6e6e'
    gray-10: '#7b7b7b'
    gray-11: '#b4b4b4'
    gray-12: '#eeeeee'
---

## Radix Gray

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

Every step also ships as an alpha variant (`gray-a1` … `gray-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `gray-alpha.css` / `gray-dark-alpha.css`.

### Pairing

This is one of the six Radix grays. Pair it with an accent scale that shares its undertone: `gray` is pure neutral, `mauve` leans purple, `slate` leans blue, `sage` leans green, `olive` leans yellow-green, `sand` leans warm/yellow.
