"""
Captura de video con OpenCV, con soporte de "ráfaga + selección por nitidez" para mitigar motion
blur a velocidades altas (hasta 160 km/h, ver ArchitectureGuide.md §1): en vez de quedarse con el
primer frame donde se detectó algo, se capturan `burst_size` frames adicionales en rápida
sucesión y se elige el más nítido (mayor varianza del Laplaciano) antes de recortar y mandar a
OCR — un frame nítido mejora mucho la precisión de EasyOCR frente a uno borroso por movimiento.
"""
from __future__ import annotations

import logging
import time
from typing import Any, Optional

import cv2
import numpy as np

logger = logging.getLogger(__name__)


def sharpness_score(frame: np.ndarray) -> float:
    """Varianza del Laplaciano — heurística estándar y barata para medir nitidez/enfoque.
    Valores más altos = imagen más nítida (menos motion blur)."""
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    return float(cv2.Laplacian(gray, cv2.CV_64F).var())


class CameraStream:
    """Envuelve cv2.VideoCapture con reconexión automática — un stream RTSP que se cae no debe
    tumbar el proceso completo, debe reintentar."""

    def __init__(self, source: Any, reconnect_seconds: float = 3.0):
        self._source = source
        self._reconnect_seconds = reconnect_seconds
        self._cap: Optional[cv2.VideoCapture] = None
        self._connect()

    def _connect(self) -> None:
        logger.info("Conectando a la fuente de video: %s", self._source)
        self._cap = cv2.VideoCapture(self._source)
        if not self._cap.isOpened():
            logger.warning("No se pudo abrir la fuente de video '%s'.", self._source)

    def read(self) -> Optional[np.ndarray]:
        """Lee un frame. Si la conexión se cayó, intenta reconectar (bloqueante, con espera) en
        vez de lanzar una excepción — el llamador simplemente ve `None` mientras tanto."""
        if self._cap is None or not self._cap.isOpened():
            time.sleep(self._reconnect_seconds)
            self._connect()
            return None

        ok, frame = self._cap.read()
        if not ok:
            logger.warning("Lectura de frame falló — reintentando conexión en %.1fs.", self._reconnect_seconds)
            self._cap.release()
            self._cap = None
            return None

        return frame

    def read_burst(self, count: int) -> list[np.ndarray]:
        """Captura hasta `count` frames adicionales en sucesión rápida (sin dormir entre ellos —
        se apoya en el framerate nativo del stream). Usado justo después de una detección
        positiva, para elegir el frame más nítido del vehículo en tránsito antes de hacer OCR."""
        frames = []
        for _ in range(count):
            frame = self.read()
            if frame is not None:
                frames.append(frame)
        return frames

    def best_of_burst(self, first_frame: np.ndarray, burst_size: int) -> np.ndarray:
        """Combina el frame donde se detectó la placa con una ráfaga adicional, y devuelve el más
        nítido de todos."""
        candidates = [first_frame] + self.read_burst(burst_size)
        return max(candidates, key=sharpness_score)

    def release(self) -> None:
        if self._cap is not None:
            self._cap.release()
