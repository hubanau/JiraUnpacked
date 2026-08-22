@echo off
setlocal

rem ============================================================
rem find-large-files.cmd
rem Finds all files under a directory larger than 100 MB,
rem using robocopy (multi-threaded, very fast).
rem
rem Usage:
rem   find-large-files.cmd [directory]
rem
rem   [directory]  Folder to search (recursively).
rem                Defaults to the current directory if omitted.
rem ============================================================

rem 100 MB in bytes (100 * 1024 * 1024)
set "LIMIT=104857600"

rem Target directory: first argument, or current directory.
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=%CD%"

if not exist "%TARGET%\" (
    echo Error: directory "%TARGET%" does not exist.
    exit /b 1
)

echo Searching "%TARGET%" for files larger than 100 MB...
echo.

rem robocopy in list-only mode (/L) never copies anything.
rem   /S       recurse into subdirectories
rem   /MIN:n   only files >= n bytes  (the fast filter)
rem   /FP      print full path
rem   /BYTES   print sizes in bytes
rem   /NC /NDL /NJH /NJS  strip class/dir-list/headers/summary noise
rem   /XJ      skip junctions (avoids loops / reparse traps)
robocopy "%TARGET%" "%TEMP%\__robocopy_null__" * /L /S /MIN:%LIMIT% ^
    /FP /BYTES /NC /NDL /NJH /NJS /XJ

echo.
echo Done.
endlocal
