#!/usr/bin/env python3
"""Hand-crafts corrupt-unicode.pdf: glyphs render visibly but carry no usable
Unicode mapping (custom /Encoding with private glyph names, no ToUnicode), so
PDFium flags every char with NoUnicodeMapping/UnicodeMapError."""
import zlib

PAGE_W, PAGE_H = 595.0, 842.0

# Two text lines: codes 1..20 map to invented glyph names g1..g20
chars = list(range(1, 21))
diffs = "[" + " ".join(f"/g{c}" for c in chars) + "]"

lines = [
    b"BT /F1 24 Tf 72 720 Td <" + bytes(chars[:14]).hex().encode() + b"> Tj ET",
    b"BT /F1 24 Tf 72 660 Td <" + bytes(chars[14:] + chars[:8]).hex().encode() + b"> Tj ET",
    b"BT /F1 24 Tf 72 600 Td <" + bytes(chars[4:20]).hex().encode() + b"> Tj ET",
]
content = b"\n".join(lines)
content_stream = zlib.compress(content)

objects = []

def obj(n, body):
    objects.append((n, body))

obj(1, b"<< /Type /Catalog /Pages 2 0 R >>")
obj(2, b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
obj(3, (f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PAGE_W} {PAGE_H}] "
        "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>").encode())
obj(4, ("<< /Type /Font /Subtype /Type1 /BaseFont /Courier "
        f"/FirstChar 1 /LastChar {len(chars)} /Widths " +
        "[" + " ".join(["600"] * len(chars)) + "] " +
        f"/Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [1 {diffs[1:]} "
        ">> >>").encode())
obj(5, f"<< /Length {len(content_stream)} /Filter /FlateDecode >>".encode() +
    b"\nstream\n" + content_stream + b"\nendstream")

out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
offsets = [0] * (len(objects) + 1)
for n, body in objects:
    offsets[n] = len(out)
    out += f"{n} 0 obj\n".encode() + body + b"\nendobj\n"
xref_pos = len(out)
out += f"xref\n0 {len(objects)+1}\n".encode()
out += b"0000000000 65535 f \n"
for n in range(1, len(objects) + 1):
    out += f"{offsets[n]:010d} 00000 n \n".encode()
out += (f"trailer\n<< /Size {len(objects)+1} /Root 1 0 R >>\n"
        f"startxref\n{xref_pos}\n%%EOF\n").encode()

with open("corrupt-unicode.pdf", "wb") as f:
    f.write(out)
print(f"wrote corrupt-unicode.pdf ({len(out)} bytes)")
