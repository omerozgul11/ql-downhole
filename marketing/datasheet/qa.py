#!/usr/bin/env python3
"""Layout QA gate for QuakeLogic datasheets. Run before every delivery.

Why DOM geometry instead of eyeballing the PDF: the failures this format
actually suffers from (text under the footer logo, a paragraph escaping its
card, two absolutely-positioned columns printed on top of each other) are all
*measurable*. Reading bounding boxes out of the live page catches them exactly,
whereas a glance at a thumbnail does not — an overlap of 6px is invisible at
thumbnail scale and glaring at 100%.

Checks:
  1. FOOTER      no content block intrudes into the footer band (y > 940)
  2. OVERFLOW    no element's text spills outside its card/cell (scroll > client)
  3. COLLISION   no two sibling text blocks overlap each other
  4. SEAM        on cover/back, no text crosses into the grey panel (x > 516)
  5. LOGO        a logo is present on every page, correctly sized
  6. BLEED       nothing pokes outside the 816x1056 page box

Usage:
    python qa.py build/datasheet.html
Exit code 0 = clean, 1 = problems found (printed).
"""
import os
import sys
from playwright.sync_api import sync_playwright

PAGE_W, PAGE_H = 816, 1056
FOOTER_TOP = 940      # content floor; footer band owns below this
ORANGE_ZONE = 516     # cover/back: text must stay left of the grey panel
LOGO_MIN, LOGO_MAX = 14, 40   # acceptable rendered logo height (px)

