"""
OCR del recorte de placa con PaddleOCR, con normalización de texto y selección por confianza
("selección por confianza" del objetivo de Fase 3): si PaddleOCR devuelve varios segmentos de
texto dentro del mismo recorte (ruido, sombras, doble línea en placas de moto), nos quedamos con
el de mayor confianza en vez de concatenar todo a ciegas.

Se eligió PaddleOCR en vez de EasyOCR (2026) para leer placas mexicanas — mismo alfabeto latino
A-Z0-9, sin diferencia real de idioma, pero PaddleOCR suele tolerar mejor ruido/blur en video de
vigilancia. Costo real a tener en cuenta: ahora el proceso Edge carga DOS frameworks de ML
distintos en memoria (PyTorch vía ultralytics para el detector, PaddlePaddle vía PaddleOCR para
el texto) — en un Jetson Orin Nano esto es más RAM/disco que si ambas etapas compartieran
framework. Si la memoria es un problema en tu hardware real, considera volver a EasyOCR
(PyTorch, mismo framework que el detector) — la interfaz pública de esta clase (`PlateOcr.read`)
es idéntica para ambos, así que el resto del pipeline no cambiaría.

PIN DE VERSIÓN IMPORTANTE: requirements.txt fija `paddleocr<3` a propósito. PaddleOCR 3.x
reescribió buena parte de su API pública (nueva interfaz basada en `.predict()` en vez de
`.ocr()`, con un objeto de resultado distinto) — este módulo está escrito contra la API clásica
2.x (`PaddleOCR(use_angle_cls=True, lang=...)` + `.ocr(img, cls=True)`), que es la que llevo más
tiempo viendo documentada y estable. No se pudo verificar contra el paquete real instalado (sin
acceso a PyPI desde este entorno) — si `pip install` trae una 3.x de todos modos o la firma no
coincide, este es el primer lugar a revisar.
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Optional

import numpy as np
from paddleocr import PaddleOCR

_NORMALIZE_PATTERN = re.compile(r"[^A-Z0-9]")


@dataclass
class OcrResult:
    text: str
    confidence: float


def normalize_plate_text(raw_text: str) -> str:
    """Mayúsculas, sin espacios/guiones/ruido — mismo formato que las placas de prueba usadas en
    todo el proyecto (TEST1234, SIMHIT001, etc.)."""
    return _NORMALIZE_PATTERN.sub("", raw_text.upper())


class PlateOcr:
    def __init__(self, lang: str, min_confidence: float, plate_pattern: Optional[str] = None):
        # use_angle_cls=True: corrige texto rotado/inclinado — útil en placas fotografiadas en
        # ángulo desde una cámara de arco/avenida en vez de de frente.
        self._reader = PaddleOCR(use_angle_cls=True, lang=lang)
        self._min_confidence = min_confidence
        # Filtro OPCIONAL por formato de placa (regex). Deliberadamente apagado por default
        # (None): el formato nacional mexicano actual es 3 letras + 3 dígitos para autos
        # particulares desde la unificación 2022, pero circulan durante años placas del formato
        # anterior (varía por estado), motos, remolques y series federales/diplomáticas con otros
        # largos — un filtro estricto puede descartar lecturas reales y válidas. Actívalo en
        # config.yaml (ocr.plate_pattern) solo si te interesa reducir ruido de OCR a costa de
        # posibles falsos negativos en placas de formato atípico.
        self._plate_pattern = re.compile(plate_pattern) if plate_pattern else None

    def read(self, plate_crop: np.ndarray) -> Optional[OcrResult]:
        if plate_crop.size == 0:
            return None

        # API clásica de PaddleOCR 2.x: para una sola imagen, ocr_result es una lista de 1
        # elemento; ese elemento es None si no encontró texto, o una lista de
        # [bbox, (texto, confianza)] por cada línea de texto detectada en el recorte.
        ocr_result = self._reader.ocr(plate_crop, cls=True)
        lines = ocr_result[0] if ocr_result else None
        if not lines:
            return None

        # cada línea: [bbox, (texto, confianza)] — nos quedamos con la de mayor confianza
        _, (text, confidence) = max(lines, key=lambda line: line[1][1])

        normalized = normalize_plate_text(text)
        if not normalized or confidence < self._min_confidence:
            return None

        if self._plate_pattern is not None and not self._plate_pattern.match(normalized):
            return None

        return OcrResult(text=normalized, confidence=float(confidence))
