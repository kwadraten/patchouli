---
name: Radix Amber
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    amber-1: '#fefdfb'
    amber-2: '#fefbe9'
    amber-3: '#fff7c2'
    amber-4: '#ffee9c'
    amber-5: '#fbe577'
    amber-6: '#f3d673'
    amber-7: '#e9c162'
    amber-8: '#e2a336'
    amber-9: '#ffc53d'
    amber-10: '#ffba18'
    amber-11: '#ab6400'
    amber-12: '#4f3422'
  dark:
    amber-1: '#16120c'
    amber-2: '#1d180f'
    amber-3: '#302008'
    amber-4: '#3f2700'
    amber-5: '#4d3000'
    amber-6: '#5c3d05'
    amber-7: '#714f19'
    amber-8: '#8f6424'
    amber-9: '#ffc53d'
    amber-10: '#ffd60a'
    amber-11: '#ffca16'
    amber-12: '#ffe7b3'
---

## Radix Amber

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

Every step also ships as an alpha variant (`amber-a1` … `amber-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `amber-alpha.css` / `amber-dark-alpha.css`.
