"""Render the checked-in Windows icon frames from the application's SVG."""

from base64 import b64decode, b64encode
from html import unescape
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import tempfile


root = Path(__file__).resolve().parent
ui = root / "ui"
palettes = json.loads((ui / "icon-palettes.json").read_text(encoding="utf-8"))
artwork = (ui / "morphocyte-mark.svg").read_text(encoding="utf-8")
sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def image(colors):
    variables = ";".join(f"--logo-{name}:{color}" for name, color in colors.items())
    svg = artwork.replace("<svg ", f'<svg style="{variables}" ', 1)
    return '<img src="data:image/svg+xml;base64,' + b64encode(svg.encode()).decode("ascii") + '">'


def encode_icon(frames):
    header = struct.pack("<HHH", 0, 1, len(sizes))
    entries = bytearray()
    offset = len(header) + 16 * len(sizes)
    for size, png in zip(sizes, frames, strict=True):
        assert struct.unpack_from(">II", png, 16) == (size, size)
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
    return header + entries + b"".join(frames)


edge = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft/Edge/Application/msedge.exe"
scratch = root / ".checks"
scratch.mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(prefix="render-icons-", dir=scratch) as temporary:
    work = Path(temporary)
    page = work / "icons.html"
    page.write_text("<!doctype html><meta charset=utf-8>" + "".join(map(image, palettes.values())) + """
<pre id="output"></pre>
<script>
addEventListener("load", () => {
  const all = [...document.images].map(image => %s.map(size => {
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = size;
    const context = canvas.getContext("2d");
    context.drawImage(image, 0, 0, size, size);
    const pixels = context.getImageData(0, 0, size, size).data;
    const corners = [0, size-1, size*(size-1), size*size-1];
    if (corners.some(i => pixels[i*4+3] !== 0) || pixels[(size*size/2+size/2)*4+3] === 0)
      throw Error("Invalid icon silhouette");
    return canvas.toDataURL().split(",")[1];
  }));
  document.getElementById("output").textContent = JSON.stringify(all);
});
</script>
""" % json.dumps(sizes), encoding="utf-8")
    result = subprocess.run([
        edge, "--headless=new", "--disable-background-networking", "--disable-extensions", "--no-first-run",
        f"--user-data-dir={work / 'browser'}", "--virtual-time-budget=4000", "--dump-dom", page.as_uri(),
    ], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45, check=True)

output = re.search(r'<pre id="output">(.+?)</pre>', result.stdout)
if not output:
    raise RuntimeError("Edge did not return transparent icon frames.")
frames = [[b64decode(item, validate=True) for item in row] for row in json.loads(unescape(output[1]))]
assert len(frames) == len(palettes)
icons = ui / "icons"
icons.mkdir(exist_ok=True)
for resource_id, row in enumerate(frames, start=1):
    (icons / f"{resource_id:02d}.ico").write_bytes(encode_icon(row))
(icons / "default.png").write_bytes(frames[0][-1])
print(f"Rendered {len(frames)} palettes at {len(sizes)} sizes with transparent backgrounds.")
