"""Line drawings for the Downhole Studio field guide (inline SVG, QuakeLogic colours)."""

ORANGE, INK, GREY, LINE, SOIL, BLUE = '#ED6A23', '#231F20', '#58595B', '#bdbdbd', '#efe6dc', '#2f6db5'
FONT = "font-family='Barlow, sans-serif'"
LAYERS = ['#f3ebe2', '#eadccd', '#e0cfbb', '#d6c2aa']


def _t(x, y, text, size=11, weight=600, colour=INK, anchor='middle'):
    return f"<text x='{x}' y='{y}' text-anchor='{anchor}' {FONT} font-size='{size}' font-weight='{weight}' fill='{colour}'>{text}</text>"


def _dim(x1, x2, y, label, colour=INK):
    return (f"<path d='M{x1} {y} L{x2} {y} M{x1} {y-5} L{x1} {y+5} M{x2} {y-5} L{x2} {y+5}' stroke='{colour}' stroke-width='1.2'/>"
            f"<path d='M{x1} {y} l7 -3.5 l0 7 Z M{x2} {y} l-7 -3.5 l0 7 Z' fill='{colour}'/>"
            + _t((x1 + x2) / 2, y - 7, label, 11, 600, colour))


def _hammer(x, y, angle=-35):
    return (f"<g transform='translate({x},{y}) rotate({angle})'>"
            f"<rect x='-3' y='-4' width='6' height='44' rx='2' fill='#9a6b3f'/>"
            f"<rect x='-11' y='-14' width='22' height='12' rx='2' fill='{INK}'/></g>")


