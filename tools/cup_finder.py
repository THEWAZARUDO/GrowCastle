#!/usr/bin/env python3
"""Find the diamond cup in the "Kim cuong o dau?" minigame and follow it through the shuffle.

Install: py -m pip install opencv-python numpy mss
Live:    py cup_finder.py                      (start it BEFORE pressing START in the minigame)
Offline: py cup_finder.py --video record.mp4   (replay a screen recording to test)

How it works (measured on a recording of the minigame):
  * During the shuffle ALL cups share one common rotation angle that grows steadily every frame,
    EXCEPT the cup holding the diamond, which spins independently.
  * Every frame: find the common angle (the angle at which most cups match cup_template.png),
    "explain away" every cup that matches at that angle - the cup-coloured pixels that are left over
    are the diamond cup. That blob is followed with a gated, velocity-predicted track.
  * When all 8 cups match the common angle again, the diamond cup is followed as the nearest of the 8
    aligned cups until it lands in its slot. Its final position = the answer.
The old optical-flow approach (LK + partial affine) lost the cup after a few frames because the
cup turns 60-170 degrees per frame and is hidden behind other cups; this version never needs to
follow cup *features*, only "which cup-coloured pixels are not explained by the common angle".
"""
from __future__ import annotations

import argparse
import math
import time
from pathlib import Path

import cv2
import numpy as np

IDLE, SPINNING, ALIGNED = 0, 1, 2
PHASE_NAME = ("waiting for the shuffle", "diamond cup spinning", "cups aligned - landing")


def tan_mask(bgr: np.ndarray) -> np.ndarray:
    """Cup-coloured (tan wood) pixels."""
    b, g, r = (bgr[..., i].astype(np.int16) for i in range(3))
    return (r >= 150) & (g >= 110) & (r - b >= 40)


