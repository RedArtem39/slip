@echo off
setlocal
set "HERE=%~dp0"
if "%HERE:~-1%"=="\" set "HERE=%HERE:~0,-1%"

echo Adding "%HERE%" to your PATH...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$d='%HERE%'; $p=[Environment]::GetEnvironmentVariable('Path','User'); if ((($p -split ';') -notcontains $d)) { [Environment]::SetEnvironmentVariable('Path', ($p.TrimEnd(';') + ';' + $d), 'User') ; Write-Host 'Added.' } else { Write-Host 'Already on PATH.' }"

echo.
echo Done. Opening a test window (close it whenever)...
start "slip" cmd /k ""%HERE%\slip.exe" help"