JS = r"""
() => {
  const PAGE_W = 816, FOOTER_TOP = 940, ORANGE_ZONE = 516;
  const out = [];
  const pages = [...document.querySelectorAll('.page')];

  pages.forEach((page, i) => {
    const pnum = i + 1;
    const pbox = page.getBoundingClientRect();
    const rel = (el) => {
      const b = el.getBoundingClientRect();
      return { x: b.left - pbox.left, y: b.top - pbox.top,
               r: b.right - pbox.left, b: b.bottom - pbox.top,
               w: b.width, h: b.height };
    };
    const isBack  = page.classList.contains('back');
    const isCover = page.classList.contains('cover');

    // ---- 1. FOOTER intrusion (content pages only) ----
    // Measure the real footer, don't assume a y-value: the footer band is
    // wherever the footer element actually sits. Require 8px of daylight.
    const footEl = page.querySelector('.footer');
    if (!isBack && !isCover && footEl) {
      const floor = rel(footEl).y - 8;
      page.querySelectorAll('.pad table, .pad .optbox, .pad .comp .cap, .pad .gal .cell, .pad p, .pad li, .pad .seclabel')
        .forEach(el => {
          const b = rel(el);
          if (b.h > 0 && b.b > floor) {
            out.push({ page: pnum, check: 'FOOTER',
              msg: `<${el.className || el.tagName}> bottom=${b.b.toFixed(0)}px collides with footer at ${floor.toFixed(0)}px`,
              text: (el.innerText || '').slice(0, 45) });
          }
        });
    }

    // ---- 2. TEXT OVERFLOW out of its container ----
    page.querySelectorAll('.optbox, .sidebar, .comp .cap, .spec2 td, .tbl td, .back .colA, .back .colB').forEach(el => {
      // vertical spill
      if (el.scrollHeight - el.clientHeight > 1) {
        out.push({ page: pnum, check: 'OVERFLOW',
          msg: `<${el.className}> content ${el.scrollHeight}px > box ${el.clientHeight}px (text escapes card)`,
          text: (el.innerText || '').slice(0, 45) });
      }
      // horizontal spill
      if (el.scrollWidth - el.clientWidth > 1) {
        out.push({ page: pnum, check: 'OVERFLOW',
          msg: `<${el.className}> content ${el.scrollWidth}px wide > box ${el.clientWidth}px`,
          text: (el.innerText || '').slice(0, 45) });
      }
    });

    // ---- 3. COLLISION between text blocks ----
    const blocks = [...page.querySelectorAll(
      '.back .colA, .back .colB, .back .vname, .back .qr, .cover .ktitle, .cover .vname, .optbox, .kf, .pf, .sidebar, .ov-left'
    )].map(el => ({ el, b: rel(el) })).filter(o => o.b.w > 0 && o.b.h > 0);

    for (let a = 0; a < blocks.length; a++) {
      for (let c = a + 1; c < blocks.length; c++) {
        const A = blocks[a], C = blocks[c];
        if (A.el.contains(C.el) || C.el.contains(A.el)) continue;
        const ox = Math.min(A.b.r, C.b.r) - Math.max(A.b.x, C.b.x);
        const oy = Math.min(A.b.b, C.b.b) - Math.max(A.b.y, C.b.y);
        if (ox > 2 && oy > 2) {
          out.push({ page: pnum, check: 'COLLISION',
            msg: `.${A.el.className.split(' ')[0]} overlaps .${C.el.className.split(' ')[0]} by ${ox.toFixed(0)}x${oy.toFixed(0)}px`,
            text: '' });
        }
      }
    }

    // ---- 4. SEAM: text crossing into grey panel on cover/back ----
    if (isBack || isCover) {
      page.querySelectorAll('.colA, .colB, .ktitle, .qr').forEach(el => {
        const b = rel(el);
        if (b.r > ORANGE_ZONE) {
          out.push({ page: pnum, check: 'SEAM',
            msg: `.${el.className.split(' ')[0]} right=${b.r.toFixed(0)}px crosses orange/grey seam at ${ORANGE_ZONE}px`,
            text: (el.innerText || '').slice(0, 45) });
        }
      });
    }

    // ---- 5. LOGO present, right-hand corner, sane size ----
    // Match on the class alone. An unsized logo renders at its native 88px and
    // shoots off the page — that is exactly what this catches.
    const logo = page.querySelector('.flogo, .cornerlogo img');
    if (!logo) {
      out.push({ page: pnum, check: 'LOGO', msg: 'no QuakeLogic logo on this page', text: '' });
    } else {
      const b = rel(logo);
      if (b.h < 14 || b.h > 40) {
        out.push({ page: pnum, check: 'LOGO',
          msg: `logo height ${b.h.toFixed(0)}px outside 14-40px range (unstyled/oversized?)`, text: '' });
      }
      if (b.r > PAGE_W - 8) {
        out.push({ page: pnum, check: 'LOGO',
          msg: `logo right edge ${b.r.toFixed(0)}px runs off the ${PAGE_W}px page`, text: '' });
      }
      if (b.x < PAGE_W / 2) {
        out.push({ page: pnum, check: 'LOGO',
          msg: `logo starts at x=${b.x.toFixed(0)}px — belongs in the RIGHT corner`, text: '' });
      }
    }

    // ---- 6. BLEED outside the page box ----
    page.querySelectorAll('.pad *, .colA, .colB, .vname, .ktitle').forEach(el => {
      const b = rel(el);
      if (b.h > 0 && (b.x < -2 || b.y < -2)) {
        out.push({ page: pnum, check: 'BLEED',
          msg: `.${(el.className||el.tagName).toString().split(' ')[0]} starts off-page at x=${b.x.toFixed(0)},y=${b.y.toFixed(0)} (clipped)`,
          text: (el.innerText || '').slice(0, 45) });
      }
    });
  });

  return { pages: pages.length, issues: out };
}
"""


def main(html_path: str) -> int:
    url = "file://" + os.path.abspath(html_path)
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": PAGE_W, "height": PAGE_H})
        page.goto(url, wait_until="networkidle")
        res = page.evaluate(JS)
        browser.close()

    issues = res["issues"]
    print(f"QA: {res['pages']} pages inspected.")
    if not issues:
        print("PASS  no overlaps, no overflow, no footer collisions, logos OK.")
        return 0

    print(f"FAIL  {len(issues)} issue(s):\n")
    for it in issues:
        line = f"  [p{it['page']}] {it['check']:9s} {it['msg']}"
        if it.get("text"):
            line += f"\n              text: {it['text']!r}"
        print(line)
    print("\nFixes: shorten the copy, split the table across two .spec2 columns,")
    print("move a block to its own page, or widen the card — do NOT shrink the")
    print("font below the sizes in datasheet.css; that is what makes it look cheap.")
    return 1


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    sys.exit(main(sys.argv[1]))
