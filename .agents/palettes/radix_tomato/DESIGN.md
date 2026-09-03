---
name: Radix Tomato
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    tomato-1: '#fffcfc'
    tomato-2: '#fff8f7'
    tomato-3: '#feebe7'
    tomato-4: '#ffdcd3'
    tomato-5: '#ffcdc2'
    tomato-6: '#fdbdaf'
    tomato-7: '#f5a898'
    tomato-8: '#ec8e7b'
    tomato-9: '#e54d2e'
    tomato-10: '#dd4425'
    tomato-11: '#d13415'
    tomato-12: '#5c271f'
  dark:
    tomato-1: '#181111'
    tomato-2: '#1f1513'
    tomato-3: '#391714'
    tomato-4: '#4e1511'
    tomato-5: '#5e1c16'
    tomato-6: '#6e2920'
    tomato-7: '#853a2d'
    tomato-8: '#ac4d39'
    tomato-9: '#e54d2e'
    tomato-10: '#ec6142'
    tomato-11: '#ff977d'
    tomato-12: '#fbd3cb'
---

## Radix Tomato

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

Every step also ships as an alpha variant (`tomato-a1` … `tomato-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `tomato-alpha.css` / `tomato-dark-alpha.css`.
