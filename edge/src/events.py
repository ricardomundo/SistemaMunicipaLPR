"""
Espejo en Python del record PlateReadEvent de Core.Contracts (C#). Los nombres de campo deben
coincidir EXACTO en casing (PascalCase) con las propiedades del record .NET, porque
Service.Inference deserializa con System.Text.Json SIN PropertyNameCaseInsensitive=true — el
comportamiento default de System.Text.Json es case-sensitive.

Ver Core.Contracts/PlateReadEvent.cs (backend .NET) como fuente de verdad del contrato.
"""
from __future__ import annotations

import json
import uuid
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from typing import Optional


@dataclass
class PlateReadEvent:
    EventId: str
    PlateText: str
    CameraId: str
    TimestampUtc: str
    Confidence: float
    ImageReference: Optional[str] = None

    @staticmethod
    def create(plate_text: str, camera_id: str, confidence: float, image_reference: Optional[str] = None) -> "PlateReadEvent":
        return PlateReadEvent(
            EventId=str(uuid.uuid4()),
            PlateText=plate_text,
            CameraId=camera_id,
            # ISO 8601 con offset UTC explícito — System.Text.Json lo parsea a DateTime sin
            # configuración adicional.
            TimestampUtc=datetime.now(timezone.utc).isoformat(),
            Confidence=confidence,
            ImageReference=image_reference,
        )

    def to_json_bytes(self) -> bytes:
        return json.dumps(asdict(self)).encode("utf-8")
