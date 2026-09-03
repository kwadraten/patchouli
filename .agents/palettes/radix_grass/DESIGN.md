---
name: Radix Grass
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    grass-1: '#fbfefb'
    grass-2: '#f5fbf5'
    grass-3: '#e9f6e9'
    grass-4: '#daf1db'
    grass-5: '#c9e8ca'
    grass-6: '#b2ddb5'
    grass-7: '#94ce9a'
    grass-8: '#65ba74'
    grass-9: '#46a758'
    grass-10: '#3e9b4f'
    grass-11: '#2a7e3b'
    grass-12: '#203c25'
  dark:
    grass-1: '#0e1511'
    grass-2: '#141a15'
    grass-3: '#1b2a1e'
    grass-4: '#1d3a24'
    grass-5: '#25482d'
    grass-6: '#2d5736'
    grass-7: '#366740'
    grass-8: '#3e7949'
    grass-9: '#46a758'
    grass-10: '#53b365'
    grass-11: '#71d083'
    grass-12: '#c2f0c2'
---

## Radix Grass

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

Every step also ships as an alpha variant (`grass-a1` … `grass-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `grass-alpha.css` / `grass-dark-alpha.css`.
