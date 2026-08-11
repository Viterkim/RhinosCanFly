# Scripts

## Mov -> Webm

```powershell
ffmpeg -i 1.mov -vf "scale=-2:720,fps=30" -c:v libvpx-vp9 -crf 34 -b:v 0 -row-mt 1 -an 1.webm
```

## Webm -> Gif

```powershell
ffmpeg -i 1.webm -filter_complex "[0:v]fps=10,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=64[p];[b][p]paletteuse=dither=bayer:bayer_scale=5" -loop 0 1.gif
```
