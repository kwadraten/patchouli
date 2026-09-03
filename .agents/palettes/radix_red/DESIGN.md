---
name: Radix Red
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    red-1: '#fffcfc'
    red-2: '#fff7f7'
    red-3: '#feebec'
    red-4: '#ffdbdc'
    red-5: '#ffcdce'
    red-6: '#fdbdbe'
    red-7: '#f4a9aa'
    red-8: '#eb8e90'
    red-9: '#e5484d'
    red-10: '#dc3e42'
    red-11: '#ce2c31'
    red-12: '#641723'
  dark:
    red-1: '#191111'
    red-2: '#201314'
    red-3: '#3b1219'
    red-4: '#500f1c'
    red-5: '#611623'
    red-6: '#72232d'
    red-7: '#8c333a'
    red-8: '#b54548'
    red-9: '#e5484d'
    red-10: '#ec5d5e'
    red-11: '#ff9592'
    red-12: '#ffd1d9'
---

## Radix Red

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

Every step also ships as an alpha variant (`red-a1` … `red-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `red-alpha.css` / `red-dark-alpha.css`.
