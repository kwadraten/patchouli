#set page(width: 210mm, height: 297mm, margin: 15mm)
#set text(size: 10pt)
#align(center + horizon, rotate(90deg, reflow: true)[
  #block(width: 200mm)[
    This English paragraph has been rotated ninety degrees, so every
    glyph carries a vertical writing angle. An analyzer must read the
    column from bottom to top, matching vertical-lr direction. The
    quick brown fox jumps over the lazy dog repeatedly here.
  ]
])
