"""
Publish de PlateReadEvent directo a RabbitMQ vía pika — AMQP puro, sin pasar por DotNetCore.CAP
(igual que el lado .NET desde el cambio de diseño de Fase 3, ver ArchitectureGuide.md §3 e
ImplementersGuide.md §7). Publicar desde Python es justamente lo simple que queda gracias a ese
cambio: un JSON plano a una cola durable, sin envelope propio de CAP que replicar.

pika.BlockingConnection NO es segura para compartir entre threads. Cada hilo que publique debe
crear su propia instancia de RabbitMqPublisher (ver main.py: uno para el publish directo del loop
principal, otro para el hilo de drenado del buffer).
"""
from __future__ import annotations

import logging

import pika
from pika.exceptions import AMQPError

from .events import PlateReadEvent

logger = logging.getLogger(__name__)


class RabbitMqPublisher:
    def __init__(
        self,
        host: str,
        port: int,
        virtual_host: str,
        username: str,
        password: str,
        queue: str,
    ):
        self._params = pika.ConnectionParameters(
            host=host,
            port=port,
            virtual_host=virtual_host,
            credentials=pika.PlainCredentials(username, password),
        )
        self._queue = queue
        self._connection: pika.BlockingConnection | None = None
        self._channel = None

    def _ensure_connected(self) -> None:
        if self._connection is not None and self._connection.is_open:
            return

        logger.info("Conectando a RabbitMQ...")
        self._connection = pika.BlockingConnection(self._params)
        self._channel = self._connection.channel()
        # durable=True debe coincidir con la declaración del lado .NET (Core.Contracts/RawQueues)
        # — declarar la misma cola con parámetros distintos desde dos clientes causa un error de
        # PRECONDITION_FAILED en RabbitMQ.
        self._channel.queue_declare(queue=self._queue, durable=True)

    def publish_bytes(self, body: bytes) -> bool:
        """Intenta publicar. Devuelve False (sin lanzar excepción) si RabbitMQ no está
        alcanzable — el llamador decide qué hacer (normalmente: guardar en el buffer local)."""
        try:
            self._ensure_connected()
            self._channel.basic_publish(
                exchange="",
                routing_key=self._queue,
                body=body,
                properties=pika.BasicProperties(delivery_mode=2),  # 2 = persistent (spec AMQP)
            )
            return True
        except AMQPError as ex:
            logger.warning("Publish a RabbitMQ falló (%s) — se reintentará vía buffer local.", ex)
            self._connection = None
            self._channel = None
            return False

    def publish_event(self, event: PlateReadEvent) -> bool:
        return self.publish_bytes(event.to_json_bytes())

    def close(self) -> None:
        if self._connection is not None and self._connection.is_open:
            self._connection.close()
