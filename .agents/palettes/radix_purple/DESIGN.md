---
name: Radix Purple
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    purple-1: '#fefcfe'
    purple-2: '#fbf7fe'
    purple-3: '#f7edfe'
    purple-4: '#f2e2fc'
    purple-5: '#ead5f9'
    purple-6: '#e0c4f4'
    purple-7: '#d1afec'
    purple-8: '#be93e4'
    purple-9: '#8e4ec6'
    purple-10: '#8347b9'
    purple-11: '#8145b5'
    purple-12: '#402060'
  dark:
    purple-1: '#18111b'
    purple-2: '#1e1523'
    purple-3: '#301c3b'
    purple-4: '#3d224e'
    purple-5: '#48295c'
    purple-6: '#54346b'
    purple-7: '#664282'
    purple-8: '#8457aa'
    purple-9: '#8e4ec6'
    purple-10: '#9a5cd0'
    purple-11: '#d19dff'
    purple-12: '#ecd9fa'
---

## Radix Purple

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

Every step also ships as an alpha variant (`purple-a1` … `purple-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `purple-alpha.css` / `purple-dark-alpha.css`.