def borehole(width=688, height=396):
    """Cross-section: grouted cased borehole, P plate and S beam at their offsets, clamped 3C receiver, ray paths."""
    g, cx, bottom = 108, 344, height - 18
    xp, xs = cx - 170, cx + 170
    rec_y = 262
    parts = [f"<svg viewBox='0 0 {width} {height}' width='{width}' xmlns='http://www.w3.org/2000/svg'>"]
    # Soil layers.
    tops = [g, g + 70, g + 150, g + 225, height]
    for k in range(4):
        parts.append(f"<rect x='0' y='{tops[k]}' width='{width}' height='{tops[k + 1] - tops[k]}' fill='{LAYERS[k]}'/>")
        if k:
            parts.append(f"<path d='M0 {tops[k]} L{width} {tops[k]}' stroke='#c9b49a' stroke-width='1' stroke-dasharray='5 4'/>")
    parts.append(f"<path d='M0 {g} L{width} {g}' stroke='#a88d72' stroke-width='2'/>")
    # Grout and casing.
    parts.append(f"<rect x='{cx - 15}' y='{g}' width='30' height='{bottom - g}' fill='#cfcfd2'/>")
    parts.append(f"<rect x='{cx - 8}' y='{g - 14}' width='16' height='{bottom - g + 14}' fill='#ffffff' stroke='{GREY}' stroke-width='2'/>")
    parts.append(_t(cx + 22, bottom - 6, 'grout', 10, 600, GREY, 'start'))
    parts.append(_t(cx - 22, bottom - 6, 'PVC casing', 10, 600, GREY, 'end'))
    # Depth ticks.
    for k, y in enumerate(range(g + 30, bottom - 10, 34)):
        parts.append(f"<path d='M{cx + 8} {y} l6 0' stroke='{GREY}' stroke-width='1'/>")
    # Cable and receiver with clamp.
    parts.append(f"<path d='M{cx} {g - 14} L{cx} {rec_y - 14}' stroke='{INK}' stroke-width='1.6'/>")
    parts.append(f"<rect x='{cx - 6}' y='{rec_y - 14}' width='12' height='30' rx='3' fill='{ORANGE}'/>")
    parts.append(f"<path d='M{cx + 6} {rec_y - 6} L{cx + 8} {rec_y - 2} M{cx - 6} {rec_y + 8} L{cx - 8} {rec_y + 4}' stroke='{INK}' stroke-width='2'/>")
    parts.append(_t(cx + 40, rec_y - 4, '3-component geophone,', 11, 700, INK, 'start'))
    parts.append(_t(cx + 40, rec_y + 10, 'clamped at the test depth', 11, 600, INK, 'start'))
    parts.append(f"<path d='M{cx + 10} {rec_y} L{cx + 36} {rec_y - 6}' stroke='{INK}' stroke-width='1'/>")
    # Ray paths.
    parts.append(f"<path d='M{xp} {g} L{cx} {rec_y}' stroke='{BLUE}' stroke-width='2' stroke-dasharray='7 5'/>")
    parts.append(f"<path d='M{xs} {g} L{cx} {rec_y}' stroke='{ORANGE}' stroke-width='2' stroke-dasharray='7 5'/>")
    parts.append(_t(xp + 22, g + 98, 'P wave', 11, 700, BLUE, 'start'))
    parts.append(_t(xs - 22, g + 98, 'S wave', 11, 700, ORANGE, 'end'))
    # P source: plate, hammer, reference geophone.
    parts.append(f"<rect x='{xp - 18}' y='{g - 5}' width='36' height='6' rx='1.5' fill='{GREY}'/>")
    parts.append(_hammer(xp + 8, g - 34))
    parts.append(f"<rect x='{xp - 48}' y='{g - 14}' width='10' height='13' rx='2' fill='{INK}'/><path d='M{xp - 43} {g - 1} l0 9' stroke='{GREY}' stroke-width='2'/>")
    parts.append(_t(xp - 54, g - 16, 'reference', 10, 600, GREY, 'end'))
    parts.append(_t(xp - 54, g - 4, 'geophone', 10, 600, GREY, 'end'))
    parts.append(_t(xp, 20, 'P SOURCE', 12, 700, BLUE))
    parts.append(_t(xp, 34, 'plate + vertical hammer blow', 10, 500, GREY))
    # S source: beam end-on under a wheel.
    parts.append(f"<rect x='{xs - 16}' y='{g - 16}' width='32' height='16' fill='#b98a56' stroke='#7a5a35' stroke-width='1'/>")
    parts.append(f"<circle cx='{xs}' cy='{g - 38}' r='22' fill='{INK}'/><circle cx='{xs}' cy='{g - 38}' r='9' fill='#888'/>")
    parts.append(f"<path d='M{xs + 26} {g - 8} l22 0 M{xs - 26} {g - 8} l-22 0' stroke='{ORANGE}' stroke-width='2'/>"
                 f"<path d='M{xs + 48} {g - 8} l-7 -4 l0 8 Z M{xs - 48} {g - 8} l7 -4 l0 8 Z' fill='{ORANGE}'/>")
    parts.append(_t(xs, 20, 'S SOURCE', 12, 700, ORANGE))
    parts.append(_t(xs, 34, 'loaded beam, struck at each end', 10, 500, GREY))
    # Offsets and seismograph.
    parts.append(_dim(xp, cx, g + 22, 'P offset 1–3 m', BLUE))
    parts.append(_dim(cx, xs, g + 22, 'S offset 1–3 m', ORANGE))
    parts.append(f"<rect x='{cx - 34}' y='8' width='68' height='34' rx='5' fill='{INK}'/>")
    parts.append(_t(cx, 24, 'SEISMOGRAPH', 9.5, 700, '#ffffff'))
    parts.append(_t(cx, 36, 'ch 1–4', 9, 500, '#dddddd'))
    parts.append(f"<path d='M{cx} 42 L{cx} {g - 14}' stroke='{INK}' stroke-width='1.2'/>")
    parts.append(_t(18, g + 56, 'depth measured to', 10, 600, GREY, 'start'))
    parts.append(_t(18, g + 69, 'the sensor centre', 10, 600, GREY, 'start'))
    parts.append('</svg>')
    return ''.join(parts)


