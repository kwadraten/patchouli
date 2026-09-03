---
name: Radix Sand
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    sand-1: '#fdfdfc'
    sand-2: '#f9f9f8'
    sand-3: '#f1f0ef'
    sand-4: '#e9e8e6'
    sand-5: '#e2e1de'
    sand-6: '#dad9d6'
    sand-7: '#cfceca'
    sand-8: '#bcbbb5'
    sand-9: '#8d8d86'
    sand-10: '#82827c'
    sand-11: '#63635e'
    sand-12: '#21201c'
  dark:
    sand-1: '#111110'
    sand-2: '#191918'
    sand-3: '#222221'
    sand-4: '#2a2a28'
    sand-5: '#31312e'
    sand-6: '#3b3a37'
    sand-7: '#494844'
    sand-8: '#62605b'
    sand-9: '#6f6d66'
    sand-10: '#7c7b74'
    sand-11: '#b5b3ad'
    sand-12: '#eeeeec'
---

## Radix Sand

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

Every step also ships as an alpha variant (`sand-a1` … `sand-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `sand-alpha.css` / `sand-dark-alpha.css`.

### Pairing

This is one of the six Radix grays. Pair it with an accent scale that shares its undertone: `gray` is pure neutral, `mauve` leans purple, `slate` leans blue, `sage` leans green, `olive` leans yellow-green, `sand` leans warm/yellow.
