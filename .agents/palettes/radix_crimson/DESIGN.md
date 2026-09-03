---
name: Radix Crimson
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    crimson-1: '#fffcfd'
    crimson-2: '#fef7f9'
    crimson-3: '#ffe9f0'
    crimson-4: '#fedce7'
    crimson-5: '#facedd'
    crimson-6: '#f3bed1'
    crimson-7: '#eaacc3'
    crimson-8: '#e093b2'
    crimson-9: '#e93d82'
    crimson-10: '#df3478'
    crimson-11: '#cb1d63'
    crimson-12: '#621639'
  dark:
    crimson-1: '#191114'
    crimson-2: '#201318'
    crimson-3: '#381525'
    crimson-4: '#4d122f'
    crimson-5: '#5c1839'
    crimson-6: '#6d2545'
    crimson-7: '#873356'
    crimson-8: '#b0436e'
    crimson-9: '#e93d82'
    crimson-10: '#ee518a'
    crimson-11: '#ff92ad'
    crimson-12: '#fdd3e8'
---

## Radix Crimson

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

Every step also ships as an alpha variant (`crimson-a1` … `crimson-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `crimson-alpha.css` / `crimson-dark-alpha.css`.
