---
name: Radix Slate
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    slate-1: '#fcfcfd'
    slate-2: '#f9f9fb'
    slate-3: '#f0f0f3'
    slate-4: '#e8e8ec'
    slate-5: '#e0e1e6'
    slate-6: '#d9d9e0'
    slate-7: '#cdced6'
    slate-8: '#b9bbc6'
    slate-9: '#8b8d98'
    slate-10: '#80838d'
    slate-11: '#60646c'
    slate-12: '#1c2024'
  dark:
    slate-1: '#111113'
    slate-2: '#18191b'
    slate-3: '#212225'
    slate-4: '#272a2d'
    slate-5: '#2e3135'
    slate-6: '#363a3f'
    slate-7: '#43484e'
    slate-8: '#5a6169'
    slate-9: '#696e77'
    slate-10: '#777b84'
    slate-11: '#b0b4ba'
    slate-12: '#edeef0'
---

## Radix Slate

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

Every step also ships as an alpha variant (`slate-a1` … `slate-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `slate-alpha.css` / `slate-dark-alpha.css`.

### Pairing

This is one of the six Radix grays. Pair it with an accent scale that shares its undertone: `gray` is pure neutral, `mauve` leans purple, `slate` leans blue, `sage` leans green, `olive` leans yellow-green, `sand` leans warm/yellow.
