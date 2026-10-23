"""Shared QuakeLogic house-style helpers for the Downhole Studio print documents.

Used by marketing/field-guide/build.py, marketing/brochure/build.py and marketing/datasheet/build.py.
The page geometry, colours and fonts come verbatim from the QuakeLogic datasheet format
(datasheet.css + brochure-extra.css, Oswald + Barlow). Pages are 816 x 1056 CSS px (US Letter).

Rendering needs a Python with Playwright and a Chromium build. By default the browsers and their
system libraries prepared for MASW Studio on the QuakeLogic build host are used as tools
(set DHS_PLAYWRIGHT_BROWSERS / DHS_BROWSER_LIBS to use others):
    /opt/apps/masw-studio/engine/.venv/bin/python marketing/<document>/build.py
"""
from pathlib import Path
import html
import os
import re
import shutil

COMMON = Path(__file__).resolve().parent
ROOT = COMMON.parents[1]
MASW = Path('/opt/apps/masw-studio')


def version() -> str:
    props = (ROOT / 'Directory.Build.props').read_text('utf-8')
    return re.search(r'<VersionPrefix>([^<]+)</VersionPrefix>', props).group(1)


def stage(build: Path) -> None:
    """Fresh build folder with the house CSS, fonts, logo and product QR."""
    if build.exists():
        shutil.rmtree(build)
    (build / 'assets').mkdir(parents=True)
    shutil.copytree(COMMON / 'fonts', build / 'fonts')
    for name in ('datasheet.css', 'brochure-extra.css', 'quakelogic_logo.png'):
        shutil.copy(COMMON / name, build / name)
    shutil.copy(COMMON / 'product_qr.png', build / 'assets/product_qr.png')


# ---------------------------------------------------------------- markdown
def inline(text: str) -> str:
    text = html.escape(text, quote=False)
    codes = []
    text = re.sub(r'`([^`]+)`', lambda m: codes.append(f'<code>{m.group(1)}</code>') or f'\0{len(codes) - 1}\0', text)
    text = re.sub(r'\[([^\]]+)\]\(([^)\s]+)\)', lambda m: m.group(1), text)  # print: keep the link text only
    text = re.sub(r'\*\*([^*]+)\*\*', r'<strong>\1</strong>', text)
    text = re.sub(r'(?<![*\w])\*([^*\n]+)\*(?![*\w])', r'<em>\1</em>', text)
    text = text.replace('&amp;nbsp;', '&nbsp;')
    return re.sub(r'\0(\d+)\0', lambda m: codes[int(m.group(1))], text)


def markdown(source: str) -> str:
    """Small Markdown subset used by the guides: headings, paragraphs, lists, tables, '> ' tips.

    A line containing only <!-- pagebreak --> starts a new page.
    """
    lines, out, i = source.replace('\r\n', '\n').split('\n'), [], 0
    block = re.compile(r'^(#{1,6}\s|>\s|\s*([-*+]|\d+[.)])\s|\||---\s*$)')
    while i < len(lines):
        line = lines[i]
        if line.strip() == '<!-- pagebreak -->':
            out.append('<div class="pagebreak"></div>')
            i += 1
            continue
        if not line.strip() or re.match(r'^---\s*$', line):
            i += 1
            continue
        if line.startswith('> '):
            tip = []
            while i < len(lines) and lines[i].startswith('>'):
                tip.append(lines[i][1:].strip())
                i += 1
            out.append(f'<div class="tip">{inline(" ".join(tip))}</div>')
            continue
        if m := re.match(r'^(#{1,6})\s+(.*)$', line):
            level = len(m.group(1))
            out.append(f'<h{level}>{inline(m.group(2))}</h{level}>')
            i += 1
            continue
        if line.startswith('|') and i + 1 < len(lines) and re.match(r'^\|?[\s:|-]+\|?\s*$', lines[i + 1]):
            def cells(text):
                return [inline(c.strip()) for c in text.strip().strip('|').split('|')]
            head = cells(line)
            i += 2
            rows = []
            while i < len(lines) and lines[i].startswith('|'):
                rows.append(cells(lines[i]))
                i += 1
            out.append('<table class="tbl"><thead><tr>' + ''.join(f'<th>{c}</th>' for c in head) + '</tr></thead><tbody>'
                       + ''.join('<tr>' + ''.join(f'<td>{c}</td>' for c in r) + '</tr>' for r in rows) + '</tbody></table>')
            continue
        if m := re.match(r'^(\s*)([-*+]|\d+[.)])\s+(.*)$', line):
            ordered = m.group(2)[0].isdigit()
            items = []  # (text, [sub items])
            while i < len(lines):
                n = re.match(r'^(\s*)([-*+]|\d+[.)])\s+(.*)$', lines[i])
                if n and not n.group(1) and n.group(2)[0].isdigit() == ordered:
                    items.append([n.group(3), []])
                    i += 1
                    continue
                if n and n.group(1) and items:  # nested list item
                    items[-1][1].append(n.group(3))
                    i += 1
                    continue
                if lines[i].strip() and re.match(r'^\s+\S', lines[i]) and items:
                    if items[-1][1]:
                        items[-1][1][-1] += ' ' + lines[i].strip()
                    else:
                        items[-1][0] += ' ' + lines[i].strip()
                    i += 1
                    continue
                break
            tag = 'ol' if ordered else 'ul'
            body = ''
            for text, subs in items:
                sub = ('<ul>' + ''.join(f'<li>{inline(s)}</li>' for s in subs) + '</ul>') if subs else ''
                body += f'<li>{inline(text)}{sub}</li>'
            out.append(f'<{tag}>{body}</{tag}>')
            continue
        para = [line.strip()]
        i += 1
        while i < len(lines) and lines[i].strip() and not block.match(lines[i]):
            para.append(lines[i].strip())
            i += 1
        out.append(f'<p>{inline(" ".join(para))}</p>')
    return '\n'.join(out)


