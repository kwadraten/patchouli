---
name: Academic Precision
colors:
  surface: '#fbf9f8'
  surface-dim: '#dbdad9'
  surface-bright: '#fbf9f8'
  surface-container-lowest: '#ffffff'
  surface-container-low: '#f5f3f3'
  surface-container: '#efeded'
  surface-container-high: '#e9e8e7'
  surface-container-highest: '#e4e2e2'
  on-surface: '#1b1c1c'
  on-surface-variant: '#484553'
  inverse-surface: '#303031'
  inverse-on-surface: '#f2f0f0'
  outline: '#797584'
  outline-variant: '#cac4d5'
  surface-tint: '#6249c2'
  primary: '#553bb5'
  on-primary: '#ffffff'
  primary-container: '#6e56cf'
  on-primary-container: '#efe8ff'
  inverse-primary: '#cbbeff'
  secondary: '#614abf'
  on-secondary: '#ffffff'
  secondary-container: '#9c87ff'
  on-secondary-container: '#320f90'
  tertiary: '#51515c'
  on-tertiary: '#ffffff'
  tertiary-container: '#696974'
  on-tertiary-container: '#eceaf7'
  error: '#ba1a1a'
  on-error: '#ffffff'
  error-container: '#ffdad6'
  on-error-container: '#93000a'
  primary-fixed: '#e6deff'
  primary-fixed-dim: '#cbbeff'
  on-primary-fixed: '#1d0061'
  on-primary-fixed-variant: '#4a2ea9'
  secondary-fixed: '#e6deff'
  secondary-fixed-dim: '#cabeff'
  on-secondary-fixed: '#1d0061'
  on-secondary-fixed-variant: '#492fa6'
  tertiary-fixed: '#e3e1ee'
  tertiary-fixed-dim: '#c6c5d2'
  on-tertiary-fixed: '#1a1b24'
  on-tertiary-fixed-variant: '#464650'
  background: '#fbf9f8'
  on-background: '#1b1c1c'
  surface-variant: '#e4e2e2'
typography:
  display:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.02em
  headline-md:
    fontFamily: Inter
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 24px
  headline-sm:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '600'
    lineHeight: 20px
  body-lg:
    fontFamily: Inter
    fontSize: 16px
    fontWeight: '400'
    lineHeight: 24px
  body-md:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
  body-sm:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 18px
  label-md:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '500'
    lineHeight: 16px
  code-sm:
    fontFamily: JetBrains Mono
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 16px
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  sidebar-width: 260px
  detail-panel-width: 340px
  gutter: 1rem
  stack-compact: 0.25rem
  stack-default: 0.75rem
  container-padding: 1.5rem
---

## Brand & Style

This design system is engineered for high-density information environments. It adopts a **Modern Corporate** aesthetic with a lean toward **Minimalism**, prioritizing content legibility and functional efficiency over decorative elements. The target audience consists of researchers, academics, and students who require a dependable, high-end productivity tool that feels like a professional extension of their workflow.

The emotional response should be one of "effortless organization" and "intellectual clarity." By utilizing a refined violet primary accent against a structured neutral backdrop, the UI remains unobtrusive during deep work while providing clear affordances for navigation and data manipulation.

## Colors

The color system is derived from a sophisticated Violet Radix palette. 

- **Primary (#6E56CF):** Used for primary actions, active states, and high-level navigation markers.
- **Secondary (#7C66DC):** A slightly lighter shade used for hover states and secondary interactive elements.
- **Tertiary (#F4F2FF):** A very soft violet tint used for row highlights, selected background states, and subtle badges.
- **Neutral (#646464):** The core for secondary text and icons, ensuring the interface feels grounded and professional.

The background uses a tiered gray scale (Radix Gray 1-3) to separate the sidebar, main content area, and detail panels without relying on heavy borders.

## Typography

The system utilizes **Inter** for its exceptional legibility in data-heavy environments. To handle complex metadata (DOIs, ISBNs, BibTeX keys), **JetBrains Mono** is introduced for technical labels and identifiers.

- **Hierarchical Density:** We use a smaller base size (13px/14px) to maximize the information visible on screen without sacrificing scanability.
- **Contrast:** Bold weights are reserved for headers and active navigation items.
- **Metadata:** Use `body-sm` for secondary list information and `label-md` for field headers in the detail panel.

## Layout & Spacing

This is a **Desktop-First, Fixed-Fluid-Fixed** layout. It consists of three primary vertical zones:
1.  **Sidebar (Fixed):** Left-aligned navigation for libraries, collections, and tags.
2.  **Main Content (Fluid):** The central data table displaying the reference list.
3.  **Detail Panel (Fixed/Collapsible):** Right-aligned panel for metadata editing and PDF previewing.

A tight 4px baseline grid is used to manage vertical rhythm. Spacing is intentionally compact to allow users to view more rows of data simultaneously. Gutters between major panels are minimal (1px borders or 8px gaps) to maintain a unified "tool" feel.

## Elevation & Depth

Hierarchy is achieved through **Tonal Layers** rather than heavy shadows.

- **Level 0 (Base):** The main content area background (White).
- **Level 1 (Subtle Inset):** Sidebars and detail panels use a very light gray (#F9F9FB) to create a clear functional distinction.
- **Level 2 (Active Overlay):** Modals and dropdown menus use a refined, low-opacity ambient shadow (0px 4px 12px rgba(0,0,0,0.08)) to float above the workspace.
- **Dividers:** 1px solid lines (#E2E2E5) are used extensively to define the grid of the data table and the sections of the detail panel.

## Shapes

The shape language is **Soft (Level 1)**. 

- **Elements:** Buttons, input fields, and tags use a 4px (0.25rem) corner radius. This provides a modern touch without appearing overly "bubbly" or consumer-grade.
- **Containers:** Internal panels and cards maintain sharp or slightly softened corners (4px) to preserve the structural integrity of the grid.
- **Selection:** Selected rows in the data table use a 0px radius to maintain a continuous horizontal line across the screen.

## Components

### Buttons & Inputs
- **Primary Action:** Solid Violet (#6E56CF) with white text.
- **Ghost Actions:** Transparent background with Violet text, used for toolbar items (e.g., "Add Reference").
- **Inputs:** High-contrast borders that turn Violet on focus. Use a "flush" style within the detail panel to look like editable text.

### Data Tables (The Core)
- **Header:** Sticky top row with `label-md` typography and subtle sort icons.
- **Rows:** Alternating subtle zebra striping or pure white with 1px dividers. Use `tertiary_color` for the "Active" row highlight.
- **Cells:** Text-overflow should be handled via ellipsis. Provide specific column alignments for dates and page numbers.

### Sidebar & Tabs
- **Navigation:** Use a "Plank" style for active states—a vertical violet bar (2px) on the left edge of the selected navigation item.
- **Tabs:** Flat style tabs used in the detail panel to switch between "Info," "Notes," and "Tags."

### Status Bar
- Located at the very bottom, using a dark neutral background (#2E2E2E) or light gray (#F1F1F3) to provide sync status, item counts, and system alerts.