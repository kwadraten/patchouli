---
name: Radix Indigo
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    indigo-1: '#fdfdfe'
    indigo-2: '#f7f9ff'
    indigo-3: '#edf2fe'
    indigo-4: '#e1e9ff'
    indigo-5: '#d2deff'
    indigo-6: '#c1d0ff'
    indigo-7: '#abbdf9'
    indigo-8: '#8da4ef'
    indigo-9: '#3e63dd'
    indigo-10: '#3358d4'
    indigo-11: '#3a5bc7'
    indigo-12: '#1f2d5c'
  dark:
    indigo-1: '#11131f'
    indigo-2: '#141726'
    indigo-3: '#182449'
    indigo-4: '#1d2e62'
    indigo-5: '#253974'
    indigo-6: '#304384'
    indigo-7: '#3a4f97'
    indigo-8: '#435db1'
    indigo-9: '#3e63dd'
    indigo-10: '#5472e4'
    indigo-11: '#9eb1ff'
    indigo-12: '#d6e1ff'
---

## Radix Indigo

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

Every step also ships as an alpha variant (`indigo-a1` … `indigo-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `indigo-alpha.css` / `indigo-dark-alpha.css`.