# ---------------------------------------------------------------- page parts
def footer(run: str = '') -> str:
    return (f'<div class="footer"><div class="left"><span class="pg"></span>'
            + (f'<span class="run">{run}</span>' if run else '')
            + '</div><img class="flogo" src="quakelogic_logo.png" alt="QuakeLogic"></div>')


def frame(src: str, title: str = 'Downhole Studio') -> str:
    return f'<div class="frame"><div class="bar"><i></i><i></i><i></i><span>{title}</span></div><img src="assets/{src}"></div>'


def back_page(vname: str, about_html: str, machine_html: str = '') -> str:
    return f'''<section class="page back">
  <div class="greypanel"></div>
  <div class="cornerlogo"><img src="quakelogic_logo.png" alt="QuakeLogic"></div>
  <div class="vname">{vname}</div>
  <div class="colA">
    <div class="lbl">Corporate Headquarters:</div>
    <div class="blk"><b>QUAKELOGIC INC.</b><br>2008 Opportunity Dr., Suite 130<br>Roseville, CA 95678, USA<br><br>Monday &ndash; Friday<br>9:00 AM &ndash; 5:00 PM (PST/PDT)</div>
    <div class="lbl">Contact Us:</div>
    <div class="blk"><b>Phone:</b> +1 (916) 899-0391<br><b>Email:</b> sales@quakelogic.net<br><b>Web:</b> products.quakelogic.net</div>
    <div class="lbl">Software Support:</div>
    <div class="blk support">support@quakelogic.net<br>Email response time: 3 business days</div>
  </div>
  <div class="colB">
    {about_html}
    <div class="qr"><div class="t">Scan to View Products:</div><img src="assets/product_qr.png"></div>
  </div>
  {machine_html}
</section>'''


ABOUT = ('<h4>About QuakeLogic:</h4><p>QuakeLogic Inc. is a California-based engineering and technology company '
         'providing innovative, reliable, high-quality solutions to customers worldwide.</p>'
         '<h4>Our Commitment</h4><p>We are committed to quality, innovation, and customer satisfaction &mdash; '
         'building lasting partnerships through reliable solutions and dependable support.</p>')

