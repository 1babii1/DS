#!/usr/bin/env bash
set -euo pipefail

# Produces the short, captioned portfolio overview from real browser captures.
# It intentionally does not pretend to show the authenticated assistant flow:
# record that complete, interactive path with docs/demo/storyboard.md instead.

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
assets="$root/docs/demo/assets"
video_dir="$root/docs/demo/video"
work_dir="${TMPDIR:-/tmp}/ds-portfolio-showcase-video"
font="/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

command -v ffmpeg >/dev/null || { echo "ffmpeg is required." >&2; exit 1; }
test -f "$assets/engineering.png" || { echo "Missing $assets/engineering.png" >&2; exit 1; }
test -f "$assets/history.png" || { echo "Missing $assets/history.png" >&2; exit 1; }
test -f "$font" || { echo "Missing a DejaVu Sans font at $font" >&2; exit 1; }

rm -rf "$work_dir"
mkdir -p "$work_dir" "$video_dir"

make_card() {
  local name="$1"
  local text_file="$2"
  local seconds="$3"
  ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "color=c=0x0d1612:s=1920x1080:r=30:d=$seconds" \
    -vf "drawbox=x=168:y=246:w=10:h=426:color=0x5dca99:t=fill,drawtext=fontfile=$font:textfile=$text_file:fontcolor=0xf3f8f5:fontsize=66:line_spacing=22:x=218:y=(h-text_h)/2" \
    -c:v libx264 -pix_fmt yuv420p "$work_dir/$name.mp4"
}

cat >"$work_dir/opening.txt" <<'EOF'
DS / PEOPLE & ORGANIZATION

A distributed-systems portfolio project.
Eight services. One documented reason
behind every consequential decision.
EOF

cat >"$work_dir/proof.txt" <<'EOF'
PROOF, NOT CLAIMS

632 tests across 16 projects — green.
481 / 481 k6 checks — passed.

The local AI may propose. A person approves.
The server bounds what can happen.
EOF

cat >"$work_dir/closing.txt" <<'EOF'
RUN THE TOUR LOCALLY

scripts/demo.sh up

The complete live walkthrough, including
the authenticated assistant, is documented
in docs/demo/storyboard.md.
EOF

make_card opening "$work_dir/opening.txt" 7
make_card proof "$work_dir/proof.txt" 8
make_card closing "$work_dir/closing.txt" 7

ffmpeg -hide_banner -loglevel error -y -loop 1 -framerate 30 -t 9 -i "$assets/engineering.png" \
  -vf "scale=1920:1080,drawbox=x=120:y=865:w=1050:h=118:color=0x0d1612@0.92:t=fill,drawtext=fontfile=$font:text='BROWSER SESSION  →  NEXT.JS BFF  →  SERVICES':fontcolor=0xf3f8f5:fontsize=32:x=164:y=907" \
  -c:v libx264 -pix_fmt yuv420p "$work_dir/engineering.mp4"

ffmpeg -hide_banner -loglevel error -y -loop 1 -framerate 30 -t 9 -i "$assets/history.png" \
  -vf "scale=1920:1080,drawbox=x=120:y=865:w=1110:h=118:color=0x0d1612@0.92:t=fill,drawtext=fontfile=$font:text='ORG HISTORY IS REBUILT FROM THE EVENT LOG':fontcolor=0xf3f8f5:fontsize=32:x=164:y=907" \
  -c:v libx264 -pix_fmt yuv420p "$work_dir/history.mp4"

printf "file '%s'\n" \
  "$work_dir/opening.mp4" "$work_dir/engineering.mp4" "$work_dir/proof.mp4" \
  "$work_dir/history.mp4" "$work_dir/closing.mp4" >"$work_dir/segments.txt"

ffmpeg -hide_banner -loglevel error -y -f concat -safe 0 -i "$work_dir/segments.txt" \
  -c:v libx264 -preset slow -crf 21 -movflags +faststart -an \
  "$video_dir/portfolio-overview.mp4"

echo "Wrote $video_dir/portfolio-overview.mp4"
