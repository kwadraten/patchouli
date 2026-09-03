---
name: Radix Jade
source: '@radix-ui/colors 3.0.0'
colors:
  light:
    jade-1: '#fbfefd'
    jade-2: '#f4fbf7'
    jade-3: '#e6f7ed'
    jade-4: '#d6f1e3'
    jade-5: '#c3e9d7'
    jade-6: '#acdec8'
    jade-7: '#8bceb6'
    jade-8: '#56ba9f'
    jade-9: '#29a383'
    jade-10: '#26997b'
    jade-11: '#208368'
    jade-12: '#1d3b31'
  dark:
    jade-1: '#0d1512'
    jade-2: '#121c18'
    jade-3: '#0f2e22'
    jade-4: '#0b3b2c'
    jade-5: '#114837'
    jade-6: '#1b5745'
    jade-7: '#246854'
    jade-8: '#2a7e68'
    jade-9: '#29a383'
    jade-10: '#27b08b'
    jade-11: '#1fd8a4'
    jade-12: '#adf0d4'
---

## Radix Jade

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

Every step also ships as an alpha variant (`jade-a1` … `jade-a12`) for layering over colored backgrounds. Alpha values are not duplicated here; see the source package's `jade-alpha.css` / `jade-dark-alpha.css`.
