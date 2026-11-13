@echo off
cd /d H:\MapLibrary\pipeline
py -3.13 -B mac_feeder.py --job 60 --ahead 2 >> H:\MapLibrary\mac_feeder.log 2>&1
