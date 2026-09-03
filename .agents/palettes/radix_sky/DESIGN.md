---
name: Radix Sky
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    sky-1: '#f9feff'
    sky-2: '#f1fafd'
    sky-3: '#e1f6fd'
    sky-4: '#d1f0fa'
    sky-5: '#bee7f5'
    sky-6: '#a9daed'
    sky-7: '#8dcae3'
    sky-8: '#60b3d7'
    sky-9: '#7ce2fe'
    sky-10: '#74daf8'
    sky-11: '#00749e'
    sky-12: '#1d3e56'
  dark:
    sky-1: '#0d141f'
    sky-2: '#111a27'
    sky-3: '#112840'
    sky-4: '#113555'
    sky-5: '#154467'
    sky-6: '#1b537b'
    sky-7: '#1f6692'
    sky-8: '#197cae'
    sky-9: '#7ce2fe'
    sky-10: '#a8eeff'
    sky-11: '#75c7f0'
    sky-12: '#c2f3ff'
---

## Radix Sky

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

Every step also ships as an alpha variant (`sky-a1` … `sky-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `sky-alpha.css` / `sky-dark-alpha.css`.
