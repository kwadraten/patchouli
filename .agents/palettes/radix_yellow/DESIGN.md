---
name: Radix Yellow
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    yellow-1: '#fdfdf9'
    yellow-2: '#fefce9'
    yellow-3: '#fffab8'
    yellow-4: '#fff394'
    yellow-5: '#ffe770'
    yellow-6: '#f3d768'
    yellow-7: '#e4c767'
    yellow-8: '#d5ae39'
    yellow-9: '#ffe629'
    yellow-10: '#ffdc00'
    yellow-11: '#9e6c00'
    yellow-12: '#473b1f'
  dark:
    yellow-1: '#14120b'
    yellow-2: '#1b180f'
    yellow-3: '#2d2305'
    yellow-4: '#362b00'
    yellow-5: '#433500'
    yellow-6: '#524202'
    yellow-7: '#665417'
    yellow-8: '#836a21'
    yellow-9: '#ffe629'
    yellow-10: '#ffff57'
    yellow-11: '#f5e147'
    yellow-12: '#f6eeb4'
---

## Radix Yellow

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

Every step also ships as an alpha variant (`yellow-a1` … `yellow-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `yellow-alpha.css` / `yellow-dark-alpha.css`.
