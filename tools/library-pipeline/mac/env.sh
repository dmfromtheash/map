# Source this before any work: keeps every tool, cache and temp file inside ~/MapWork.
export MAPWORK="$HOME/MapWork"
export PATH="$MAPWORK/bin:$MAPWORK/.venv/bin:$PATH"
export UV_CACHE_DIR="$MAPWORK/.uv/cache" UV_PYTHON_INSTALL_DIR="$MAPWORK/.uv/python" UV_TOOL_DIR="$MAPWORK/.uv/tools" UV_TOOL_BIN_DIR="$MAPWORK/bin" UV_NO_MODIFY_PATH=1
export PIP_CACHE_DIR="$MAPWORK/.uv/pip" TMPDIR="$MAPWORK/tmp/" PYTHONDONTWRITEBYTECODE=1 MPLCONFIGDIR="$MAPWORK/tmp/mpl"
# .NET 10 SDK for Map correctness tests on the Mac (owner 13.11), packages and CLI state also inside ~/MapWork.
export DOTNET_ROOT="$MAPWORK/dotnet" PATH="$MAPWORK/dotnet:$PATH" DOTNET_CLI_HOME="$MAPWORK/.dotnet-home"
export NUGET_PACKAGES="$MAPWORK/.nuget/packages" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
