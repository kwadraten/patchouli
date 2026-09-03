---
name: Radix Iris
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    iris-1: '#fdfdff'
    iris-2: '#f8f8ff'
    iris-3: '#f0f1fe'
    iris-4: '#e6e7ff'
    iris-5: '#dadcff'
    iris-6: '#cbcdff'
    iris-7: '#b8baf8'
    iris-8: '#9b9ef0'
    iris-9: '#5b5bd6'
    iris-10: '#5151cd'
    iris-11: '#5753c6'
    iris-12: '#272962'
  dark:
    iris-1: '#13131e'
    iris-2: '#171625'
    iris-3: '#202248'
    iris-4: '#262a65'
    iris-5: '#303374'
    iris-6: '#3d3e82'
    iris-7: '#4a4a95'
    iris-8: '#5958b1'
    iris-9: '#5b5bd6'
    iris-10: '#6e6ade'
    iris-11: '#b1a9ff'
    iris-12: '#e0dffe'
---

## Radix Iris

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

Every step also ships as an alpha variant (`iris-a1` … `iris-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `iris-alpha.css` / `iris-dark-alpha.css`.
