"""Draws a badminton court as a flat isometric picture and prints it as SVG.

The output is the body of the drawing in `venue/web/src/app/shared/court.ts`:

    python scripts/gen_court.py > body.svg    # then paste inside the <svg> in court.ts

Isometric, not perspective: every line of the court is parallel to its opposite, which is what
makes the picture read as a flat illustration rather than a photograph. Everything standing up —
the posts, the net, the shuttle — also drops a hard-edged shadow along one fixed direction, which
is the other half of the look.

Measurements are the real ones in metres (13.40 × 6.10, net 1.55 m at the posts), apart from the
paint: court lines are 4 cm in the rules and that is a hairline at this size, so they are drawn
wider on purpose.
"""
import math

S = 46.0                     # pixels per metre
ROT = math.radians(34)       # how far the court is turned away from the viewer
TILT = math.radians(30)      # how far down the camera looks
LIGHT = (1.85, 0.95)         # metres of shadow per metre of height, along the floor
LINE = 0.13                  # painted line width, wider than life so it reads at this size

SIDE, LEN = 3.05, 13.40
NET_Z = LEN / 2
SINGLES = SIDE - 0.46
SHORT_SERVICE, LONG_SERVICE = 1.98, 0.76
POST_H = 1.55
POST_W = 0.14                # chunky on purpose: a hairline post reads as a scratch


def project(p):
    """Axonometric: turn about the upright axis, then look down at it. No vanishing point."""
    x, y, z = p[0], p[1], p[2] - NET_Z
    u = x * math.cos(ROT) - z * math.sin(ROT)
    v = (x * math.sin(ROT) + z * math.cos(ROT)) * math.sin(TILT) - y * math.cos(TILT)
    return (u * S, v * S)


def polyline(points):
    return "M" + "L".join(f"{x:.1f} {y:.1f}" for x, y in points)


def polygon(points):
    return polyline(points) + "Z"


def floor_rect(x0, z0, x1, z1):
    """A rectangle painted on the floor, as a projected polygon."""
    return polygon([project(p) for p in ((x0, 0.0, z0), (x1, 0.0, z0), (x1, 0.0, z1), (x0, 0.0, z1))])


def shadow_of(points):
    """Where a set of points lands on the floor, given the light."""
    return [(x + LIGHT[0] * y, 0.0, z + LIGHT[1] * y) for x, y, z in points]


# --- the floor, and the block of shade one corner of the hall sits in ----------------------------
# The floor runs well past the frame: the picture is a crop of a hall, not a diamond on a page.
# The floor itself is painted by CSS (one flat colour behind the drawing), so nothing here draws it.
MARGIN_X, MARGIN_Z = 9.0, 7.0
# The corner of the hall the light does not reach, cut off the floor on a diagonal.
shade = polygon([project(p) for p in (
    (-SIDE - MARGIN_X, 0, -MARGIN_Z), (SIDE + MARGIN_X, 0, -MARGIN_Z),
    (SIDE + MARGIN_X, 0, -MARGIN_Z + 1.4), (-SIDE - MARGIN_X, 0, -MARGIN_Z + 4.6),
)])

half = LINE / 2
lines = [
    floor_rect(-SIDE, 0, SIDE, LINE),
    floor_rect(-SIDE, LEN - LINE, SIDE, LEN),
    floor_rect(-SIDE, 0, -SIDE + LINE, LEN),
    floor_rect(SIDE - LINE, 0, SIDE, LEN),
    floor_rect(-SINGLES - half, 0, -SINGLES + half, LEN),
    floor_rect(SINGLES - half, 0, SINGLES + half, LEN),
    floor_rect(-SIDE, NET_Z - SHORT_SERVICE - half, SIDE, NET_Z - SHORT_SERVICE + half),
    floor_rect(-SIDE, NET_Z + SHORT_SERVICE - half, SIDE, NET_Z + SHORT_SERVICE + half),
    floor_rect(-SIDE, LONG_SERVICE - half, SIDE, LONG_SERVICE + half),
    floor_rect(-SIDE, LEN - LONG_SERVICE - half, SIDE, LEN - LONG_SERVICE + half),
    floor_rect(-half, 0, half, NET_Z - SHORT_SERVICE),
    floor_rect(-half, NET_Z + SHORT_SERVICE, half, LEN),
]


# --- the net -------------------------------------------------------------------------------------
def net_height(x):
    """1.524 m in the middle, 1.55 m at the posts — the sag a strung net actually has."""
    return 1.524 + (POST_H - 1.524) * (abs(x) / SIDE) ** 2


SPANS = 16
xs = [-SIDE + i * (2 * SIDE) / SPANS for i in range(SPANS + 1)]
net_top = [(x, net_height(x), NET_Z) for x in xs]
net_foot = [(x, 0.0, NET_Z) for x in xs]

net_face = polygon([project(p) for p in net_top] + [project(p) for p in reversed(net_foot)])
net_shadow = polygon(
    [project(p) for p in shadow_of(net_top)] + [project(p) for p in reversed(net_foot)]
)

mesh, mesh_shadow = [], []
for i in range(1, SPANS * 2):                                  # the strings running down
    x = -SIDE + i * (2 * SIDE) / (SPANS * 2)
    up = [(x, net_height(x), NET_Z), (x, 0.0, NET_Z)]
    mesh.append(polyline([project(p) for p in up]))
    mesh_shadow.append(polyline([project(p) for p in (shadow_of(up[:1])[0], up[1])]))
for i in range(1, 7):                                          # and the strings running across
    t = i / 7
    across = [(x, net_height(x) * t, NET_Z) for x in xs]
    mesh.append(polyline([project(p) for p in across]))
    mesh_shadow.append(polyline([project(p) for p in shadow_of(across)]))

