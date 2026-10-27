#set page(width: 210mm, height: 297mm, margin: 14mm)
#set text(size: 10pt)

English horizontal text at the top of the page introduces the
document. This paragraph runs left to right as usual.

#v(4mm)
#let vline(s) = stack(dir: ttb, spacing: 0.35em, ..s.clusters().map(c => [#c]))
#grid(
  columns: (1fr, auto),
  gutter: 8mm,
  [
    The block on the right of this row is vertical Japanese with
    #set text(font: "MS Mincho")
    upright glyphs. A mixed page like this is common in bilingual
    magazines and academic layouts. The reading flow visits the
    horizontal column first, then the vertical one.
  ],
  box(height: 70mm, stack(dir: rtl, spacing: 1.3em,
    text(font: "MS Mincho", size: 10pt, vline("桜の花は春の象徴である。")),
    text(font: "MS Mincho", size: 10pt, vline("毎年多くの人々が公園や")),
    text(font: "MS Mincho", size: 10pt, vline("川沿いに集まり花見を楽しむ。")),
    text(font: "MS Mincho", size: 10pt, vline("桜前線は南から北へ移る。")),
  )),
)
