@echo off
cd /d H:\MapLibrary\sol\sol1
rem Owner 13.11: the GPU rests 15 s after every Real-ESRGAN run (86 C, two driver resets).
set MAP_GPU_COOLDOWN=15
py -3.13 -B run_upscale2.py C:\Users\Leshe\Pictures\refs H:\MapLibrary\lists\all.txt --workers 6 --batch 120 >> H:\MapLibrary\upscale.log 2>&1
