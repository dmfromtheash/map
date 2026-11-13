#!/bin/bash
# Creates ~/MapWork: the only place where Map work happens on this Mac. Everything (tools, caches, temp) stays inside.
set -e
W=~/MapWork
mkdir -p $W/bin $W/.uv/cache $W/.uv/python $W/upscale $W/agents $W/tmp
if [ -d ~/MapUpscale/esr ]; then
  mv ~/MapUpscale/esr $W/esr
  mv ~/MapUpscale/installation-manifest.json $W/esr/
  # own earlier test leftovers (small, listed explicitly)
  find ~/MapUpscale/mt ~/MapUpscale/smoke -type f -delete
  find ~/MapUpscale/mt ~/MapUpscale/smoke -depth -type d -empty -delete
  rm -f $W/esr/onepiece_demo.mp4 $W/esr/input.jpg $W/esr/input2.jpg
  rmdir ~/MapUpscale
fi
cat > $W/env.sh <<'EOS'
# Source this before any work: keeps every tool, cache and temp file inside ~/MapWork.
export MAPWORK="$HOME/MapWork"
export PATH="$MAPWORK/bin:$MAPWORK/.venv/bin:$PATH"
export UV_CACHE_DIR="$MAPWORK/.uv/cache" UV_PYTHON_INSTALL_DIR="$MAPWORK/.uv/python" UV_TOOL_DIR="$MAPWORK/.uv/tools" UV_TOOL_BIN_DIR="$MAPWORK/bin" UV_NO_MODIFY_PATH=1
export PIP_CACHE_DIR="$MAPWORK/.uv/pip" TMPDIR="$MAPWORK/tmp/" PYTHONDONTWRITEBYTECODE=1 MPLCONFIGDIR="$MAPWORK/tmp/mpl"
EOS
. $W/env.sh
cd $W/tmp
curl -LsSf https://github.com/astral-sh/uv/releases/download/0.8.22/uv-aarch64-apple-darwin.tar.gz -o uv.tgz
tar xzf uv.tgz
mv uv-aarch64-apple-darwin/uv uv-aarch64-apple-darwin/uvx $W/bin/
rmdir uv-aarch64-apple-darwin
rm -f uv.tgz
uv --version
uv python install 3.13
uv venv --python 3.13 $W/.venv
uv pip install --python $W/.venv/bin/python numpy scipy pillow
python -c 'import sys,numpy,scipy,PIL; print(sys.version.split()[0], numpy.__version__, scipy.__version__, PIL.__version__)'
ls $W $W/esr
du -sh $W
