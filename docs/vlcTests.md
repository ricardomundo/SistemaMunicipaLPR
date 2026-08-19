# Simulación de cámara IP con VLC (RTSP) — pruebas de `edge/`

> Referenciado desde [ImplementersGuide.md §10](ImplementersGuide.md#10-pipeline-edge-python--edge). Mientras no hay una cámara IP física ni un nodo Jetson desplegado en campo, este es el método para probar el pipeline Edge contra un stream de red real en vez de un archivo de video local — `edge/config.yaml`'s `camera.source` ya soporta URLs RTSP (`cv2.VideoCapture` las consume igual que una cámara IP real), así que desde la perspectiva del pipeline un RTSP de VLC en la misma máquina es indistinguible de una cámara IP en la red.

## Requisitos

- VLC instalado en la máquina donde se hace la prueba (no tiene que ser la misma que corre `edge/`, siempre que haya conectividad de red entre ambas).
- Un video de prueba con placas mexicanas visibles (el mismo usado para la verificación end-to-end inicial del pipeline sirve).

## 1. Emitir el video como servidor RTSP

Desde PowerShell:

```powershell
& "C:\Program Files\VideoLAN\VLC\vlc.exe" -vvv "C:\ruta\a\tu_video_de_prueba.mp4" --sout "#rtp{sdp=rtsp://:8554/stream}" --input-repeat=99999
```

- `--input-repeat=99999` deja el video repitiéndose en loop — más confiable que `--loop` cuando se combina con `--sout` (bug conocido de VLC donde `--loop` no reinicia bien el stream de salida hacia RTSP).
- Deja esta ventana corriendo mientras dura la prueba. El servidor RTSP tarda unos segundos en quedar listo tras arrancar VLC — dale ese margen antes del paso 3.
- El stream queda expuesto en `rtsp://<ip-de-esta-máquina>:8554/stream` — `127.0.0.1` si `edge/` corre en la misma máquina que VLC, o la IP de red si corre en otra.

## 2. Apuntar `edge/config.yaml` al stream

```yaml
camera:
  id: "CAM-TEST-01"
  source: "rtsp://127.0.0.1:8554/stream"
```

(mantener el resto de `config.yaml` sin cambios respecto a la prueba con archivo local).

## 3. Correr el pipeline Edge normal

```powershell
cd edge
venv\Scripts\Activate.ps1
python -m src.main
```

`CameraStream` (en `capture.py`) se conecta al RTSP igual que a un archivo o cámara USB — detección, OCR, publish a RabbitMQ y alerta en SQL funcionan exactamente igual que en la prueba original con archivo local.

## Troubleshooting

- **`CameraStream` loguea "No se pudo abrir la fuente de video" en loop:** casi siempre es que `main.py` arrancó antes de que el servidor RTSP de VLC estuviera listo. `CameraStream._connect()` reintenta solo, así que basta con esperar unos segundos — si no se recupera, confirma que la ventana de VLC sigue abierta y no marcó error.
- **El stream se corta a medias:** revisa que `--input-repeat` se haya aplicado (si VLC llega al final del video sin repetir, la conexión RTSP se cierra) — confirma en la consola de VLC que el video reinició en vez de terminar el proceso.
- **Firewall de Windows bloqueando el puerto 8554:** si `edge/` corre en otra máquina que la de VLC, confirma que el firewall permite tráfico entrante a ese puerto en la máquina que corre VLC.
