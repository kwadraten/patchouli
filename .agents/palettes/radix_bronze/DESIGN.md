---
name: Radix Bronze
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    bronze-1: '#fdfcfc'
    bronze-2: '#fdf7f5'
    bronze-3: '#f6edea'
    bronze-4: '#efe4df'
    bronze-5: '#e7d9d3'
    bronze-6: '#dfcdc5'
    bronze-7: '#d3bcb3'
    bronze-8: '#c2a499'
    bronze-9: '#a18072'
    bronze-10: '#957468'
    bronze-11: '#7d5e54'
    bronze-12: '#43302b'
  dark:
    bronze-1: '#141110'
    bronze-2: '#1c1917'
    bronze-3: '#262220'
    bronze-4: '#302a27'
    bronze-5: '#3b3330'
    bronze-6: '#493e3a'
    bronze-7: '#5a4c47'
    bronze-8: '#6f5f58'
    bronze-9: '#a18072'
    bronze-10: '#ae8c7e'
    bronze-11: '#d4b3a5'
    bronze-12: '#ede0d9'
---

## Radix Bronze

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

Every step also ships as an alpha variant (`bronze-a1` … `bronze-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `bronze-alpha.css` / `bronze-dark-alpha.css`.
