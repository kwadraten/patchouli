#set page(width: 210mm, height: 297mm, margin: 18mm)
#set text(font: "SimSun", size: 11pt)
#let vline(s) = stack(dir: ttb, spacing: 0.35em, ..s.clusters().map(c => [#c]))
#align(top + right, stack(dir: rtl, spacing: 1.4em,
  vline("大学之道，在明明德，"),
  vline("在亲民，在止于至善。"),
  vline("知止而后有定，定而后能静，"),
  vline("静而后能安，安而后能虑，"),
  vline("虑而后能得。物有本末，"),
  vline("事有终始，知所先后，"),
  vline("则近道矣。古之欲明明德"),
  vline("于天下者，先治其国；"),
  vline("欲治其国者，先齐其家；"),
  vline("欲齐其家者，先修其身；"),
  vline("欲修其身者，先正其心；"),
  vline("欲正其心者，先诚其意。"),
))
