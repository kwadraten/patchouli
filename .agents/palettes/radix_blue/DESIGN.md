---
name: Radix Blue
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    blue-1: '#fbfdff'
    blue-2: '#f4faff'
    blue-3: '#e6f4fe'
    blue-4: '#d5efff'
    blue-5: '#c2e5ff'
    blue-6: '#acd8fc'
    blue-7: '#8ec8f6'
    blue-8: '#5eb1ef'
    blue-9: '#0090ff'
    blue-10: '#0588f0'
    blue-11: '#0d74ce'
    blue-12: '#113264'
  dark:
    blue-1: '#0d1520'
    blue-2: '#111927'
    blue-3: '#0d2847'
    blue-4: '#003362'
    blue-5: '#004074'
    blue-6: '#104d87'
    blue-7: '#205d9e'
    blue-8: '#2870bd'
    blue-9: '#0090ff'
    blue-10: '#3b9eff'
    blue-11: '#70b8ff'
    blue-12: '#c2e6ff'
---

## Radix Blue

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

Every step also ships as an alpha variant (`blue-a1` … `blue-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `blue-alpha.css` / `blue-dark-alpha.css`.
