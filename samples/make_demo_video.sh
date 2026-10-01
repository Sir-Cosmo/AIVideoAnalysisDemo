#!/usr/bin/env bash
# Synthetic 640x360 screen recording: cursor moves 0–12 s to a "Speichern" button, which changes colour at 12.71 s.
set -e
ffmpeg -y -loglevel error \
  -f lavfi -i "color=c=0x404040:s=640x360:r=25:d=16" \
  -f lavfi -i "color=c=white:s=8x12:r=25:d=16" \
  -f lavfi -i "anullsrc=r=16000:cl=mono:d=16" \
  -filter_complex "[0:v]drawbox=x=440:y=240:w=120:h=50:color=0x3366cc@1:t=fill,drawbox=x=440:y=240:w=120:h=50:color=0x66aa33@1:t=fill:enable='gte(t,12.71)'[bg];[bg][1:v]overlay=x='if(lt(t,12),40+t*38,496)':y='if(lt(t,12),60+t*17,264)':eval=frame[v]" \
  -map "[v]" -map 2:a -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "${1:-demo.mp4}"
echo "wrote ${1:-demo.mp4}"