def beam(width=688, height=310):
    """Plan view: beam at right angles to the radial line, held by the vehicle's wheels, struck at each end."""
    cy, bx = 155, 470
    parts = [f"<svg viewBox='0 0 {width} {height}' width='{width}' xmlns='http://www.w3.org/2000/svg'>",
             f"<rect x='0' y='0' width='{width}' height='{height}' fill='{SOIL}' rx='6'/>"]
    parts.append(f"<circle cx='120' cy='{cy}' r='13' fill='#ffffff' stroke='{GREY}' stroke-width='3'/>")
    parts.append(_t(120, cy + 34, 'borehole', 11, 700))
    parts.append(f"<path d='M136 {cy} L{bx - 14} {cy}' stroke='{GREY}' stroke-width='1.4' stroke-dasharray='6 4'/>")
    parts.append(_t((136 + bx) / 2 - 20, cy - 8, 'radial line (S offset)', 10.5, 600, GREY))
    # Vehicle outline with wheels on the beam.
    parts.append(f"<rect x='{bx - 40}' y='{cy - 92}' width='150' height='184' rx='14' fill='none' stroke='{GREY}' stroke-width='1.5' stroke-dasharray='4 4'/>")
    parts.append(_t(bx + 24, cy - 72, 'vehicle: parked,', 10, 600, GREY, 'start'))
    parts.append(_t(bx + 24, cy - 59, 'braked, chocked', 10, 600, GREY, 'start'))
    parts.append(f"<rect x='{bx - 10}' y='{cy - 96}' width='20' height='192' rx='3' fill='#b98a56' stroke='#7a5a35'/>")
    parts.append(f"<rect x='{bx - 7}' y='{cy - 104}' width='14' height='9' fill='{GREY}'/><rect x='{bx - 7}' y='{cy + 95}' width='14' height='9' fill='{GREY}'/>")
    for wy in (cy - 58, cy + 38):
        parts.append(f"<rect x='{bx - 16}' y='{wy}' width='32' height='20' rx='4' fill='{INK}'/>")
    parts.append(_t(bx + 22, cy + 4, 'wheels on the beam', 10, 600, INK, 'start'))
    # Hammer blows.
    parts.append(f"<path d='M{bx} {cy - 138} L{bx} {cy - 110}' stroke='{ORANGE}' stroke-width='3'/><path d='M{bx} {cy - 108} l-6 -10 l12 0 Z' fill='{ORANGE}'/>")
    parts.append(f"<path d='M{bx} {cy + 138} L{bx} {cy + 110}' stroke='{BLUE}' stroke-width='3'/><path d='M{bx} {cy + 108} l-6 10 l12 0 Z' fill='{BLUE}'/>")
    parts.append(_t(bx - 22, cy - 118, 'S+ : strike this end', 11.5, 700, ORANGE, 'end'))
    parts.append(_t(bx - 22, cy + 126, 'S− : strike the other end', 11.5, 700, BLUE, 'end'))
    parts.append(_t(width - 16, cy - 4, 'shear motion', 10.5, 700, INK, 'end'))
    parts.append(_t(width - 16, cy + 10, 'along the beam', 10.5, 600, GREY, 'end'))
    parts.append(f"<path d='M{width - 50} {cy + 24} l0 40 M{width - 50} {cy + 24} l-5 9 M{width - 50} {cy + 24} l5 9 M{width - 50} {cy + 64} l-5 -9 M{width - 50} {cy + 64} l5 -9' stroke='{INK}' stroke-width='1.6' fill='none'/>")
    parts.append('</svg>')
    return ''.join(parts)


def sequence(width=688, height=150):
    """The routine at every depth."""
    steps = [('1', 'Set depth', 'sensor centre at mark'), ('2', 'Clamp', 'wait to settle'), ('3', 'P shot', 'plate, vertical'),
             ('4', 'S+ shot', 'left end of beam'), ('5', 'S− shot', 'right end of beam'), ('6', 'Check & log', 'repeat if poor')]
    w, gap, top = 100, 17.6, 22
    parts = [f"<svg viewBox='0 0 {width} {height}' width='{width}' xmlns='http://www.w3.org/2000/svg'>"]
    for k, (n, title, sub) in enumerate(steps):
        x = k * (w + gap)
        fill = ORANGE if k in (2, 3, 4) else INK
        parts.append(f"<rect x='{x}' y='{top}' width='{w}' height='66' rx='8' fill='{fill}'/>")
        parts.append(_t(x + w / 2, top + 24, f'{n} · {title}', 12, 700, '#ffffff'))
        parts.append(_t(x + w / 2, top + 44, sub, 9.6, 500, '#ffffff'))
        if k < len(steps) - 1:
            ax = x + w + 2
            parts.append(f"<path d='M{ax} {top + 33} l{gap - 6} 0' stroke='{GREY}' stroke-width='2'/><path d='M{ax + gap - 4} {top + 33} l-7 -4 l0 8 Z' fill='{GREY}'/>")
    last = (len(steps) - 1) * (w + gap) + w / 2
    parts.append(f"<path d='M{last} {top + 66} L{last} {top + 98} L{w / 2} {top + 98} L{w / 2} {top + 72}' stroke='{ORANGE}' stroke-width='2' fill='none'/>"
                 f"<path d='M{w / 2} {top + 68} l-5 9 l10 0 Z' fill='{ORANGE}'/>")
    parts.append(_t(width / 2, top + 116, 'Release the clamp and move to the next depth (constant interval, e.g. 1 m)', 11, 600, GREY))
    parts.append('</svg>')
    return ''.join(parts)


DIAGRAMS = {
    'borehole': (borehole, 'Cross-section: P and S sources at measured offsets, grouted PVC casing, clamped 3-component geophone'),
    'beam': (beam, 'Plan view of the S-wave source: beam at right angles to the radial line, loaded by the vehicle, struck at both ends'),
    'sequence': (sequence, 'The routine at every depth'),
}
