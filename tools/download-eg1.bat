@echo off

setlocal EnableExtensions EnableDelayedExpansion

rem Downloads all 8 EG-1 shards sequentially, collecting per-shard exit codes.

set BASE=C:\Users\saura\agent-workspace\enviouswispr-windows\models\eg-1

if not exist "%BASE%" mkdir "%BASE%"

set RC=0

for /L %%i in (1,1,8) do (

  set "N=%%i"

  if 100%%i LSS 1000 set "N=0%%i"

  if 10%%i LSS 100 set "N=00%%i"

  if %%i LSS 10 set "N=000%%i"

  curl -L --fail --retry 3 -o "%BASE%\eg-1-v2-!N!-of-00008.gguf" "https://models.enviouslabs.co/eg1/v3-eg2/eg-1-v2-!N!-of-00008.gguf"

  if errorlevel 1 (

    echo SHARD %%i FAILED N=!N!

    set /a RC+=1

  ) else (

    echo SHARD %%i OK N=!N!

  )

)

echo DONE rc=%RC%

endlocal & exit /b %RC%

