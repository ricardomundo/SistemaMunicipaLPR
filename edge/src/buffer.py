"""
Buffer local en SQLite para tolerancia a cortes de red (ArchitectureGuide.md §5, "Plan de
contingencia y tolerancia a fallos" — fila "Pérdida de conexión a Internet/red municipal en un
arco"). Si el publish directo a RabbitMQ falla, el evento se guarda aquí en vez de perderse;
un hilo de fondo (ver main.py) reintenta drenarlo en orden cuando la conexión vuelve.
"""
from __future__ import annotations

import sqlite3
import threading
from dataclasses import dataclass


@dataclass
class BufferedEvent:
    id: int
    event_json: bytes


class EventBuffer:
    """Cada hilo que toca SQLite necesita su propia conexión — sqlite3 no es segura para compartir
    conexiones entre threads por default. Este wrapper abre una conexión propia por instancia; el
    hilo de publish directo y el hilo de drenado deben crear cada uno la suya sobre el mismo
    archivo (SQLite maneja bien el acceso concurrente de varios procesos/conexiones al mismo
    archivo)."""

    def __init__(self, sqlite_path: str):
        self._path = sqlite_path
        self._lock = threading.Lock()
        self._init_schema()

    def _connect(self) -> sqlite3.Connection:
        return sqlite3.connect(self._path, timeout=10)

    def _init_schema(self) -> None:
        with self._connect() as conn:
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS pending_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_json BLOB NOT NULL,
                    created_at_utc TEXT NOT NULL DEFAULT (datetime('now'))
                )
                """
            )

    def enqueue(self, event_json: bytes) -> None:
        with self._lock, self._connect() as conn:
            conn.execute("INSERT INTO pending_events (event_json) VALUES (?)", (event_json,))

    def dequeue_batch(self, max_batch: int) -> list[BufferedEvent]:
        with self._lock, self._connect() as conn:
            rows = conn.execute(
                "SELECT id, event_json FROM pending_events ORDER BY id ASC LIMIT ?", (max_batch,)
            ).fetchall()
        return [BufferedEvent(id=row[0], event_json=row[1]) for row in rows]

    def remove(self, event_id: int) -> None:
        with self._lock, self._connect() as conn:
            conn.execute("DELETE FROM pending_events WHERE id = ?", (event_id,))

    def pending_count(self) -> int:
        with self._lock, self._connect() as conn:
            (count,) = conn.execute("SELECT COUNT(*) FROM pending_events").fetchone()
        return count
