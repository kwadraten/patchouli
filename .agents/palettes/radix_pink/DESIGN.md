---
name: Radix Pink
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    pink-1: '#fffcfe'
    pink-2: '#fef7fb'
    pink-3: '#fee9f5'
    pink-4: '#fbdcef'
    pink-5: '#f6cee7'
    pink-6: '#efbfdd'
    pink-7: '#e7acd0'
    pink-8: '#dd93c2'
    pink-9: '#d6409f'
    pink-10: '#cf3897'
    pink-11: '#c2298a'
    pink-12: '#651249'
  dark:
    pink-1: '#191117'
    pink-2: '#21121d'
    pink-3: '#37172f'
    pink-4: '#4b143d'
    pink-5: '#591c47'
    pink-6: '#692955'
    pink-7: '#833869'
    pink-8: '#a84885'
    pink-9: '#d6409f'
    pink-10: '#de51a8'
    pink-11: '#ff8dcc'
    pink-12: '#fdd1ea'
---

## Radix Pink

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

Every step also ships as an alpha variant (`pink-a1` … `pink-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `pink-alpha.css` / `pink-dark-alpha.css`.
