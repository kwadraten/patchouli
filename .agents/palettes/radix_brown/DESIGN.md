---
name: Radix Brown
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    brown-1: '#fefdfc'
    brown-2: '#fcf9f6'
    brown-3: '#f6eee7'
    brown-4: '#f0e4d9'
    brown-5: '#ebdaca'
    brown-6: '#e4cdb7'
    brown-7: '#dcbc9f'
    brown-8: '#cea37e'
    brown-9: '#ad7f58'
    brown-10: '#a07553'
    brown-11: '#815e46'
    brown-12: '#3e332e'
  dark:
    brown-1: '#12110f'
    brown-2: '#1c1816'
    brown-3: '#28211d'
    brown-4: '#322922'
    brown-5: '#3e3128'
    brown-6: '#4d3c2f'
    brown-7: '#614a39'
    brown-8: '#7c5f46'
    brown-9: '#ad7f58'
    brown-10: '#b88c67'
    brown-11: '#dbb594'
    brown-12: '#f2e1ca'
---

## Radix Brown

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

Every step also ships as an alpha variant (`brown-a1` … `brown-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `brown-alpha.css` / `brown-dark-alpha.css`.
