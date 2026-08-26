@echo off
setlocal
set "SPIKE_ROOT=%~dp0"
set "CUDA_VENV=%SPIKE_ROOT%venv-cuda"
set "PATH=%CUDA_VENV%\Lib\site-packages\nvidia\cu13\bin\x86_64;%CUDA_VENV%\Lib\site-packages\nvidia\cudnn\bin;%PATH%"
"%CUDA_VENV%\Scripts\python.exe" "%SPIKE_ROOT%s1_longclip.py"
endlocal
