@echo off
setlocal
set "VW_MCP_EXECUTABLE=%~dp0..\bin\win-x64\ValidatedWorld.Mcp.exe"
if not exist "%VW_MCP_EXECUTABLE%" (
  echo ValidatedWorld MCP executable is missing. Reinstall the complete win-x64 plugin package. 1>&2
  exit /b 9009
)
"%VW_MCP_EXECUTABLE%" %*
exit /b %ERRORLEVEL%
