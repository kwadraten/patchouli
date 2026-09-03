---
name: Radix Violet
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    violet-1: '#fdfcfe'
    violet-2: '#faf8ff'
    violet-3: '#f4f0fe'
    violet-4: '#ebe4ff'
    violet-5: '#e1d9ff'
    violet-6: '#d4cafe'
    violet-7: '#c2b5f5'
    violet-8: '#aa99ec'
    violet-9: '#6e56cf'
    violet-10: '#654dc4'
    violet-11: '#6550b9'
    violet-12: '#2f265f'
  dark:
    violet-1: '#14121f'
    violet-2: '#1b1525'
    violet-3: '#291f43'
    violet-4: '#33255b'
    violet-5: '#3c2e69'
    violet-6: '#473876'
    violet-7: '#56468b'
    violet-8: '#6958ad'
    violet-9: '#6e56cf'
    violet-10: '#7d66d9'
    violet-11: '#baa7ff'
    violet-12: '#e2ddfe'
---

## Radix Violet

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

Every step also ships as an alpha variant (`violet-a1` … `violet-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `violet-alpha.css` / `violet-dark-alpha.css`.
