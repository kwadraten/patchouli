---
name: Radix Sage
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    sage-1: '#fbfdfc'
    sage-2: '#f7f9f8'
    sage-3: '#eef1f0'
    sage-4: '#e6e9e8'
    sage-5: '#dfe2e0'
    sage-6: '#d7dad9'
    sage-7: '#cbcfcd'
    sage-8: '#b8bcba'
    sage-9: '#868e8b'
    sage-10: '#7c8481'
    sage-11: '#5f6563'
    sage-12: '#1a211e'
  dark:
    sage-1: '#101211'
    sage-2: '#171918'
    sage-3: '#202221'
    sage-4: '#272a29'
    sage-5: '#2e3130'
    sage-6: '#373b39'
    sage-7: '#444947'
    sage-8: '#5b625f'
    sage-9: '#63706b'
    sage-10: '#717d79'
    sage-11: '#adb5b2'
    sage-12: '#eceeed'
---

## Radix Sage

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

Every step also ships as an alpha variant (`sage-a1` … `sage-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `sage-alpha.css` / `sage-dark-alpha.css`.

### Pairing

This is one of the six Radix grays. Pair it with an accent scale that shares its undertone: `gray` is pure neutral, `mauve` leans purple, `slate` leans blue, `sage` leans green, `olive` leans yellow-green, `sand` leans warm/yellow.
