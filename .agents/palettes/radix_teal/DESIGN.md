---
name: Radix Teal
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    teal-1: '#fafefd'
    teal-2: '#f3fbf9'
    teal-3: '#e0f8f3'
    teal-4: '#ccf3ea'
    teal-5: '#b8eae0'
    teal-6: '#a1ded2'
    teal-7: '#83cdc1'
    teal-8: '#53b9ab'
    teal-9: '#12a594'
    teal-10: '#0d9b8a'
    teal-11: '#008573'
    teal-12: '#0d3d38'
  dark:
    teal-1: '#0d1514'
    teal-2: '#111c1b'
    teal-3: '#0d2d2a'
    teal-4: '#023b37'
    teal-5: '#084843'
    teal-6: '#145750'
    teal-7: '#1c6961'
    teal-8: '#207e73'
    teal-9: '#12a594'
    teal-10: '#0eb39e'
    teal-11: '#0bd8b6'
    teal-12: '#adf0dd'
---

## Radix Teal

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

Every step also ships as an alpha variant (`teal-a1` … `teal-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `teal-alpha.css` / `teal-dark-alpha.css`.
