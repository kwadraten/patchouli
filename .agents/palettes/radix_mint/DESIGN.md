---
name: Radix Mint
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    mint-1: '#f9fefd'
    mint-2: '#f2fbf9'
    mint-3: '#ddf9f2'
    mint-4: '#c8f4e9'
    mint-5: '#b3ecde'
    mint-6: '#9ce0d0'
    mint-7: '#7ecfbd'
    mint-8: '#4cbba5'
    mint-9: '#86ead4'
    mint-10: '#7de0cb'
    mint-11: '#027864'
    mint-12: '#16433c'
  dark:
    mint-1: '#0e1515'
    mint-2: '#0f1b1b'
    mint-3: '#092c2b'
    mint-4: '#003a38'
    mint-5: '#004744'
    mint-6: '#105650'
    mint-7: '#1e685f'
    mint-8: '#277f70'
    mint-9: '#86ead4'
    mint-10: '#a8f5e5'
    mint-11: '#58d5ba'
    mint-12: '#c4f5e1'
---

## Radix Mint

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

Every step also ships as an alpha variant (`mint-a1` … `mint-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `mint-alpha.css` / `mint-dark-alpha.css`.
