@echo off
setlocal

set "WORKER_URL=%BUDDY_WORKER_BASE_URL%"
if "%WORKER_URL%"=="" (
  echo Set BUDDY_WORKER_BASE_URL to your Worker origin first.
  exit /b 1
)

set "PROMPT=%~1"
if "%PROMPT%"=="" (
  set "PROMPT=Reply with exactly one short sentence confirming this Gemini model is reachable."
)

call :TestModel gemini-3.1-flash-lite-preview
call :TestModel gemini-2.5-flash
exit /b 0

:TestModel
set "MODEL=%~1"
set "REQUEST_FILE=%TEMP%\buddy-gemini-%MODEL%-%RANDOM%.json"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$request = @{ provider = 'gemini'; model = $env:MODEL; max_tokens = 128; stream = $true; system = 'You are a terse API smoke test assistant.'; messages = @(@{ role = 'user'; content = $env:PROMPT }) }; $json = $request | ConvertTo-Json -Depth 20; [System.IO.File]::WriteAllText($env:REQUEST_FILE, $json, [System.Text.UTF8Encoding]::new($false))"
if errorlevel 1 (
  echo Failed to create request JSON for %MODEL%.
  exit /b 1
)

echo.
echo Testing %MODEL% through %WORKER_URL%/chat
curl.exe -sS --no-buffer "%WORKER_URL%/chat" ^
  -H "Content-Type: application/json" ^
  -X POST ^
  --data-binary "@%REQUEST_FILE%"

del "%REQUEST_FILE%" >nul 2>nul
exit /b 0
