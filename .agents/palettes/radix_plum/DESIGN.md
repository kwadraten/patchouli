---
name: Radix Plum
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    plum-1: '#fefcff'
    plum-2: '#fdf7fd'
    plum-3: '#fbebfb'
    plum-4: '#f7def8'
    plum-5: '#f2d1f3'
    plum-6: '#e9c2ec'
    plum-7: '#deade3'
    plum-8: '#cf91d8'
    plum-9: '#ab4aba'
    plum-10: '#a144af'
    plum-11: '#953ea3'
    plum-12: '#53195d'
  dark:
    plum-1: '#181118'
    plum-2: '#201320'
    plum-3: '#351a35'
    plum-4: '#451d47'
    plum-5: '#512454'
    plum-6: '#5e3061'
    plum-7: '#734079'
    plum-8: '#92549c'
    plum-9: '#ab4aba'
    plum-10: '#b658c4'
    plum-11: '#e796f3'
    plum-12: '#f4d4f4'
---

## Radix Plum

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

Every step also ships as an alpha variant (`plum-a1` … `plum-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `plum-alpha.css` / `plum-dark-alpha.css`.