TAPE = 0.115
tape = polygon(
    [project(p) for p in net_top]
    + [project((x, net_height(x) - TAPE, NET_Z)) for x in reversed(xs)]
)
tape_ends = [
    (project((sx, net_height(sx) - TAPE / 2, NET_Z)), TAPE * S * 0.5)
    for sx in (-SIDE, SIDE)
]

posts, post_lit, post_caps, post_shadows, post_tops = [], [], [], [], []
for sx in (-SIDE, SIDE):
    w = POST_W
    posts.append(polygon([
        project((sx - w, 0.0, NET_Z)), project((sx + w, 0.0, NET_Z)),
        project((sx + w, POST_H, NET_Z)), project((sx - w, POST_H, NET_Z)),
    ]))
    # The side the light falls on, so the post reads as a round thing rather than a stripe.
    lit = w * 0.42
    post_lit.append(polygon([
        project((sx + lit, 0.0, NET_Z)), project((sx + w, 0.0, NET_Z)),
        project((sx + w, POST_H, NET_Z)), project((sx + lit, POST_H, NET_Z)),
    ]))
    cap_top = project((sx, POST_H, NET_Z))
    post_caps.append(polygon([
        project((sx - w, POST_H - 0.24, NET_Z)), project((sx + w, POST_H - 0.24, NET_Z)),
        project((sx + w, POST_H, NET_Z)), project((sx - w, POST_H, NET_Z)),
    ]))
    post_tops.append((cap_top, project((sx, 0.0, NET_Z))))
    post_shadows.append(polygon([
        project((sx - w, 0.0, NET_Z)), project((sx + w, 0.0, NET_Z)),
        project(shadow_of([(sx + w, POST_H, NET_Z)])[0]),
        project(shadow_of([(sx - w, POST_H, NET_Z)])[0]),
    ]))

# --- a shuttle dropping towards the net, and the mark it throws on the floor ----------------------
SHUTTLE = (-1.5, 2.0, NET_Z - 1.6)
head = project(SHUTTLE)
# Cork down, feathers up: a shuttle only ever falls one way round.
hx, hy = head
skirt = (
    f"M{hx - 5.4:.1f} {hy - 3.5:.1f}"
    f"L{hx - 12.6:.1f} {hy - 26.0:.1f}"
    f"Q{hx:.1f} {hy - 33.0:.1f} {hx + 12.6:.1f} {hy - 26.0:.1f}"
    f"L{hx + 5.4:.1f} {hy - 3.5:.1f}"
    f"Q{hx:.1f} {hy - 1.0:.1f} {hx - 5.4:.1f} {hy - 3.5:.1f}Z"
)
feathers = [
    polyline([(hx + 5.2 * k, hy - 5.0), (hx + 12.2 * k, hy - 26.5)])
    for k in (-0.6, -0.2, 0.2, 0.6)
]
shuttle_mark = project(shadow_of([SHUTTLE])[0])

CAP_R = POST_W * S * math.cos(ROT)

# One element per group wherever the shapes share a style: an SVG subpath costs coordinates, a
# separate element costs a DOM node, a compiled instruction and an encapsulation attribute too.
print("<!-- generated by scripts/gen_court.py - do not edit these shapes by hand -->")
print(f'<path class="paint" d="{"".join(lines)}" />')
print(f'<path class="shade" d="{shade}" />')
print(f'<path class="cast" d="{net_shadow + "".join(post_shadows)}" />')
print(f'<path class="cast-mesh" d="{"".join(mesh_shadow)}" />')
print(f'<ellipse class="shuttle-mark" cx="{shuttle_mark[0]:.1f}" cy="{shuttle_mark[1]:.1f}" rx="13" ry="7" />')
print(f'<path class="posts" d="{"".join(posts)}" />')
print(f'<path class="post-lit" d="{"".join(post_lit)}" />')
print('<g class="post-caps">')
print(f'  <path d="{"".join(post_caps)}" />')
for (cx, cy), (fx, fy) in post_tops:
    print(f'  <ellipse cx="{cx:.1f}" cy="{cy:.1f}" rx="{CAP_R:.1f}" ry="{CAP_R * 0.55:.1f}" />')
    print(f'  <ellipse class="foot" cx="{fx:.1f}" cy="{fy:.1f}" rx="{CAP_R:.1f}" ry="{CAP_R * 0.55:.1f}" />')
print('</g>')
print(f'<path class="net-face" d="{net_face}" />')
print(f'<path class="net-mesh" d="{"".join(mesh)}" />')
print(f'<path class="tape" d="{tape}" />')
print('<g class="tape-ends">')
for (cx, cy), r in tape_ends:
    print(f'  <ellipse cx="{cx:.1f}" cy="{cy:.1f}" rx="{r * 0.8:.1f}" ry="{r:.1f}" />')
print('</g>')
print('<g class="shuttle">')
print(f'  <path class="skirt" d="{skirt}" />')
print(f'  <path class="feather" d="{"".join(feathers)}" />')
print(f'  <circle class="cork" cx="{head[0]:.1f}" cy="{head[1]:.1f}" r="6" />')
print('</g>')

# The frame is the court itself with a little air, not the whole floor.
corners = [project(p) for p in (
    (-SIDE, 0, 0), (SIDE, 0, 0), (SIDE, 0, LEN), (-SIDE, 0, LEN), (SIDE, POST_H, NET_Z),
)]
xs_, ys_ = [c[0] for c in corners], [c[1] for c in corners]
pad = 70
print(f"<!-- viewBox {min(xs_) - pad:.0f} {min(ys_) - pad:.0f} {max(xs_) - min(xs_) + pad * 2:.0f} "
      f"{max(ys_) - min(ys_) + pad * 2:.0f} -->")