# --------------------------------------------------------------------------------------------------
# First frame: find the 8 cups (upright, before the shuffle) with multi-scale template matching
# --------------------------------------------------------------------------------------------------
def find_cups(frame: np.ndarray, template_path: Path, threshold: float, cup_count: int = 8):
    template = cv2.imread(str(template_path), cv2.IMREAD_GRAYSCALE)
    if template is None:
        raise SystemExit(f"Cannot read cup reference image: {template_path}")
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    search = cv2.resize(gray, None, fx=.25, fy=.25, interpolation=cv2.INTER_AREA)
    candidates = []
    for scale in np.geomspace(.2, 1.9, 40):
        tw = max(12, round(template.shape[1] * scale * .25))
        th = max(12, round(template.shape[0] * scale * .25))
        if tw >= search.shape[1] or th >= search.shape[0]:
            continue
        sized = cv2.resize(template, (tw, th),
                           interpolation=cv2.INTER_AREA if scale < 1 else cv2.INTER_CUBIC)
        response = cv2.matchTemplate(search, sized, cv2.TM_CCORR_NORMED)
        for _ in range(15):
            _, score, _, (x, y) = cv2.minMaxLoc(response)
            if score < threshold:
                break
            candidates.append((float(score), scale, (x * 4, y * 4, tw * 4, th * 4)))
            dx, dy = max(2, tw // 2), max(2, th // 2)
            response[max(0, y - dy):y + dy + 1, max(0, x - dx):x + dx + 1] = -1
    candidates.sort(key=lambda item: item[0], reverse=True)
    picked = []
    for score, scale, (x, y, w, h) in candidates:
        cx, cy = x + w / 2, y + h / 2
        if any(abs(cx - (px + pw / 2)) < .6 * min(w, pw) and abs(cy - (py + ph / 2)) < .6 * min(h, ph)
               for _, _, (px, py, pw, ph) in picked):
            continue
        picked.append((score, scale, (x, y, w, h)))
    picked = picked[:cup_count]
    picked.sort(key=lambda item: (item[2][1] + item[2][3] / 2, item[2][0] + item[2][2] / 2))
    return picked


# --------------------------------------------------------------------------------------------------
# The spinning-cup tracker
# --------------------------------------------------------------------------------------------------
class SpinTracker:
    def __init__(self, tpl_bgra, cup_tan_w, cup_tan_area, ring_origin, step=15):
        tm = tan_mask(tpl_bgra[..., :3])
        _, xs = np.where(tm)
        s = cup_tan_w / float(xs.max() - xs.min() + 1)            # template px -> screen px
        self.cup_w = tpl_bgra.shape[1] * s
        self.cup_h = tpl_bgra.shape[0] * s
        self.k = float(np.clip(45.0 / self.cup_h, .2, 1.0))      # working resolution
        k = self.k
        self.ox, self.oy = ring_origin
        tw, th = max(8, round(self.cup_w * k)), max(8, round(self.cup_h * k))
        t = cv2.resize(tpl_bgra, (tw, th), interpolation=cv2.INTER_AREA)
        g = cv2.cvtColor(t[..., :3], cv2.COLOR_BGR2GRAY)
        m = (t[..., 3] > 127).astype(np.uint8) * 255
        d = int(math.ceil(math.hypot(tw, th))) + 2
        self.rot = {}
        for a in range(0, 360, step):
            mat = cv2.getRotationMatrix2D((tw / 2.0, th / 2.0), a, 1.0)
            mat[0, 2] += d / 2.0 - tw / 2.0
            mat[1, 2] += d / 2.0 - th / 2.0
            self.rot[a] = (cv2.warpAffine(g, mat, (d, d), flags=cv2.INTER_LINEAR),
                           cv2.warpAffine(m, mat, (d, d), flags=cv2.INTER_NEAREST))
        self.cup_area = cup_tan_area * k * k
        self.nms = max(6, int(round(.17 * self.cup_w * k * 2)))
        self.win = 90
        self.theta = None
        self.pos = None            # (x, y) of the diamond cup, screen pixels
        self.vel = (0.0, 0.0)
        self.phase = IDLE
        self.streak = 0
        self.frames = 0
        self.lost_total = 0
        self.trail = []

    def _peaks(self, g, angle, thr, maxn):
        t, m = self.rot[angle]
        if g.shape[0] < t.shape[0] or g.shape[1] < t.shape[1]:
            return []
        res = np.nan_to_num(cv2.matchTemplate(g, t, cv2.TM_CCORR_NORMED, mask=m),
                            nan=0.0, posinf=0.0, neginf=0.0)
        out = []
        for _ in range(maxn):
            _, sc, _, (px, py) = cv2.minMaxLoc(res)
            if sc < thr:
                break
            out.append((sc, px + t.shape[1] / 2.0, py + t.shape[0] / 2.0))
            r = self.nms
            res[max(0, py - r):py + r + 1, max(0, px - r):px + r + 1] = -1
        return out

    def update(self, ring_bgr: np.ndarray) -> str:
        self.frames += 1
        k = self.k
        half = cv2.resize(ring_bgr, None, fx=k, fy=k, interpolation=cv2.INTER_AREA)
        gray = cv2.cvtColor(half, cv2.COLOR_BGR2GRAY)
        angles = (list(self.rot) if self.theta is None else
                  [a for a in self.rot if abs((a - self.theta + 180) % 360 - 180) <= self.win])
        best = None
        for a in angles:                      # 1. the common angle
            pk = self._peaks(gray, a, .95, 7)
            score = sum(p[0] for p in pk)
            if best is None or score > best[0]:
                best = (score, a, pk)
        _, self.theta, group = best
        strict = self._peaks(gray, self.theta, .97, 8)
        all8 = len(strict) >= 8
        if self.phase == IDLE:
            if not all8:
                self.phase = SPINNING
        elif self.phase == SPINNING:
            self.streak = self.streak + 1 if all8 else 0
            if self.streak >= 2:
                self.phase = ALIGNED
        if self.phase == IDLE:
            return "idle"
        status = (self._residual(half, group) if self.phase == SPINNING
                  else self._nearest_cup(strict))
        if self.pos is not None:
            self.trail.append(self.pos)
            self.trail = self.trail[-60:]
        return status

    def _residual(self, half, group):
        k = self.k
        tm = tan_mask(half).astype(np.uint8)
        expl = np.zeros(tm.shape, np.uint8)
        m = self.rot[self.theta][1]
        for _, cx, cy in group:               # 2. explain away every cup at the common angle
            px, py = int(round(cx - m.shape[1] / 2.0)), int(round(cy - m.shape[0] / 2.0))
            y0, x0 = max(0, py), max(0, px)
            y1, x1 = min(tm.shape[0], py + m.shape[0]), min(tm.shape[1], px + m.shape[1])
            if y1 > y0 and x1 > x0:
                expl[y0:y1, x0:x1] |= (m[y0 - py:y1 - py, x0 - px:x1 - px] > 0).astype(np.uint8)
        expl = cv2.dilate(expl, np.ones((5, 5), np.uint8))
        resid = ((tm > 0) & (expl == 0)).astype(np.uint8)     # 3. what is left is the diamond cup
        resid = cv2.morphologyEx(resid, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
        n, _, stats, cent = cv2.connectedComponentsWithStats(resid, connectivity=8)
        blobs = [(float(stats[i, cv2.CC_STAT_AREA]), self.ox + cent[i][0] / k, self.oy + cent[i][1] / k)
                 for i in range(1, n) if stats[i, cv2.CC_STAT_AREA] >= .06 * self.cup_area]
        if self.pos is None:                  # first spinning frame: the biggest leftover blob
            if not blobs:
                return "lost"
            _, x, y = max(blobs)
            self.pos = (x, y)
            return "seen"
        px_, py_ = self.pos[0] + self.vel[0], self.pos[1] + self.vel[1]
        chosen, best_cost = None, 9.0
        for area, x, y in blobs:              # 4. gated by the predicted position
            d = math.hypot(x - px_, y - py_) / self.cup_w
            cost = d - .5 * min(area / self.cup_area, 1.0)
            if d < 1.3 and cost < best_cost:
                best_cost, chosen = cost, (x, y)
        return self._apply(chosen)

    def _nearest_cup(self, strict):
        k = self.k
        px_, py_ = self.pos[0] + self.vel[0], self.pos[1] + self.vel[1]
        chosen, best_d = None, 9.0
        for _, cx, cy in strict:
            x, y = self.ox + cx / k, self.oy + cy / k
            d = math.hypot(x - px_, y - py_) / self.cup_w
            if d < .8 and d < best_d:
                best_d, chosen = d, (x, y)
        return self._apply(chosen)

    def _apply(self, chosen):
        if chosen:
            x, y = chosen
            self.vel = (.6 * (x - self.pos[0]) + .4 * self.vel[0], .6 * (y - self.pos[1]) + .4 * self.vel[1])
            self.pos = (x, y)
            return "seen"
        self.lost_total += 1
        self.vel = (self.vel[0] * .6, self.vel[1] * .6)
        self.pos = (self.pos[0] + self.vel[0], self.pos[1] + self.vel[1])
        return "lost"


# --------------------------------------------------------------------------------------------------
def draw(frame, tracker, slots, answer):
    view = frame.copy()
    for i, (cx, cy) in enumerate(slots, 1):
        cv2.circle(view, (int(cx), int(cy)), 5, (200, 200, 200), -1, cv2.LINE_AA)
        cv2.putText(view, str(i), (int(cx) + 8, int(cy) - 8), cv2.FONT_HERSHEY_SIMPLEX, .6,
                    (200, 200, 200), 2, cv2.LINE_AA)
    if tracker.pos is not None:
        for a, b in zip(tracker.trail, tracker.trail[1:]):
            cv2.line(view, tuple(map(int, a)), tuple(map(int, b)), (60, 235, 90), 3, cv2.LINE_AA)
        cx, cy = map(int, tracker.pos)
        r = int(tracker.cup_w * .6)
        cv2.rectangle(view, (cx - r, cy - int(r * 1.3)), (cx + r, cy + int(r * 1.3)), (60, 235, 90), 3)
    text = f"{PHASE_NAME[tracker.phase]}"
    if answer:
        text = f"DIAMOND CUP -> slot {answer}"
    cv2.rectangle(view, (0, 0), (view.shape[1], 42), (24, 27, 30), -1)
    cv2.putText(view, f"{text}   |   Q: quit", (12, 29), cv2.FONT_HERSHEY_SIMPLEX, .75,
                (245, 245, 245), 2, cv2.LINE_AA)
    scale = min(1.0, 1200 / view.shape[1], 800 / view.shape[0])
    if scale < 1:
        view = cv2.resize(view, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    cv2.imshow("Cup Finder", view)
    return cv2.waitKey(1) & 0xFF


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--monitor", type=int, default=1, help="screen number starting at 1")
    ap.add_argument("--template", type=Path, default=Path(__file__).with_name("cup_template.png"))
    ap.add_argument("--threshold", type=float, default=.62, help="cup match score for the first frame")
    ap.add_argument("--cup-count", type=int, default=8)
    ap.add_argument("--video", type=Path, help="replay a screen recording instead of the live screen")
    ap.add_argument("--no-preview", action="store_true")
    args = ap.parse_args()

    template = cv2.imread(str(args.template), cv2.IMREAD_UNCHANGED)
    if template is None:
        raise SystemExit(f"Cannot read {args.template}")
    if template.shape[2] == 3:
        template = cv2.cvtColor(template, cv2.COLOR_BGR2BGRA)

    if args.video:
        cap = cv2.VideoCapture(str(args.video))
        grab = lambda: (lambda ok_f: ok_f[1] if ok_f[0] else None)(cap.read())
    else:
        import mss
        sct = mss.mss()
        if not 1 <= args.monitor < len(sct.monitors):
            raise SystemExit(f"Screen not found. Available: 1..{len(sct.monitors) - 1}")
        mon = sct.monitors[args.monitor]
        grab = lambda: cv2.cvtColor(np.asarray(sct.grab(mon)), cv2.COLOR_BGRA2BGR)

    first = grab()
    if first is None:
        raise SystemExit("Cannot read the first frame")
    cups = find_cups(first, args.template, args.threshold, args.cup_count)
    if len(cups) != args.cup_count:
        raise SystemExit(f"Found {len(cups)} of {args.cup_count} cups. Start the script while all 8 cups "
                         "are visible and upright, or lower --threshold (e.g. .55).")
    slots = [(x + w / 2, y + h / 2) for _, _, (x, y, w, h) in cups]
    print("Cups (numbered top-to-bottom, then left-to-right):")
    for i, ((x, y), (sc, _, _)) in enumerate(zip(slots, cups), 1):
        print(f"  cup {i}: centre=({x:.0f},{y:.0f}) match={sc:.2f}")

    # Measure one on-screen cup: every cup box is disjoint from its neighbours
    widths, areas = [], []
    for _, _, (x, y, w, h) in cups:
        crop = first[max(0, y):y + h, max(0, x):x + w]
        m = tan_mask(crop)
        if m.sum() > 200:
            _, xs = np.where(m)
            widths.append(xs.max() - xs.min() + 1)
            areas.append(int(m.sum()))
    cup_w, cup_area = float(np.median(widths)), float(np.median(areas))
    xs_all = [x for _, _, (x, _, w, _) in cups] + [x + w for _, _, (x, _, w, _) in cups]
    ys_all = [y for _, _, (_, y, _, h) in cups] + [y + h for _, _, (_, y, _, h) in cups]
    pad = int(cup_w * 1.4)
    x0, x1 = max(0, min(xs_all) - pad), min(first.shape[1], max(xs_all) + pad)
    y0, y1 = max(0, min(ys_all) - pad), min(first.shape[0], max(ys_all) + pad)
    tracker = SpinTracker(template, cup_w, cup_area, (x0, y0))
    print(f"\nTracking area {x1 - x0}x{y1 - y0}; cup width ~{tracker.cup_w:.0f}px. "
          "Press START in the minigame now; waiting for the shuffle...\n")

    answer, last_move, start = None, 0, time.monotonic()
    while True:
        frame = grab()
        if frame is None:
            break
        tracker.update(frame[y0:y1, x0:x1])
        if tracker.pos is not None and tracker.phase == ALIGNED:
            slot = 1 + min(range(len(slots)), key=lambda i: math.hypot(slots[i][0] - tracker.pos[0],
                                                                         slots[i][1] - tracker.pos[1]))
            if answer != slot:
                answer, last_move = slot, tracker.frames
                print(f"  diamond cup is heading to slot {slot}")
            if tracker.frames - last_move > 12 and tracker.vel[0] ** 2 + tracker.vel[1] ** 2 < 1:
                print(f"\nDONE: the diamond cup ended in slot {slot} "
                      f"(position {tracker.pos[0]:.0f},{tracker.pos[1]:.0f}; tracked {tracker.frames} frames, "
                      f"{tracker.lost_total} predicted-only frames).")
                break
        if not args.no_preview:
            if draw(frame, tracker, slots, answer) in (ord("q"), 27):
                break
        if not args.video:
            time.sleep(.005)
        if time.monotonic() - start > 90:
            print("Timed out waiting for a shuffle.")
            break
    cv2.destroyAllWindows()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        cv2.destroyAllWindows()
