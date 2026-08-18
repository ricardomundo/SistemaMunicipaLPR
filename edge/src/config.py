"""
Carga y valida config.yaml (ver config.example.yaml para el formato y comentarios de cada
campo). Sin dependencias del resto del backend .NET — este pipeline corre como proceso Python
independiente en el nodo Edge (Jetson u otro PC junto a la cámara).
"""
from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Optional

import yaml


@dataclass
class CameraConfig:
    id: str
    source: Any  # int (índice de dispositivo) o str (URL RTSP)
    frame_skip: int
    burst_size: int
    plate_cooldown_seconds: float


@dataclass
class DetectorConfig:
    model_path: str
    min_confidence: float
    crop_padding: int = 0


@dataclass
class OcrConfig:
    min_confidence: float
    lang: str = "en"
    plate_pattern: Optional[str] = None  # regex opcional — ver la nota en src/ocr.py antes de activarlo


@dataclass
class RabbitMqConfig:
    host: str
    port: int
    virtual_host: str
    username: str
    password: str
    queue: str
    reconnect_seconds: float


@dataclass
class BufferConfig:
    sqlite_path: str
    flush_interval_seconds: float
    max_batch_per_flush: int


@dataclass
class ImagesConfig:
    save_dir: str


@dataclass
class AppConfig:
    camera: CameraConfig
    detector: DetectorConfig
    ocr: OcrConfig
    rabbitmq: RabbitMqConfig
    buffer: BufferConfig
    images: ImagesConfig


def load_config(path: str) -> AppConfig:
    config_path = Path(path)
    if not config_path.exists():
        print(
            f"ERROR: no se encontró '{path}'. Copia config.example.yaml a config.yaml y "
            "ajústalo antes de correr el pipeline.",
            file=sys.stderr,
        )
        sys.exit(1)

    with config_path.open("r", encoding="utf-8") as f:
        raw = yaml.safe_load(f)

    try:
        cfg = AppConfig(
            camera=CameraConfig(**raw["camera"]),
            detector=DetectorConfig(**raw["detector"]),
            ocr=OcrConfig(**raw["ocr"]),
            rabbitmq=RabbitMqConfig(**raw["rabbitmq"]),
            buffer=BufferConfig(**raw["buffer"]),
            images=ImagesConfig(**raw["images"]),
        )
    except (KeyError, TypeError) as ex:
        print(f"ERROR: config.yaml incompleto o mal formado — falta o sobra un campo: {ex}", file=sys.stderr)
        sys.exit(1)

    if not Path(cfg.detector.model_path).exists():
        print(
            f"ERROR: no se encontró el modelo de detección de placas en '{cfg.detector.model_path}'. "
            "Un YOLO preentrenado en COCO no sirve aquí — hace falta un modelo entrenado "
            "específicamente para detectar placas. Ver la nota en config.example.yaml e "
            "ImplementersGuide.md §10.",
            file=sys.stderr,
        )
        sys.exit(1)

    return cfg