# Flows the hidden #guide-src blocks onto copies of the .guide-template page, never past the content
# floor; headings are carried to the next page with the block that follows them; page numbers last.
PAGINATE = r'''
(async () => {
  await document.fonts.ready;
  await Promise.all([...document.images].map(i => i.complete ? 0 : new Promise(r => { i.onload = i.onerror = r; })));
  const FLOOR = 932;
  const template = document.querySelector('.guide-template');
  const src = document.getElementById('guide-src');
  if (template && src) {
    const after = template.nextElementSibling;
    let page = template, doc = page.querySelector('.gdoc');
    const over = () => { const top = page.getBoundingClientRect().top; const last = doc.lastElementChild;
      return last && last.getBoundingClientRect().bottom - top > FLOOR; };
    const fresh = () => { const p = template.cloneNode(true); p.removeAttribute('data-guide');
      p.querySelector('.gdoc').innerHTML = ''; after.before(p); page = p; doc = p.querySelector('.gdoc'); };
    // Headings and lead-in lines ending with ':' travel to the next page with the block they introduce.
    const leads = el => /^H[23]$/.test(el.tagName) || (el.tagName === 'P' && el.textContent.trim().endsWith(':'));
    const carry = () => { const moved = []; while (doc.lastElementChild && doc.children.length > 1 && leads(doc.lastElementChild)) { const h = doc.lastElementChild; h.remove(); moved.unshift(h); }
      fresh(); moved.forEach(m => doc.append(m)); };
    for (const block of [...src.children]) {
      if (block.classList.contains('pagebreak')) { if (doc.children.length) fresh(); continue; }
      if (block.tagName === 'UL' || block.tagName === 'OL') {
        let list = block.cloneNode(false), n = 0; doc.append(list);
        for (const item of [...block.children]) {
          list.append(item); n++;
          if (over()) { item.remove(); if (!list.children.length) list.remove();
            carry(); list = block.cloneNode(false); if (block.tagName === 'OL') list.start = n; doc.append(list); list.append(item); }
        }
        continue;
      }
      if (block.tagName === 'TABLE') {
        doc.append(block);
        if (over()) {
          const rows = [...block.tBodies[0].rows]; block.remove();
          const make = () => { const t = block.cloneNode(false); t.append(block.tHead.cloneNode(true));
            const b = document.createElement('tbody'); t.append(b); doc.append(t); return [t, b]; };
          let [table, body] = make(), first = true;
          for (const row of rows) {
            body.append(row);
            if (!over()) continue;
            row.remove();
            // Never leave fewer than three rows of a table at the foot of a page: move it whole.
            const kept = first && body.children.length < 3 ? [...body.children] : [];
            if (!body.children.length || kept.length) table.remove();
            carry(); [table, body] = make(); kept.forEach(r => body.append(r)); body.append(row); first = false;
          }
        }
        continue;
      }
      doc.append(block);
      if (over()) { block.remove(); carry(); doc.append(block); }
    }
    src.remove();
  }
  [...document.querySelectorAll('.page')].forEach((p, i) => { const pg = p.querySelector('.footer .pg'); if (pg) pg.textContent = i + 1; });
  window.__ready = true;
})();
'''


def browser_env() -> None:
    """Points Playwright at the Chromium build and its system libraries (see the module docstring)."""
    browsers = os.environ.get('DHS_PLAYWRIGHT_BROWSERS', str(MASW / '.tools/browsers'))
    libs = os.environ.get('DHS_BROWSER_LIBS', str(MASW / '.tools/browser-libs/root/usr/lib/x86_64-linux-gnu'))
    if Path(browsers).exists():
        os.environ.setdefault('PLAYWRIGHT_BROWSERS_PATH', browsers)
    if Path(libs).exists():
        current = os.environ.get('LD_LIBRARY_PATH', '')
        if libs not in current.split(':'):
            os.environ['LD_LIBRARY_PATH'] = libs + (':' + current if current else '')


def render(html_path: Path, pdf_path: Path) -> int:
    """Renders an HTML document to a US-Letter PDF with zero margins; returns the page count."""
    browser_env()
    from playwright.sync_api import sync_playwright
    pdf_path.parent.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={'width': 816, 'height': 1056})
        page.goto(html_path.as_uri(), wait_until='networkidle')
        page.wait_for_function('window.__ready === true', timeout=120000)
        pages = page.locator('.page').count()
        page.pdf(path=str(pdf_path), width='816px', height='1056px', print_background=True,
                 margin={'top': '0', 'bottom': '0', 'left': '0', 'right': '0'})
        browser.close()
    return pages


def layout_issues(html_path: Path) -> list:
    """Runs the QuakeLogic datasheet layout QA (marketing/datasheet/qa.py) on a finished document."""
    import importlib.util
    spec = importlib.util.spec_from_file_location('ql_datasheet_qa', COMMON.parent / 'datasheet/qa.py')
    qa = importlib.util.module_from_spec(spec)
    browser_env()
    spec.loader.exec_module(qa)
    from playwright.sync_api import sync_playwright
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={'width': 816, 'height': 1056})
        page.goto(html_path.resolve().as_uri(), wait_until='networkidle')
        page.wait_for_function('window.__ready === true', timeout=120000)
        issues = page.evaluate(qa.JS)['issues']
        browser.close()
    return issues
