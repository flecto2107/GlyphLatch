"""Create original OpenType fixtures with unique names for one test run."""

import argparse
import json
from pathlib import Path
from uuid import uuid4

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables.TupleVariation import TupleVariation


def draw_test_glyph(pen):
    # A diamond makes it easy to distinguish the fixture from a fallback A.
    pen.moveTo((60, 350))
    pen.lineTo((350, 700))
    pen.lineTo((640, 350))
    pen.lineTo((350, 0))
    pen.closePath()


def build_font(path, family, postscript, kind):
    is_ttf = kind != "static-otf"
    builder = FontBuilder(1000, isTTF=is_ttf)
    builder.setupGlyphOrder([".notdef", "space", "A"])
    builder.setupCharacterMap({32: "space", 65: "A"})
    builder.setupHorizontalMetrics({".notdef": (700, 0), "space": (350, 0), "A": (700, 60)})
    builder.setupHorizontalHeader(ascent=800, descent=-200)
    builder.setupNameTable({
        "familyName": family,
        "styleName": "Regular",
        "uniqueFontIdentifier": postscript + "--1.0",
        "fullName": family + " Regular",
        "psName": postscript,
        "version": "Version 1.0",
    })
    builder.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200)
    builder.setupPost()

    glyphs = {}
    for name in builder.font.getGlyphOrder():
        width = 350 if name == "space" else 700
        pen = TTGlyphPen(None) if is_ttf else T2CharStringPen(width, None)
        if name == "A":
            draw_test_glyph(pen)
        glyphs[name] = pen.glyph() if is_ttf else pen.getCharString()

    if is_ttf:
        builder.setupGlyf(glyphs)
    else:
        builder.setupCFF(postscript, {"FullName": family + " Regular", "FamilyName": family,
                                     "Weight": "Regular"}, glyphs, {})

    if kind == "variable-ttf":
        builder.setupFvar([("wght", 400, 400, 700, "Weight")], [])
        # Four contour points, followed by four phantom points for metrics.
        delta = [(-30, 0), (0, 0), (30, 0), (0, 0)] + [(0, 0)] * 4
        builder.setupGvar({"A": [TupleVariation({"wght": (0, 1, 1)}, delta)]})

    builder.save(path)
    with TTFont(path) as font:
        assert font.getBestCmap()[65] == "A"
        assert ("fvar" in font and "gvar" in font) == (kind == "variable-ttf")
        assert ("CFF " in font) == (kind == "static-otf")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    run = uuid4().hex[:8]
    samples = []
    for kind, suffix, extension in [
        ("static-ttf", "TT", "ttf"),
        ("static-otf", "OT", "otf"),
        ("variable-ttf", "VF", "ttf"),
    ]:
        family = f"GLatch {run} {suffix}"
        postscript = f"GLatch-{run}-{suffix}-Regular"
        filename = f"{kind}-{run}.{extension}"
        build_font(args.output / filename, family, postscript, kind)
        samples.append({"kind": kind, "family": family, "file": filename})

    manifest = args.output / f"fixtures-{run}.json"
    manifest.write_text(json.dumps(samples, indent=2) + "\n", encoding="utf-8")
    print(manifest.resolve())


if __name__ == "__main__":
    main()
