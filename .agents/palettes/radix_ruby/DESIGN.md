---
name: Radix Ruby
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    ruby-1: '#fffcfd'
    ruby-2: '#fff7f8'
    ruby-3: '#feeaed'
    ruby-4: '#ffdce1'
    ruby-5: '#ffced6'
    ruby-6: '#f8bfc8'
    ruby-7: '#efacb8'
    ruby-8: '#e592a3'
    ruby-9: '#e54666'
    ruby-10: '#dc3b5d'
    ruby-11: '#ca244d'
    ruby-12: '#64172b'
  dark:
    ruby-1: '#191113'
    ruby-2: '#1e1517'
    ruby-3: '#3a141e'
    ruby-4: '#4e1325'
    ruby-5: '#5e1a2e'
    ruby-6: '#6f2539'
    ruby-7: '#883447'
    ruby-8: '#b3445a'
    ruby-9: '#e54666'
    ruby-10: '#ec5a72'
    ruby-11: '#ff949d'
    ruby-12: '#fed2e1'
---

## Radix Ruby

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

Every step also ships as an alpha variant (`ruby-a1` … `ruby-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `ruby-alpha.css` / `ruby-dark-alpha.css`.
