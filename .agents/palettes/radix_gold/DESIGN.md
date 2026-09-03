---
name: Radix Gold
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    gold-1: '#fdfdfc'
    gold-2: '#faf9f2'
    gold-3: '#f2f0e7'
    gold-4: '#eae6db'
    gold-5: '#e1dccf'
    gold-6: '#d8d0bf'
    gold-7: '#cbc0aa'
    gold-8: '#b9a88d'
    gold-9: '#978365'
    gold-10: '#8c7a5e'
    gold-11: '#71624b'
    gold-12: '#3b352b'
  dark:
    gold-1: '#121211'
    gold-2: '#1b1a17'
    gold-3: '#24231f'
    gold-4: '#2d2b26'
    gold-5: '#38352e'
    gold-6: '#444039'
    gold-7: '#544f46'
    gold-8: '#696256'
    gold-9: '#978365'
    gold-10: '#a39073'
    gold-11: '#cbb99f'
    gold-12: '#e8e2d9'
---

## Radix Gold

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

Every step also ships as an alpha variant (`gold-a1` … `gold-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `gold-alpha.css` / `gold-dark-alpha.css`.
