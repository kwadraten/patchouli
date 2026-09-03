---
name: Radix Cyan
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    cyan-1: '#fafdfe'
    cyan-2: '#f2fafb'
    cyan-3: '#def7f9'
    cyan-4: '#caf1f6'
    cyan-5: '#b5e9f0'
    cyan-6: '#9ddde7'
    cyan-7: '#7dcedc'
    cyan-8: '#3db9cf'
    cyan-9: '#00a2c7'
    cyan-10: '#0797b9'
    cyan-11: '#107d98'
    cyan-12: '#0d3c48'
  dark:
    cyan-1: '#0b161a'
    cyan-2: '#101b20'
    cyan-3: '#082c36'
    cyan-4: '#003848'
    cyan-5: '#004558'
    cyan-6: '#045468'
    cyan-7: '#12677e'
    cyan-8: '#11809c'
    cyan-9: '#00a2c7'
    cyan-10: '#23afd0'
    cyan-11: '#4ccce6'
    cyan-12: '#b6ecf7'
---

## Radix Cyan

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

Every step also ships as an alpha variant (`cyan-a1` … `cyan-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `cyan-alpha.css` / `cyan-dark-alpha.css`.
