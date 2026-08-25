@echo off
rem S1 CUDA run: ORT 1.29 CUDA EP needs the nvidia pip packages' DLL dirs on PATH.
rem Usage: run_cuda.bat [--decoder fp32]
set "PATH=C:\Users\saura\agent-workspace\enviouswispr-windows\spikes\s1\venv-cuda\Lib\site-packages\nvidia\cu13\bin\x86_64;C:\Users\saura\agent-workspace\enviouswispr-windows\spikes\s1\venv-cuda\Lib\site-packages\nvidia\cudnn\bin;%PATH%"
C:\Users\saura\agent-workspace\enviouswispr-windows\spikes\s1\venv-cuda\Scripts\python.exe C:\Users\saura\agent-workspace\enviouswispr-windows\spikes\s1\s1_latency.py --tier cuda %1 %2
