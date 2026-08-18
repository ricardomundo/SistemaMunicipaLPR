"""
Punto de entrada del pipeline Edge (Fase 3): OpenCV captura → YOLO detección → PaddleOCR →
selección por confianza → publish. Un proceso por cámara física.

Uso:
    cd edge
    cp config.example.yaml config.yaml   # y ajustar
    pip install -r requirements.txt
    python -m src.main [ruta a config.yaml, default "config.yaml"]

Ver ImplementersGuide.md §10 para el detalle completo (incluyendo el gap del modelo de
detección de placas, que este repo no incluye).
"""
from __future__ import annotations

import logging
import sys
import threading
import time
from datetime import datetime
from pathlib import Path

from .buffer import EventBuffer
from .capture import CameraStream
from .config import AppConfig, load_config
from .detector import PlateDetector
from .events import PlateReadEvent
from .ocr import PlateOcr
from .publisher import RabbitMqPublisher

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger("edge.main")


def _save_plate_image(cfg: AppConfig, event_id: str, crop) -> str:
    """Guarda el recorte de la placa en disco local y devuelve la ruta relativa a usar como
    ImageReference. NOTA (gap conocido): no hay todavía un uploader hacia un storage central —
    ver la nota en config.example.yaml."""
    import cv2

    save_dir = Path(cfg.images.save_dir) / cfg.camera.id / datetime.utcnow().strftime("%Y-%m-%d")
    save_dir.mkdir(parents=True, exist_ok=True)
    image_path = save_dir / f"{event_id}.jpg"
    cv2.imwrite(str(image_path), crop)
    return str(image_path)


def _buffer_drain_loop(cfg: AppConfig, buffer: EventBuffer, stop_event: threading.Event) -> None:
    """Hilo de fondo: intenta drenar el buffer local hacia RabbitMQ. Corre en su propio hilo con
    su propia conexión pika (BlockingConnection no es segura entre threads) — separado del hilo
    principal, que también publica directo."""
    publisher = RabbitMqPublisher(
        host=cfg.rabbitmq.host,
        port=cfg.rabbitmq.port,
        virtual_host=cfg.rabbitmq.virtual_host,
        username=cfg.rabbitmq.username,
        password=cfg.rabbitmq.password,
        queue=cfg.rabbitmq.queue,
    )

    while not stop_event.is_set():
        stop_event.wait(cfg.buffer.flush_interval_seconds)

        pending = buffer.pending_count()
        if pending == 0:
            continue

        logger.info("Drenando buffer local: %d evento(s) pendientes.", pending)
        batch = buffer.dequeue_batch(cfg.buffer.max_batch_per_flush)
        for buffered in batch:
            if not publisher.publish_bytes(buffered.event_json):
                # Se detiene en el primer fallo del batch para no reordenar eventos —
                # se reintenta desde el mismo punto en el siguiente ciclo.
                break
            buffer.remove(buffered.id)

    publisher.close()


def run(config_path: str) -> None:
    cfg = load_config(config_path)

    logger.info("Cámara '%s', fuente=%s.", cfg.camera.id, cfg.camera.source)
    camera = CameraStream(cfg.camera.source, reconnect_seconds=cfg.rabbitmq.reconnect_seconds)
    detector = PlateDetector(cfg.detector.model_path, cfg.detector.min_confidence)
    ocr = PlateOcr(cfg.ocr.lang, cfg.ocr.min_confidence, cfg.ocr.plate_pattern)
    buffer = EventBuffer(cfg.buffer.sqlite_path)
    publisher = RabbitMqPublisher(
        host=cfg.rabbitmq.host,
        port=cfg.rabbitmq.port,
        virtual_host=cfg.rabbitmq.virtual_host,
        username=cfg.rabbitmq.username,
        password=cfg.rabbitmq.password,
        queue=cfg.rabbitmq.queue,
    )

    stop_event = threading.Event()
    drain_thread = threading.Thread(
        target=_buffer_drain_loop, args=(cfg, buffer, stop_event), daemon=True
    )
    drain_thread.start()

    last_seen: dict[str, float] = {}  # placa -> hora del último publish (debounce)
    frame_index = 0

    logger.info("Pipeline arrancado. Ctrl+C para detener.")
    try:
        while True:
            frame = camera.read()
            if frame is None:
                continue

            frame_index += 1
            if frame_index % max(cfg.camera.frame_skip, 1) != 0:
                continue

            detections = detector.detect(frame)
            if not detections:
                continue

            # nos quedamos con la detección de mayor confianza si hay varias en el mismo frame
            best_detection = max(detections, key=lambda d: d.confidence)

            sharp_frame = camera.best_of_burst(frame, cfg.camera.burst_size)
            crop = best_detection.crop(sharp_frame, padding=cfg.detector.crop_padding)
            if crop.size == 0:
                continue

            ocr_result = ocr.read(crop)
            if ocr_result is None:
                logger.debug("OCR sin resultado suficientemente confiable — descartado.")
                continue

            now = time.monotonic()
            last = last_seen.get(ocr_result.text)
            if last is not None and (now - last) < cfg.camera.plate_cooldown_seconds:
                continue  # mismo vehículo, todavía dentro de la ventana de cooldown
            last_seen[ocr_result.text] = now

            event = PlateReadEvent.create(
                plate_text=ocr_result.text,
                camera_id=cfg.camera.id,
                confidence=min(best_detection.confidence, ocr_result.confidence),
            )
            # El nombre del archivo usa el EventId ya generado (PlateReadEvent.create lo asigna),
            # así que la imagen queda guardada primero y luego se referencia desde el evento.
            event.ImageReference = _save_plate_image(cfg, event.EventId, crop)

            logger.info(
                "Placa detectada: %s (confianza=%.2f, cámara=%s).",
                event.PlateText, event.Confidence, event.CameraId,
            )

            if not publisher.publish_event(event):
                buffer.enqueue(event.to_json_bytes())

    except KeyboardInterrupt:
        logger.info("Deteniendo (Ctrl+C)...")
    finally:
        stop_event.set()
        drain_thread.join(timeout=5)
        publisher.close()
        camera.release()


if __name__ == "__main__":
    config_arg = sys.argv[1] if len(sys.argv) > 1 else "config.yaml"
    run(config_arg)
