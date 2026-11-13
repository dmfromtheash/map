## Common rules for every Sol agent on this Mac (read first)

Project: Map — the owner's native offline map editor (Windows, C#/.NET). The owner's 39 081 reference pictures are being
upscaled ×2/×4 (two Real-ESRGAN models blended 60/40 + grain restore, recipe `finish_asset.py`) into an external drive
library that Map attaches. Owner rules: pictures must stay exactly as drawn — ONLY the resolution grows (no repainting,
no style change, no cropping of content, no colour change). Russian UI texts: dry, factual.

- This Mac is shared with the owner. Work ONLY inside your own folder (your `-C` folder under `~/MapWork/agents/`).
  Never write, move or delete anything outside it. Never stop, kill or restart processes you did not start yourself.
  Never touch `~/MapWork/upscale/` (the LIVE upscale worker runs there) or other agents' folders.
- Before any command: `source ~/MapWork/env.sh` (Python 3.13 venv with numpy 2.5.1, scipy 1.18.0, Pillow 12.2.0; uv).
  Set `export TMPDIR="$PWD/tmp"` (create it) so temporary files stay in your folder. Do not install anything
  system-wide; if you truly need an extra Python package, install it into a venv inside your own folder and say so.
- Real-ESRGAN: `~/MapWork/esr/realesrgan-ncnn-vulkan` with `-m ~/MapWork/esr/models` (read-only). The GPU is shared with
  the live worker: keep your own GPU runs short and sequential.
- Do not delete your inputs. At the end delete your own temporary/intermediate files (keep deliverables + report).
- No git commits/pushes. No network use except what your task says.
- At the end write a Russian report `SOL_REPORT.md` in your folder: what you produced (files), how you verified it
  (exact commands + numbers), what is NOT verified, risks. Short and factual. Statuses: FACT / PARTIAL / NOT CHECKED.
