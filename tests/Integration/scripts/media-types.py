#!/usr/bin/env python3
"""Writes the 0.7 test files that ffmpeg cannot: EXIF in a JPEG, EPUB, PDF and CBZ.

Standard library only, with fixed timestamps, so the same input gives the same bytes.
Called by prepare-media.sh inside a container; paths are relative to its /work mount.
"""

import struct
import sys
import zipfile

ZIP_TIME = (2026, 1, 1, 0, 0, 0)

# TIFF field types and their sizes in bytes.
BYTE, ASCII, SHORT, LONG, RATIONAL = 1, 2, 3, 4, 5
SIZES = {BYTE: 1, ASCII: 1, SHORT: 2, LONG: 4, RATIONAL: 8}


def field(tag, kind, value):
    """One IFD entry as (tag, type, count, payload)."""
    if kind == ASCII:
        payload = value.encode() + b"\0"
    elif kind == BYTE:
        payload = bytes(value)
    elif kind == SHORT:
        payload = b"".join(struct.pack("<H", v) for v in value)
    elif kind == LONG:
        payload = b"".join(struct.pack("<I", v) for v in value)
    else:
        payload = b"".join(struct.pack("<II", n, d) for n, d in value)
    return (tag, kind, len(payload) // SIZES[kind], payload)


def ifd_size(entries):
    data = sum(len(p) + len(p) % 2 for _, _, _, p in entries if len(p) > 4)
    return 2 + 12 * len(entries) + 4 + data


def ifd_bytes(entries, offset):
    """An IFD placed at offset in the TIFF block, with its out-of-line values after it."""
    entries = sorted(entries)
    head = struct.pack("<H", len(entries))
    data = b""
    data_at = offset + 2 + 12 * len(entries) + 4
    for tag, kind, count, payload in entries:
        if len(payload) <= 4:
            head += struct.pack("<HHI", tag, kind, count) + payload.ljust(4, b"\0")
        else:
            head += struct.pack("<HHII", tag, kind, count, data_at + len(data))
            data += payload + (b"\0" if len(payload) % 2 else b"")
    return head + struct.pack("<I", 0) + data


def dms(value):
    value = abs(value)
    degrees = int(value)
    minutes = int((value - degrees) * 60)
    seconds = round(((value - degrees) * 60 - minutes) * 60 * 100)
    return [(degrees, 1), (minutes, 1), (seconds, 100)]


def exif(src, dst, *pairs):
    """Copies a JPEG and puts an EXIF block right after SOI. gps=lat,lon,alt adds a GPS IFD."""
    tags = dict(p.split("=", 1) for p in pairs)
    jpeg = open(src, "rb").read()
    if jpeg[:2] != b"\xff\xd8":
        sys.exit(f"{src} is not a JPEG")
    width, height = jpeg_size(jpeg)
    ifd0 = [
        field(0x010E, ASCII, tags["description"]),
        field(0x010F, ASCII, tags["make"]),
        field(0x0110, ASCII, tags["model"]),
        field(0x0112, SHORT, [1]),
        field(0x0132, ASCII, tags["date"]),
        field(0x8769, LONG, [0]),
    ]
    exif_ifd = [
        field(0x829A, RATIONAL, [(1, 250)]),
        field(0x829D, RATIONAL, [(28, 10)]),
        field(0x8827, SHORT, [int(tags.get("iso", "200"))]),
        field(0x9003, ASCII, tags["date"]),
        field(0x9004, ASCII, tags["date"]),
        field(0x920A, RATIONAL, [(50, 1)]),
        field(0xA002, LONG, [width]),
        field(0xA003, LONG, [height]),
    ]
    gps_ifd = []
    if "gps" in tags:
        lat, lon, alt = (float(v) for v in tags["gps"].split(","))
        ifd0.append(field(0x8825, LONG, [0]))
        gps_ifd = [
            field(0x0000, BYTE, [2, 3, 0, 0]),
            field(0x0001, ASCII, "N" if lat >= 0 else "S"),
            field(0x0002, RATIONAL, dms(lat)),
            field(0x0003, ASCII, "E" if lon >= 0 else "W"),
            field(0x0004, RATIONAL, dms(lon)),
            field(0x0005, BYTE, [0 if alt >= 0 else 1]),
            field(0x0006, RATIONAL, [(round(abs(alt) * 10), 10)]),
        ]
    exif_at = 8 + ifd_size(ifd0)
    gps_at = exif_at + ifd_size(exif_ifd)
    ifd0 = [e for e in ifd0 if e[0] not in (0x8769, 0x8825)] + [field(0x8769, LONG, [exif_at])]
    if gps_ifd:
        ifd0.append(field(0x8825, LONG, [gps_at]))
    tiff = b"II*\0" + struct.pack("<I", 8) + ifd_bytes(ifd0, 8) + ifd_bytes(exif_ifd, exif_at)
    if gps_ifd:
        tiff += ifd_bytes(gps_ifd, gps_at)
    body = b"Exif\0\0" + tiff
    app1 = b"\xff\xe1" + struct.pack(">H", len(body) + 2) + body
    open(dst, "wb").write(jpeg[:2] + app1 + jpeg[2:])


def jpeg_size(jpeg):
    """Width and height from the first SOF marker."""
    i = 2
    while i < len(jpeg):
        marker = jpeg[i + 1]
        length = struct.unpack(">H", jpeg[i + 2:i + 4])[0]
        if marker in (0xC0, 0xC1, 0xC2):
            height, width = struct.unpack(">HH", jpeg[i + 5:i + 9])
            return width, height
        i += 2 + length
    sys.exit("no SOF marker")


def zip_write(archive, name, data, stored=False):
    info = zipfile.ZipInfo(name, ZIP_TIME)
    info.compress_type = zipfile.ZIP_STORED if stored else zipfile.ZIP_DEFLATED
    info.external_attr = 0o644 << 16
    info.create_system = 3
    archive.writestr(info, data)


def esc(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def epub(dst, title, author, isbn, date, cover):
    """An EPUB 3 with three chapters, a cover and an ISBN in its package document."""
    chapters = [f"Capitolo {n}" for n in (1, 2, 3)]
    opf = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" xmlns:opf="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid" opf:scheme="ISBN">{isbn}</dc:identifier>
    <dc:title>{esc(title)}</dc:title>
    <dc:creator opf:role="aut">{esc(author)}</dc:creator>
    <dc:language>it</dc:language>
    <dc:date>{date}</dc:date>
    <dc:publisher>JellyMesh Test</dc:publisher>
    <dc:description>JellyMesh test book.</dc:description>
    <dc:subject>Test</dc:subject>
    <meta property="dcterms:modified">{date}T00:00:00Z</meta>
    <meta name="cover" content="cover"/>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="cover" href="cover.jpg" media-type="image/jpeg" properties="cover-image"/>
""" + "".join(f'    <item id="c{n}" href="c{n}.xhtml" media-type="application/xhtml+xml"/>\n' for n in (1, 2, 3)) + """  </manifest>
  <spine>
""" + "".join(f'    <itemref idref="c{n}"/>\n' for n in (1, 2, 3)) + """  </spine>
</package>
"""
    page = """<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>{t}</title></head>
<body>{b}</body></html>
"""
    nav = "<nav epub:type=\"toc\"><ol>" + "".join(
        f'<li><a href="c{n}.xhtml">{c}</a></li>' for n, c in enumerate(chapters, 1)) + "</ol></nav>"
    with zipfile.ZipFile(dst, "w") as archive:
        zip_write(archive, "mimetype", "application/epub+zip", stored=True)
        zip_write(archive, "META-INF/container.xml", """<?xml version="1.0" encoding="utf-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
</container>
""")
        zip_write(archive, "OEBPS/content.opf", opf)
        zip_write(archive, "OEBPS/nav.xhtml", page.format(t=esc(title), b=nav))
        zip_write(archive, "OEBPS/cover.jpg", open(cover, "rb").read(), stored=True)
        for n, chapter in enumerate(chapters, 1):
            text = "".join(f"<p>{esc(title)}, {chapter}, paragrafo {p}.</p>" for p in range(1, 41))
            zip_write(archive, f"OEBPS/c{n}.xhtml", page.format(t=chapter, b=f"<h1>{chapter}</h1>{text}"))


def pdf(dst, title, author, *pages):
    """A PDF with one JPEG per page and the page number as text, no dates."""
    objects = []

    def add(body):
        objects.append(body)
        return len(objects)

    catalog = add(None)
    tree = add(None)
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    kids = []
    for n, path in enumerate(pages, 1):
        jpeg = open(path, "rb").read()
        width, height = jpeg_size(jpeg)
        image = add(b"<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB "
                    b"/BitsPerComponent 8 /Filter /DCTDecode /Length %d >>\nstream\n" % (width, height, len(jpeg))
                    + jpeg + b"\nendstream")
        content = b"q %d 0 0 %d 0 0 cm /Im0 Do Q BT /F1 32 Tf 40 40 Td (Pagina %d) Tj ET" % (width, height, n)
        stream = add(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
        kids.append(add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 %d %d] /Contents %d 0 R "
                        b"/Resources << /XObject << /Im0 %d 0 R >> /Font << /F1 %d 0 R >> >> >>"
                        % (tree, width, height, stream, image, font)))
    objects[catalog - 1] = b"<< /Type /Catalog /Pages %d 0 R >>" % tree
    objects[tree - 1] = b"<< /Type /Pages /Kids [%s] /Count %d >>" % (
        b" ".join(b"%d 0 R" % k for k in kids), len(kids))
    info = add(b"<< /Title (%s) /Author (%s) /Producer (JellyMesh test) >>" % (title.encode("latin-1"), author.encode("latin-1")))
    out = b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n"
    offsets = []
    for n, body in enumerate(objects, 1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % n + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    out += b"".join(b"%010d 00000 n \n" % o for o in offsets)
    out += b"trailer\n<< /Size %d /Root %d 0 R /Info %d 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (
        len(objects) + 1, catalog, info, xref)
    open(dst, "wb").write(out)


def cbz(dst, title, series, number, writer, year, *pages):
    """A CBZ with ComicInfo.xml and the pages in order."""
    info = f"""<?xml version="1.0" encoding="utf-8"?>
<ComicInfo xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Title>{esc(title)}</Title>
  <Series>{esc(series)}</Series>
  <Number>{number}</Number>
  <Writer>{esc(writer)}</Writer>
  <Year>{year}</Year>
  <Summary>JellyMesh test comic.</Summary>
  <PageCount>{len(pages)}</PageCount>
  <LanguageISO>it</LanguageISO>
</ComicInfo>
"""
    with zipfile.ZipFile(dst, "w") as archive:
        zip_write(archive, "ComicInfo.xml", info)
        for n, path in enumerate(pages, 1):
            zip_write(archive, f"{n:03d}.jpg", open(path, "rb").read(), stored=True)


if __name__ == "__main__":
    commands = {"exif": exif, "epub": epub, "pdf": pdf, "cbz": cbz}
    if len(sys.argv) < 2 or sys.argv[1] not in commands:
        sys.exit("usage: media-types.py exif|epub|pdf|cbz ...")
    commands[sys.argv[1]](*sys.argv[2:])
