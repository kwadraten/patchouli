#set page(width: 210mm, height: 297mm, margin: 18mm)
#set text(font: "MS Mincho", size: 11pt)
#let vline(s) = stack(dir: ttb, spacing: 0.35em, ..s.clusters().map(c => [#c]))
#align(top + right, stack(dir: rtl, spacing: 1.4em,
  vline("吾輩は猫である。名前はまだない。"),
  vline("どこで生れたかとんと見当がつかぬ。"),
  vline("何でも薄暗いじめじめした所で"),
  vline("ニャーニャー泣いていた事だけは"),
  vline("記憶している。吾輩はここで始めて"),
  vline("人間というものを見た。しかも"),
  vline("あとで聞くとそれは書生という"),
  vline("人間中で一番獰悪な種族であった"),
  vline("そうだ。この書生というのは"),
  vline("時々我々を捕えて煮て食うという"),
  vline("話である。しかしその当時は"),
  vline("何という考もなかったから別段"),
  vline("恐しいとも思わなかった。"),
))
