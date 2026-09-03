---
name: Radix Orange
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    orange-1: '#fefcfb'
    orange-2: '#fff7ed'
    orange-3: '#ffefd6'
    orange-4: '#ffdfb5'
    orange-5: '#ffd19a'
    orange-6: '#ffc182'
    orange-7: '#f5ae73'
    orange-8: '#ec9455'
    orange-9: '#f76b15'
    orange-10: '#ef5f00'
    orange-11: '#cc4e00'
    orange-12: '#582d1d'
  dark:
    orange-1: '#17120e'
    orange-2: '#1e160f'
    orange-3: '#331e0b'
    orange-4: '#462100'
    orange-5: '#562800'
    orange-6: '#66350c'
    orange-7: '#7e451d'
    orange-8: '#a35829'
    orange-9: '#f76b15'
    orange-10: '#ff801f'
    orange-11: '#ffa057'
    orange-12: '#ffe0c2'
---

## Radix Orange

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

Every step also ships as an alpha variant (`orange-a1` … `orange-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `orange-alpha.css` / `orange-dark-alpha.css`.
