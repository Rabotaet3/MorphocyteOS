"""Generate the Atlas border display from the bundled country and oblast data."""

from pathlib import Path
import json
import re

from shapely import difference, intersection, make_valid, set_precision, union_all
from shapely.geometry import LineString, Polygon, mapping, shape
from shapely.geometry.polygon import orient

ui = Path(__file__).parent / "ui"
template = (ui / "atlas-template.html").read_text(encoding="utf-8")


def read_data(section):
    match = re.search(fr'id="{section}">(.*?)</script>', template, re.S)
    return json.loads(match[1])


atlas = read_data("atlas-geography")
scale_x, scale_y = atlas["transform"]["scale"]
offset_x, offset_y = atlas["transform"]["translate"]
arcs = []
for arc in atlas["arcs"]:
    x = y = 0
    points = []
    for dx, dy in arc:
        x += dx
        y += dy
        points.append([x * scale_x + offset_x, y * scale_y + offset_y])
    arcs.append(points)


def ring(indices):
    points = []
    for index in indices:
        arc = arcs[index] if index >= 0 else arcs[~index][::-1]
        points.extend(arc[bool(points):])
    return points


def polygons(geometry):
    if geometry.is_empty:
        return
    if geometry.geom_type == "Polygon":
        yield orient(geometry, sign=-1)
    elif hasattr(geometry, "geoms"):
        for part in geometry.geoms:
            yield from polygons(part)


def country(iso):
    return next(item for item in atlas["objects"]["features"]["geometries"] if item["properties"]["id"] == iso)


ukraine_rings = country("UKR")["arcs"]
russia_rings = country("RUS")["arcs"]
russia_arcs = {i if i >= 0 else ~i for island in russia_rings for path in island for i in path}
shared = {i if i >= 0 else ~i for path in ukraine_rings for i in path} & russia_arcs
assert shared

# Use the countries' original coastline and other neighbors. The union dissolves
# boundaries within the four oblasts. Snapping to a common precision keeps the
# two sides of the resulting country boundary coincident.
grid = 0.00001
old_border = union_all([LineString(arcs[i]) for i in shared], grid_size=grid)
ua = set_precision(make_valid(Polygon(ring(ukraine_rings[0]), [ring(r) for r in ukraine_rings[1:]])), grid)
regions = [make_valid(shape(item["geometry"])) for item in read_data("atlas-oblasts")]
assert len(regions) == 4
claimed = union_all(regions, grid_size=grid).simplify(0.011, preserve_topology=True)
# A small seam tolerance compensates for the different resolution of the source
# borders, and is limited to the original shared boundary.
seam = intersection(claimed.buffer(0.12), old_border.buffer(0.12), grid_size=grid)
claimed = union_all([claimed, seam], grid_size=grid)
part = intersection(ua, claimed, grid_size=grid)
rest = difference(ua, claimed, grid_size=grid)
# Snapping two data sources leaves a tiny closed island at the old land border.
# Merge only those detached slivers that are entirely enclosed by the new area.
slivers = [island for island in polygons(rest)
           if island.area < 0.002 and part.boundary.covers(island.boundary)]
if slivers:
    part = union_all([part, *slivers], grid_size=grid)
    rest = difference(rest, part, grid_size=grid)
assert part.area > 8 and rest.area > 30
assert abs(part.area + rest.area - ua.area) < 0.001
new_border = intersection(part.boundary, rest.boundary, grid_size=grid)
remaining_border = difference(old_border, claimed, grid_size=grid)
border = union_all([new_border, remaining_border], grid_size=grid)


def packed(items):
    return {"type": "MultiPolygon", "coordinates": [mapping(item)["coordinates"] for item in items]}


payload = {"ukraine": packed(polygons(rest)), "russiaAddition": packed(polygons(part)), "border": mapping(border)}
(ui / "border-view.json").write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
