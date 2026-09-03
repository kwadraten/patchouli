---
name: Radix Mauve
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    mauve-1: '#fdfcfd'
    mauve-2: '#faf9fb'
    mauve-3: '#f2eff3'
    mauve-4: '#eae7ec'
    mauve-5: '#e3dfe6'
    mauve-6: '#dbd8e0'
    mauve-7: '#d0cdd7'
    mauve-8: '#bcbac7'
    mauve-9: '#8e8c99'
    mauve-10: '#84828e'
    mauve-11: '#65636d'
    mauve-12: '#211f26'
  dark:
    mauve-1: '#121113'
    mauve-2: '#1a191b'
    mauve-3: '#232225'
    mauve-4: '#2b292d'
    mauve-5: '#323035'
    mauve-6: '#3c393f'
    mauve-7: '#49474e'
    mauve-8: '#625f69'
    mauve-9: '#6f6d78'
    mauve-10: '#7c7a85'
    mauve-11: '#b5b2bc'
    mauve-12: '#eeeef0'
---

## Radix Mauve

A 12-step gray scale from [Radix Colors](https://www.radix-ui.com/colors) (`@radix-ui/colors` 3.0.0). Each step targets a specific UI use case, so steps can be composed without hand-tuning: text on steps 11/12 is guaranteed to meet APCA contrast targets against the background steps (1-5) of the same scale.

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

Every step also ships as an alpha variant (`mauve-a1` … `mauve-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `mauve-alpha.css` / `mauve-dark-alpha.css`.

### Pairing

This is one of the six Radix grays. Pair it with an accent scale that shares its undertone: `gray` is pure neutral, `mauve` leans purple, `slate` leans blue, `sage` leans green, `olive` leans yellow-green, `sand` leans warm/yellow.
