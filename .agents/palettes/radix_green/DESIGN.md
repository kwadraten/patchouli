---
name: Radix Green
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    green-1: '#fbfefc'
    green-2: '#f4fbf6'
    green-3: '#e6f6eb'
    green-4: '#d6f1df'
    green-5: '#c4e8d1'
    green-6: '#adddc0'
    green-7: '#8eceaa'
    green-8: '#5bb98b'
    green-9: '#30a46c'
    green-10: '#2b9a66'
    green-11: '#218358'
    green-12: '#193b2d'
  dark:
    green-1: '#0e1512'
    green-2: '#121b17'
    green-3: '#132d21'
    green-4: '#113b29'
    green-5: '#174933'
    green-6: '#20573e'
    green-7: '#28684a'
    green-8: '#2f7c57'
    green-9: '#30a46c'
    green-10: '#33b074'
    green-11: '#3dd68c'
    green-12: '#b1f1cb'
---

## Radix Green

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

Every step also ships as an alpha variant (`green-a1` … `green-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `green-alpha.css` / `green-dark-alpha.css`.
