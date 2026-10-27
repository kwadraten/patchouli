#set page(width: 210mm, height: 297mm, margin: 18mm)
#set text(font: "Arial", size: 12pt)

The following paragraph mixes left-to-right English with an
embedded right-to-left phrase: the word for library is
#text(dir: rtl)[مكتبة] in Arabic script, and the sentence then
continues in left-to-right order until the end of the line.

#v(4mm)
#text(dir: rtl)[
  هذا السطر العربي كامل من اليمين إلى اليسار، وفيه كلمات
  متعددة يجب أن تبقى في ترتيبها المنطقي الصحيح.
]

#v(4mm)
After the Arabic block, the document returns to a normal
English paragraph that ends at the BOTTOM of this mixed page.
