"""
Detección de placas con YOLO (ultralytics). Envuelve el modelo cargado desde
`detector.model_path` en config.yaml — ESE modelo debe estar entrenado específicamente para
detectar placas (ver la nota extensa en config.example.yaml y ImplementersGuide.md §10). Este
módulo no sabe ni le importa qué arquitectura YOLO exacta es (v8, v11, etc.) — ultralytics.YOLO
carga cualquiera de forma transparente a partir del archivo .pt.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from ultralytics import YOLO


@dataclass
class Detection:
    x1: int
    y1: int
    x2: int
    y2: int
    confidence: float

    def crop(self, frame: np.ndarray, padding: int = 0) -> np.ndarray:
        # Un margen chico alrededor del bbox ayuda al OCR cuando YOLO recorta el borde de algún
        # carácter — a costa de meter algo del marco/tornillos de la placa si el padding es
        # demasiado grande. `padding` es configurable (detector.crop_padding en config.yaml).
        y1 = max(0, self.y1 - padding)
        y2 = min(frame.shape[0], self.y2 + padding)
        x1 = max(0, self.x1 - padding)
        x2 = min(frame.shape[1], self.x2 + padding)
        return frame[y1:y2, x1:x2]


class PlateDetector:
    def __init__(self, model_path: str, min_confidence: float):
        self._model = YOLO(model_path)
        self._min_confidence = min_confidence

    def detect(self, frame: np.ndarray) -> list[Detection]:
        results = self._model.predict(frame, verbose=False)
        detections: list[Detection] = []

        for result in results:
            for box in result.boxes:
                confidence = float(box.conf[0])
                if confidence < self._min_confidence:
                    continue

                x1, y1, x2, y2 = map(int, box.xyxy[0].tolist())
                detections.append(Detection(x1=x1, y1=y1, x2=x2, y2=y2, confidence=confidence))

        return detections
